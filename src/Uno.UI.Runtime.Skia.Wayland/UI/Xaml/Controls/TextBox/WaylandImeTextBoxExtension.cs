// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Uno.UI.Hosting;
using Uno.UI.Xaml.Controls.Extensions;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// zwp_text_input_v3 implementation of <see cref="IImeTextBoxExtension"/>. The compositor routes
/// enabled text input to its input method; keys the input method does not consume still arrive
/// through wl_keyboard, so keyboard text and committed text are never both delivered.
/// </summary>
internal sealed class WaylandImeTextBoxExtension : IImeTextBoxExtension
{
	private const uint ChangeCauseInputMethod = 0;
	private const uint ChangeCauseOther = 1;

	private readonly IWaylandTextInput _textInput;

	// Dispatch thread: double-buffered state, applied at done.
	private string? _pendingPreedit;
	private int _pendingPreeditCursor;
	private string? _pendingCommit;
	private uint _pendingDeleteBefore;
	private uint _pendingDeleteAfter;

	// Under _stateLock (the display's SyncRoot): the state of each commit, keyed by its serial, so a done
	// applies to the session and surrounding text it was computed for.
	private readonly object _stateLock;
	private readonly Dictionary<uint, (long Session, TextBox TextBox, WaylandTextInputText.SurroundingText? Surrounding)> _committedStates = new();
	private uint _commitSerial;
	private long _session;

	// UI thread.
	private WlSurface? _focusedSurface;
	private TextBox? _textBox;
	private WlSurface? _textBoxSurface;
	private long _readOnlyToken;
	private long _enabledToken;
	private bool _enabled;
	private bool _supportsSurrounding;
	private bool _isComposing;
	private bool _applyingInputMethodChange;
	// State requests wait for a done that matches the last commit.
	private bool _deferState;
	private bool _stateDirty;
	private WaylandTextInputText.SurroundingText? _surroundingText;

	internal WaylandImeTextBoxExtension(IWaylandTextInput textInput, object syncRoot)
	{
		_stateLock = syncRoot;
		lock (syncRoot)
		{
			_textInput = textInput;
			_textInput.Enter += (_, e) => WaylandConnection.PostToUI(() => OnEnter(e.Surface));
			_textInput.Leave += (_, e) => WaylandConnection.PostToUI(() => OnLeave(e.Surface));
			_textInput.PreeditString += (_, e) =>
			{
				_pendingPreedit = e.Text;
				_pendingPreeditCursor = e.CursorBegin;
			};
			_textInput.CommitString += (_, e) => _pendingCommit = e.Text;
			_textInput.DeleteSurroundingText += (_, e) =>
			{
				_pendingDeleteBefore = e.BeforeLength;
				_pendingDeleteAfter = e.AfterLength;
			};
			_textInput.Done += OnDone;
		}
	}

	internal static WaylandImeTextBoxExtension? TryCreate(WaylandConnection connection)
	{
		lock (connection.Display.SyncRoot)
		{
			return connection.TextInputManager is { } manager && connection.Seat is { } seat
				? new WaylandImeTextBoxExtension(new WaylandTextInput(manager.GetTextInput(seat)), connection.Display.SyncRoot)
				: null;
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
		_readOnlyToken = textBox.RegisterPropertyChangedCallback(TextBox.IsReadOnlyProperty, (_, _) => UpdateState(textBox));
		_enabledToken = textBox.RegisterPropertyChangedCallback(TextBox.IsEnabledProperty, (_, _) => UpdateState(textBox));
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
			textBox.UnregisterPropertyChangedCallback(TextBox.IsReadOnlyProperty, _readOnlyToken);
			textBox.UnregisterPropertyChangedCallback(TextBox.IsEnabledProperty, _enabledToken);
		}
		_textBox = null;
		_textBoxSurface = null;
		_surroundingText = null;
	}

