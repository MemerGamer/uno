// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
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

	private readonly ZwpTextInputV3 _textInput;

	// Dispatch thread: double-buffered state, applied at done.
	private string? _pendingPreedit;
	private int _pendingPreeditCursor;
	private string? _pendingCommit;
	private uint _pendingDeleteBefore;
	private uint _pendingDeleteAfter;

	// UI thread.
	private WlSurface? _focusedSurface;
	private TextBox? _textBox;
	private WlSurface? _textBoxSurface;
	private bool _enabled;
	private bool _isComposing;
	private bool _applyingInputMethodChange;
	private WaylandTextInputText.SurroundingText? _surroundingText;

	private WaylandImeTextBoxExtension(WaylandConnection connection, ZwpTextInputManagerV3 manager, WlSeat seat)
	{
		lock (connection.Display.SyncRoot)
		{
			_textInput = manager.GetTextInput(seat);
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
		=> connection.TextInputManager is { } manager && connection.Seat is { } seat
			? new WaylandImeTextBoxExtension(connection, manager, seat)
			: null;

	public bool IsComposing => _isComposing;

	public event EventHandler? CompositionStarted;
	public event EventHandler<ImeCompositionEventArgs>? CompositionUpdated;
	public event EventHandler<ImeCompositionEventArgs>? CompositionCompleted;
	public event EventHandler? CompositionEnded;

	public void StartImeSession(TextBox textBox)
	{
		_textBox = textBox;
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
		if (_enabled)
		{
			_enabled = false;
			_textInput.Disable();
			_textInput.Commit();
			WaylandDiagnostics.Trace("text-input: disabled");
		}
		_textBox = null;
		_textBoxSurface = null;
		_surroundingText = null;
	}

	// Sends the surrounding text and caret rectangle of the focused TextBox after it changed.
	internal void UpdateState(TextBox textBox)
	{
		// Changes made while applying input method events are reported once, by Apply.
		if (!_applyingInputMethodChange)
		{
			SendState(textBox, ChangeCauseOther);
		}
	}

	private void SendState(TextBox textBox, uint cause)
	{
		if (!_enabled || textBox != _textBox)
		{
			return;
		}

		_textInput.SetTextChangeCause(cause);
		// The surrounding text excludes the preedit, which TextBox shows inline while composing.
		if (!_isComposing)
		{
			var selectionEnd = textBox.SelectionStart + textBox.SelectionLength;
			var (cursor, anchor) = textBox.IsBackwardSelection ? (textBox.SelectionStart, selectionEnd) : (selectionEnd, textBox.SelectionStart);
			var surrounding = WaylandTextInputText.GetSurroundingText(textBox.Text ?? string.Empty, cursor, anchor);
			_surroundingText = surrounding;
			_textInput.SetSurroundingText(surrounding.Text, surrounding.Cursor, surrounding.Anchor);
		}
		if (GetCaretRectangle(textBox) is { } caret)
		{
			_textInput.SetCursorRectangle(caret.X, caret.Y, caret.Width, caret.Height);
		}
		_textInput.Commit();
	}

	private void Enable()
	{
		if (_textBox is not { } textBox)
		{
			return;
		}
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
		// The compositor disables text input on leave; a disable request would be a protocol error.
		_enabled = false;
		if (_isComposing)
		{
			_isComposing = false;
			CompositionEnded?.Invoke(this, EventArgs.Empty);
		}
	}

	// Dispatch thread.
	private void OnDone(object? sender, ZwpTextInputV3.DoneEventArgs args)
	{
		var preedit = _pendingPreedit;
		var preeditCursor = _pendingPreeditCursor;
		var commit = _pendingCommit;
		var deleteBefore = _pendingDeleteBefore;
		var deleteAfter = _pendingDeleteAfter;
		_pendingPreedit = null;
		_pendingPreeditCursor = 0;
		_pendingCommit = null;
		_pendingDeleteBefore = 0;
		_pendingDeleteAfter = 0;
		WaylandConnection.PostToUI(() => Apply(preedit, preeditCursor, commit, deleteBefore, deleteAfter));
	}

	// Applies a done event in the order the protocol specifies: remove the preedit, delete
	// surrounding text, insert the commit string, then show the new preedit.
	private void Apply(string? preedit, int preeditCursor, string? commit, uint deleteBefore, uint deleteAfter)
	{
		var textChanged = commit is not null || deleteBefore != 0 || deleteAfter != 0;
		// Compositors also send done after each of our commits; an empty one changes nothing unless it ends a preedit.
		if (!_enabled || _textBox is not { } textBox || (!textChanged && preedit is null && !_isComposing))
		{
			return;
		}
		WaylandDiagnostics.Trace($"text-input: done preedit={preedit?.Length ?? -1} commit={commit?.Length ?? -1} delete={deleteBefore}/{deleteAfter}");

		_applyingInputMethodChange = true;
		try
		{
			if ((deleteBefore != 0 || deleteAfter != 0) && _surroundingText is { } surrounding)
			{
				if (_isComposing)
				{
					OnPreeditChanged(null, 0);
				}
				DeleteSurroundingText(textBox, surrounding, deleteBefore, deleteAfter);
			}
			if (commit is not null)
			{
				OnCommittedText(commit);
			}
			if (!string.IsNullOrEmpty(preedit))
			{
				OnPreeditChanged(preedit, preeditCursor < 0 ? preedit.Length : WaylandTextInputText.ByteOffsetToCharIndex(preedit, preeditCursor));
			}
			else if (_isComposing)
			{
				OnPreeditChanged(null, 0);
			}
		}
		finally
		{
			_applyingInputMethodChange = false;
		}
		// Report the edited text; a preedit-only update leaves the surrounding text unchanged.
		if (textChanged)
		{
			SendState(textBox, ChangeCauseInputMethod);
		}
	}

	private static void DeleteSurroundingText(TextBox textBox, WaylandTextInputText.SurroundingText surrounding, uint beforeLength, uint afterLength)
	{
		var (before, after) = WaylandTextInputText.GetDeletionRanges(surrounding, beforeLength, afterLength);
		var text = textBox.Text ?? string.Empty;
		if (after.Start + after.Length > text.Length)
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

	private void OnPreeditChanged(string? text, int cursor)
	{
		if (!string.IsNullOrEmpty(text))
		{
			if (!_isComposing)
			{
				_isComposing = true;
				CompositionStarted?.Invoke(this, EventArgs.Empty);
			}
			CompositionUpdated?.Invoke(this, new ImeCompositionEventArgs(text, cursor));
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
