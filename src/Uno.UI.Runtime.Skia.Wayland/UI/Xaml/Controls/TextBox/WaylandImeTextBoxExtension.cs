// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using Microsoft.UI.Xaml.Controls;
using Uno.UI.Hosting;
using Uno.UI.Xaml.Controls.Extensions;
using Uno.WinUI.Runtime.Skia.Wayland.Input;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// zwp_text_input_v3 implementation of <see cref="IImeTextBoxExtension"/>. The compositor routes
/// enabled text input to its input method; keys the input method does not consume still arrive
/// through wl_keyboard, so keyboard text and committed text are never both delivered.
/// </summary>
internal sealed class WaylandImeTextBoxExtension : IImeTextBoxExtension
{
	private const int MaximumCommittedStates = 64;
	private const uint ChangeCauseInputMethod = 0;
	private const uint ChangeCauseOther = 1;

	private readonly IWaylandTextInput? _textInput;
	private readonly Action<Action> _postToUI;

	// Under _stateLock: double-buffered state, applied at done.
	private readonly WaylandTextInputBatch _pending = new();

	// Under _stateLock (the display's SyncRoot): the state of each commit, keyed by its serial, so a done
	// applies to the session and surrounding text it was computed for.
	private readonly object _stateLock;
	private readonly WaylandTextInputStateHistory<TextBox> _committedStates = new(MaximumCommittedStates);
	private uint _commitSerial;
	private TextBox? _sessionTextBox;
	private long _session;

	// UI thread.
	private WlSurface? _focusedSurface;
	private TextBox? _textBox;
	private WlSurface? _textBoxSurface;
	private long _readOnlyToken;
	private long _enabledToken;
	private long _textToken;
	private long _predictionToken;
	private long _spellingToken;
	private long _inputScopeToken;
	private long _acceptsReturnToken;
	private long _textRevision;
	private string _revisionText = string.Empty;
	private bool _enabled;
	private bool _supportsSurrounding;
	private bool _isComposing;
	private bool _applyingInputMethodChange;
	// State requests wait for a done that matches the last commit.
	private bool _deferState;
	private bool _stateDirty;
	private WaylandTextInputText.SurroundingText? _surroundingText;
	private (WaylandTextInputText.SurroundingText Surrounding, string Text, (int X, int Y, int Width, int Height)? Caret, (uint Hint, uint Purpose) Content)? _lastSentState;

	internal WaylandImeTextBoxExtension(IWaylandTextInput? textInput, object syncRoot, Action<Action> postToUI, Action? onTextInput = null)
	{
		_stateLock = syncRoot;
		_postToUI = postToUI;
		_textInput = textInput;
		if (textInput is null)
		{
			return;
		}
		lock (syncRoot)
		{
			textInput.Enter += (_, e) => _postToUI(() => OnEnter(e.Surface));
			textInput.Leave += (_, e) => _postToUI(() => OnLeave(e.Surface));
			textInput.PreeditString += (_, e) =>
			{
				if (!string.IsNullOrEmpty(e.Text))
				{
					onTextInput?.Invoke();
				}
				_pending.Preedit = e.Text;
				_pending.PreeditCursor = e.CursorBegin;
			};
			textInput.CommitString += (_, e) =>
			{
				if (!string.IsNullOrEmpty(e.Text))
				{
					onTextInput?.Invoke();
				}
				_pending.Commit = e.Text;
			};
			textInput.DeleteSurroundingText += (_, e) =>
			{
				_pending.DeleteBefore = e.BeforeLength;
				_pending.DeleteAfter = e.AfterLength;
			};
			textInput.Done += OnDone;
		}
	}

	// Without text-input-v3, the same focused session receives complete keyboard Compose strings.
	internal static WaylandImeTextBoxExtension? TryCreate(WaylandConnection connection)
	{
		lock (connection.Display.SyncRoot)
		{
			return connection.TextInputManager is { } manager && connection.Seat is { } seat
				? new WaylandImeTextBoxExtension(new WaylandTextInput(manager.GetTextInput(seat)), connection.Display.SyncRoot, connection.PostToUI, connection.NotifyTextInputReceived)
				: new WaylandImeTextBoxExtension(null, connection.Display.SyncRoot, connection.PostToUI);
		}
	}

	public bool IsComposing => _isComposing;