	// Sends the surrounding text and caret rectangle of the focused TextBox after it changed.
	internal void UpdateState(TextBox textBox)
	{
		if (textBox != _textBox || _applyingInputMethodChange)
		{
			return;
		}
		if (textBox.IsReadOnly || !textBox.IsEnabled)
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
			SendState(textBox, ChangeCauseOther);
		}
	}

	private void CommitState()
	{
		lock (_stateLock)
		{
			_textInput.Commit();
			_commitSerial = unchecked(_commitSerial + 1);
			if (_enabled && _textBox is { } textBox)
			{
				_committedStates[_commitSerial] = (_session, textBox, _supportsSurrounding ? _surroundingText : null);
			}
		}
	}

	private void Disable()
	{
		lock (_stateLock)
		{
			_session++;
			_committedStates.Clear();
			ResetPending();
		}
		if (_isComposing)
		{
			_isComposing = false;
			CompositionEnded?.Invoke(this, EventArgs.Empty);
		}
		_deferState = _stateDirty = false;
		if (_enabled)
		{
			_enabled = false;
			_textInput.Disable();
			CommitState();
			WaylandDiagnostics.Trace("text-input: disabled");
		}
	}

	private void SendState(TextBox textBox, uint cause)
	{
		if (!_enabled || textBox != _textBox)
		{
			return;
		}

		if (_deferState)
		{
			_stateDirty = true;
			return;
		}
		_textInput.SetTextChangeCause(cause);
		var surrounding = CalculateSurrounding(textBox);
		if (_supportsSurrounding && !surrounding.IsSupported)
		{
			// Support is fixed for an enable/disable context; reset it rather than send a false anchor.
			Disable();
			Enable();
			return;
		}
		_surroundingText = surrounding;
		if (_supportsSurrounding)
		{
			_textInput.SetSurroundingText(surrounding.Text, surrounding.Cursor, surrounding.Anchor);
		}

		if (GetCaretRectangle(textBox) is { } caret)
		{
			_textInput.SetCursorRectangle(caret.X, caret.Y, caret.Width, caret.Height);
		}
		CommitState();
		_stateDirty = false;
	}

	private void Enable()
	{
		if (_textBox is not { IsReadOnly: false, IsEnabled: true } textBox)
		{
			return;
		}
		_supportsSurrounding = CalculateSurrounding(textBox).IsSupported;
		// Enabling again resets the input method state for the newly focused TextBox.
		_enabled = true;
		_textInput.Enable();
		_textInput.SetContentType((uint)ZwpTextInputV3.ContentHintValues.None, (uint)ZwpTextInputV3.ContentPurposeValues.Normal);
		WaylandDiagnostics.Trace("text-input: enabled");
		SendState(textBox, ChangeCauseOther);
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
		var preedit = _pendingPreedit;
		var preeditCursor = _pendingPreeditCursor;
		var commit = _pendingCommit;
		var deleteBefore = _pendingDeleteBefore;
		var deleteAfter = _pendingDeleteAfter;
		ResetPending();
		lock (_stateLock)
		{
			if (_committedStates.TryGetValue(args.Serial, out var state))
			{
				WaylandConnection.PostToUI(() => Apply(args.Serial, state.Session, state.TextBox, state.Surrounding, preedit, preeditCursor, commit, deleteBefore, deleteAfter));
			}
		}
	}

	private void ResetPending()
	{
		_pendingPreedit = null;
		_pendingPreeditCursor = 0;
		_pendingCommit = null;
		_pendingDeleteBefore = 0;
		_pendingDeleteAfter = 0;
	}

	// Applies a done event in the order the protocol specifies: delete surrounding text (dropping the preedit
	// first), insert the commit string, then show the new preedit. A preedit update alone keeps the composition.
	private void Apply(uint serial, long session, TextBox textBox, WaylandTextInputText.SurroundingText? surrounding, string? preedit, int preeditCursor, string? commit, uint deleteBefore, uint deleteAfter)
	{
		if (!_enabled || session != _session || textBox != _textBox || textBox.IsReadOnly || !textBox.IsEnabled)
		{
			return;
		}
		lock (_stateLock)
		{
			if (_committedStates.TryGetValue(serial, out var state))
			{
				surrounding = state.Surrounding;
			}
		}
		_deferState = serial != _commitSerial;
		var deleting = deleteBefore != 0 || deleteAfter != 0;
		var wasComposing = _isComposing;
		_applyingInputMethodChange = true;
		try
		{
			if (deleting && _isComposing)
			{
				OnPreeditChanged(null, 0);
			}
			if (deleting && surrounding is { IsSupported: true } snapshot)
			{
				DeleteSurroundingText(textBox, snapshot, deleteBefore, deleteAfter);
			}
			if (commit is not null)
			{
				OnCommittedText(commit);
			}
			// A new preedit replaces the selection; the surrounding text excludes it and keeps the caret at its start.
			OnPreeditChanged(preedit, preeditCursor);
			_surroundingText = CalculateSurrounding(textBox);
		}
		finally
		{
			_applyingInputMethodChange = false;
		}
		lock (_stateLock)
		{
			if (serial == _commitSerial)
			{
				foreach (var previous in new List<uint>(_committedStates.Keys))
				{
					if (previous != serial)
					{
						_committedStates.Remove(previous);
					}
				}
			}
			if (_committedStates.ContainsKey(serial))
			{
				_committedStates[serial] = (session, textBox, _supportsSurrounding ? _surroundingText : null);
			}
		}
		// A preedit-only update leaves the surrounding text unchanged; its start and end move the caret.
		if (deleting || commit is not null || wasComposing != _isComposing || _stateDirty)
		{
			SendState(textBox, ChangeCauseInputMethod);
		}
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