	public event EventHandler? CompositionStarted;
	public event EventHandler<ImeCompositionEventArgs>? CompositionUpdated;
	public event EventHandler<ImeCompositionEventArgs>? CompositionCompleted;
	public event EventHandler? CompositionEnded;

	public void StartImeSession(TextBox textBox)
	{
		if (_textBox is not null)
		{
			EndImeSession();
		}
		_textBox = textBox;
		_revisionText = textBox.Text ?? string.Empty;
		_textToken = textBox.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => UpdateTextRevision(textBox));
		_readOnlyToken = textBox.RegisterPropertyChangedCallback(TextBox.IsReadOnlyProperty, (_, _) => UpdateState(textBox));
		_enabledToken = textBox.RegisterPropertyChangedCallback(TextBox.IsEnabledProperty, (_, _) => UpdateState(textBox));
		_predictionToken = textBox.RegisterPropertyChangedCallback(TextBox.IsTextPredictionEnabledProperty, (_, _) => UpdateContentState(textBox));
		_spellingToken = textBox.RegisterPropertyChangedCallback(TextBox.IsSpellCheckEnabledProperty, (_, _) => UpdateContentState(textBox));
		_inputScopeToken = textBox.RegisterPropertyChangedCallback(TextBox.InputScopeProperty, (_, _) => UpdateContentState(textBox));
		_acceptsReturnToken = textBox.RegisterPropertyChangedCallback(TextBox.AcceptsReturnProperty, (_, _) => UpdateContentState(textBox));
		_textBoxSurface = textBox.XamlRoot is { } xamlRoot && XamlRootMap.GetHostForRoot(xamlRoot) is WaylandXamlRootHost host ? host.Surface : null;
		_isComposing = false;
		if (_textBoxSurface is not null && _textBoxSurface == _focusedSurface)
		{
			Enable();
		}
	}

	public void EndImeSession()
	{
		if (_isComposing)
		{
			// Losing focus keeps the composed text, as on WinUI.
			_isComposing = false;
			CompositionEnded?.Invoke(this, EventArgs.Empty);
		}
		Disable();
		if (_textBox is { } textBox)
		{
			textBox.UnregisterPropertyChangedCallback(TextBox.TextProperty, _textToken);
			textBox.UnregisterPropertyChangedCallback(TextBox.IsReadOnlyProperty, _readOnlyToken);
			textBox.UnregisterPropertyChangedCallback(TextBox.IsEnabledProperty, _enabledToken);
			textBox.UnregisterPropertyChangedCallback(TextBox.IsTextPredictionEnabledProperty, _predictionToken);
			textBox.UnregisterPropertyChangedCallback(TextBox.IsSpellCheckEnabledProperty, _spellingToken);
			textBox.UnregisterPropertyChangedCallback(TextBox.InputScopeProperty, _inputScopeToken);
			textBox.UnregisterPropertyChangedCallback(TextBox.AcceptsReturnProperty, _acceptsReturnToken);
		}
		_textBox = null;
		_textBoxSurface = null;
		_surroundingText = null;
	}

	private void UpdateContentState(TextBox host)
	{
		if (host != _textBox)
		{
			return;
		}
		if (_applyingInputMethodChange)
		{
			// Publish after composition callbacks finish, when the surrounding text is consistent.
			_stateDirty = true;
		}
		else
		{
			UpdateState(host);
		}
	}

	// Sends the surrounding text and caret rectangle of the focused TextBox after it changed.
	internal void UpdateState(TextBox host)
	{
		if (host != _textBox || _applyingInputMethodChange)
		{
			return;
		}
		UpdateTextRevision(host);
		if (_isComposing && !host.IsComposing)
		{
			// An external edit canceled the host's preedit; its span no longer belongs to this session.
			Disable();
			if (_textBoxSurface == _focusedSurface && _focusedSurface is not null)
			{
				Enable();
			}
			return;
		}
		if (host.IsReadOnly || !host.IsEnabled)
		{
			Disable();
			return;
		}
		if (!_enabled && _textBoxSurface == _focusedSurface && _focusedSurface is not null)
		{
			Enable();
		}
		else
		{
			SendState(host, ChangeCauseOther);
		}
	}

	private void CommitState()
	{
		var state = _enabled && _textBox is { } host
			? new WaylandTextInputStateHistory<TextBox>.State(_session, host, _supportsSurrounding ? _surroundingText : null, GetTextWithoutPreedit(host), _textRevision)
			: (WaylandTextInputStateHistory<TextBox>.State?)null;
		lock (_stateLock)
		{
			_commitSerial = unchecked(_commitSerial + 1);
			if (state is { } committedState)
			{
				_committedStates.Record(_commitSerial, committedState);
			}
		}
		_textInput?.Commit();
	}

	private void Disable()
	{
		lock (_stateLock)
		{
			_session++;
			_sessionTextBox = null;
			_committedStates.Clear();
			_pending.Disable();
		}
		if (_isComposing)
		{
			_isComposing = false;
			CompositionEnded?.Invoke(this, EventArgs.Empty);
		}
		_deferState = _stateDirty = false;
		_lastSentState = null;
		if (_enabled)
		{
			_enabled = false;
			_textInput?.Disable();
			CommitState();
			WaylandDiagnostics.Trace("text-input: disabled");
		}
	}

	private void SendState(TextBox host, uint cause)
	{
		if (!_enabled || host != _textBox)
		{
			return;
		}

		if (_deferState)
		{
			_stateDirty = true;
			return;
		}
		var surrounding = CalculateSurrounding(host);
		if (_supportsSurrounding && !surrounding.IsSupported)
		{
			// Support is fixed for an enable/disable context; reset it rather than send a false anchor.
			Disable();
			Enable();
			return;
		}
		var caretRectangle = GetCaretRectangle(host);
		var content = WaylandTextInputContent.Get(
			host.InputScope?.Names.Count > 0 ? host.InputScope.Names[0].NameValue : Microsoft.UI.Xaml.Input.InputScopeNameValue.Default,
			host.IsTextPredictionEnabled, host.IsSpellCheckEnabled, host.AcceptsReturn);
		var state = (surrounding, GetTextWithoutPreedit(host), caretRectangle, content);
		if (_lastSentState == state)
		{
			_stateDirty = false;
			return;
		}
		_textInput?.SetTextChangeCause(cause);
		_textInput?.SetContentType(content.Hint, content.Purpose);
		_surroundingText = surrounding;
		if (_supportsSurrounding)
		{
			_textInput?.SetSurroundingText(surrounding.Text, surrounding.Cursor, surrounding.Anchor);
		}

		if (caretRectangle is { } caret)
		{
			_textInput?.SetCursorRectangle(caret.X, caret.Y, caret.Width, caret.Height);
		}
		CommitState();
		_lastSentState = state;
		_stateDirty = false;
	}

	private void Enable()
	{
		if (_textBox is not { IsReadOnly: false, IsEnabled: true } host || _textInput is null)
		{
			return;
		}
		_supportsSurrounding = CalculateSurrounding(host).IsSupported;
		// Enabling again resets the input method state for the newly focused TextBox.
		_enabled = true;
		lock (_stateLock)
		{
			_pending.Begin(unchecked(_commitSerial + 1));
			_sessionTextBox = host;
		}
		_lastSentState = null;
		_textInput?.Enable();
		WaylandDiagnostics.Trace("text-input: enabled");
		SendState(host, ChangeCauseOther);
	}

	// Old batches still belong to this focus session, but their deletion offsets are stale.
	private void ResynchronizeState(TextBox host)
	{
		var supportsSurrounding = CalculateSurrounding(host).IsSupported;
		lock (_stateLock)
		{
			_committedStates.Clear();
			// Reserve the disable commit before publishing the new surrounding-text boundary.
			_commitSerial = unchecked(_commitSerial + 1);
			_pending.Resynchronize(unchecked(_commitSerial + 1));
		}
		_deferState = _stateDirty = false;
		_lastSentState = null;
		_enabled = false;
		_textInput?.Disable();
		_textInput?.Commit();
		_supportsSurrounding = supportsSurrounding;
		_enabled = true;
		_textInput?.Enable();
		SendState(host, ChangeCauseOther);
	}

	private void OnEnter(WlSurface surface)
	{
		_focusedSurface = surface;
		WaylandDiagnostics.Trace("text-input: enter");
		if (_textBoxSurface == surface)
		{
			Enable();
		}
	}

	private void OnLeave(WlSurface surface)
	{
		if (_focusedSurface != surface)
		{
			return;
		}
		WaylandDiagnostics.Trace("text-input: leave");
		_focusedSurface = null;
		// Leave ends the enabled state; the compositor ignores requests until the next enter.
		_enabled = false;
		OnPreeditChanged(null, 0);
		Disable();
		_surroundingText = null;
	}

	// Dispatch thread.
	private void OnDone(object? sender, ZwpTextInputV3.DoneEventArgs args)
	{
		Action? apply = null;
		lock (_stateLock)
		{
			var batch = _pending.Take(args.Serial, _commitSerial);
			if (batch.Disposition != WaylandTextInputBatchDisposition.Reject && _sessionTextBox is { } host)
			{
				var state = _committedStates.GetStateOrCurrent(args.Serial, _session, host);
				apply = () => Apply(args.Serial, state.Session, state.Host, state.Surrounding, state.Text, state.Revision,
					batch.Preedit, batch.PreeditCursor, batch.Commit, batch.DeleteBefore, batch.DeleteAfter);
			}
		}
		if (apply is not null)
		{
			_postToUI(apply);
		}
	}

	// Applies a done event in the order the protocol specifies: delete surrounding text (dropping the preedit
	// first), insert the commit string, then show the new preedit. A preedit update alone keeps the composition.
	private void Apply(uint serial, long session, TextBox host, WaylandTextInputText.SurroundingText? surrounding, string text, long revision, string? preedit, int preeditCursor, string? commit, uint deleteBefore, uint deleteAfter)
	{
		if (!_enabled || session != _session || host != _textBox || (host.IsReadOnly || !host.IsEnabled))
		{
			return;
		}
		bool oldContext;
		lock (_stateLock)
		{
			// A resync after done was posted also makes this batch's deletion stale.
			oldContext = _pending.GetDisposition(serial, _commitSerial) == WaylandTextInputBatchDisposition.ApplyWithoutDeletion;
			if (oldContext)
			{
				deleteBefore = deleteAfter = 0;
			}
			if (_committedStates.TryGetValue(serial, out var state))
			{
				surrounding = state.Surrounding;
				text = state.Text;
				revision = state.Revision;
			}
		}
		var deleting = deleteBefore != 0 || deleteAfter != 0;
		var reconcileDeletion = deleting && (surrounding is not { IsSupported: true } || revision != _textRevision || text != GetTextWithoutPreedit(host));
		var wasComposing = _isComposing;
		_applyingInputMethodChange = true;
		try
		{
			if (reconcileDeletion)
			{
				// Only deletion needs the old surrounding-text snapshot; commit and preedit still apply.
				WaylandDiagnostics.Trace("text-input: stale deletion, resetting");
				deleting = false;
				OnPreeditChanged(null, 0);
				ResynchronizeState(host);
			}
			_deferState = !oldContext && !reconcileDeletion && serial != _commitSerial;
			if (deleting && _isComposing)
			{
				OnPreeditChanged(null, 0);
			}
			if (deleting && surrounding is { IsSupported: true } snapshot)
			{
				DeleteSurroundingText(host, snapshot, deleteBefore, deleteAfter);
			}
			if (commit is not null)
			{
				OnCommittedText(commit);
			}
			// A new preedit replaces the selection; the surrounding text excludes it and keeps the caret at its start.
			OnPreeditChanged(preedit, preeditCursor);
			_surroundingText = CalculateSurrounding(host);
		}
		finally
		{
			_revisionText = host.Text ?? string.Empty;
			_applyingInputMethodChange = false;
		}
		var currentText = GetTextWithoutPreedit(host);
		lock (_stateLock)
		{
			if (serial == _commitSerial)
			{
				_committedStates.RetainOnly(serial);
			}
			if (_committedStates.TryGetValue(serial, out _))
			{
				_committedStates.Record(serial, new(session, host, _supportsSurrounding ? _surroundingText : null, currentText, _textRevision));
			}
		}
		// A preedit-only update leaves the surrounding text unchanged; its start and end move the caret.
		if (deleting || commit is not null || wasComposing != _isComposing || _stateDirty)
		{
			SendState(host, ChangeCauseInputMethod);
		}
	}

	private void UpdateTextRevision(TextBox host)
	{
		var text = host.Text ?? string.Empty;
		if (!_applyingInputMethodChange && text != _revisionText)
		{
			_revisionText = text;
			_textRevision++;
		}
	}

	private string GetTextWithoutPreedit(TextBox textBox)
	{
		var text = textBox.Text ?? string.Empty;
		return _isComposing ? text.Remove(textBox.CompositionStartIndex, textBox.CompositionLength) : text;
	}

	private WaylandTextInputText.SurroundingText CalculateSurrounding(TextBox textBox)
	{
		var text = textBox.Text ?? string.Empty;
		if (_isComposing)
		{
			var start = textBox.CompositionStartIndex;
			text = text.Remove(start, textBox.CompositionLength);
			return WaylandTextInputText.GetSurroundingText(text, start, start);
		}
		var end = textBox.SelectionStart + textBox.SelectionLength;
		var (cursor, anchor) = textBox.IsBackwardSelection ? (textBox.SelectionStart, end) : (end, textBox.SelectionStart);
		return WaylandTextInputText.GetSurroundingText(text, cursor, anchor);
	}

	private static void DeleteSurroundingText(TextBox textBox, WaylandTextInputText.SurroundingText surrounding, uint beforeLength, uint afterLength)
	{
		var (before, after) = WaylandTextInputText.GetDeletionRanges(surrounding, beforeLength, afterLength);
		var text = textBox.Text ?? string.Empty;
		if (textBox.IsReadOnly || !textBox.IsEnabled || after.Start + after.Length > text.Length)
		{
			return;
		}
		var selectionLength = after.Start - before.Start - before.Length;
		textBox.Text = text.Remove(after.Start, after.Length).Remove(before.Start, before.Length);
		textBox.Select(before.Start, selectionLength);
	}

	internal bool TryCommitKeyboardText(WaylandKeyboardInputSource source, string text)
	{
		if (_textBox is not { IsReadOnly: false, IsEnabled: true } textBox
			|| textBox.XamlRoot is not { } root
			|| XamlRootMap.GetHostForRoot(root) is not WaylandXamlRootHost rootHost
			|| rootHost.KeyboardSource != source)
		{
			return false;
		}
		_applyingInputMethodChange = true;
		try
		{
			OnCommittedText(text);
			_surroundingText = CalculateSurrounding(textBox);
		}
		finally
		{
			_revisionText = textBox.Text ?? string.Empty;
			_applyingInputMethodChange = false;
		}
		SendState(textBox, ChangeCauseOther);
		return true;
	}

	private void OnCommittedText(string text)
	{
		if (!_isComposing)
		{
			CompositionStarted?.Invoke(this, EventArgs.Empty);
		}
		CompositionCompleted?.Invoke(this, new ImeCompositionEventArgs(text));
		_isComposing = false;
		CompositionEnded?.Invoke(this, EventArgs.Empty);
	}

	// Uno's composition has a caret but no selection, as on macOS: a preedit selection shows its start
	// and a hidden cursor (-1) the end of the preedit.
	private void OnPreeditChanged(string? text, int cursorBegin)
	{
		if (!string.IsNullOrEmpty(text))
		{
			if (!_isComposing)
			{
				_isComposing = true;
				CompositionStarted?.Invoke(this, EventArgs.Empty);
			}
			CompositionUpdated?.Invoke(this, new ImeCompositionEventArgs(text, cursorBegin < 0 ? -1 : WaylandTextInputText.ByteOffsetToCharIndex(text, cursorBegin)));
		}
		else if (_isComposing)
		{
			// The input method cleared the preedit without committing it.
			CompositionCompleted?.Invoke(this, new ImeCompositionEventArgs(string.Empty));
			_isComposing = false;
			CompositionEnded?.Invoke(this, EventArgs.Empty);
		}
	}

	// Surface-local logical coordinates, which are the XamlRoot's coordinates.
	private static (int X, int Y, int Width, int Height)? GetCaretRectangle(TextBox textBox)
	{
		if (textBox.TextBoxView?.DisplayBlock is not { ParsedText: { } parsedText } displayBlock)
		{
			return null;
		}
		var index = textBox.IsBackwardSelection ? textBox.SelectionStart : textBox.SelectionStart + textBox.SelectionLength;
		var rect = parsedText.GetRectForIndex(index);
		var topLeft = displayBlock.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(rect.Left, rect.Top));
		return ((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y), 1, Math.Max(1, (int)Math.Ceiling(rect.Height)));
	}
}
