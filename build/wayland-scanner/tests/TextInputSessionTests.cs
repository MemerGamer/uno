#nullable enable

using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.UI.Xaml.Controls.Extensions;
using Uno.WinUI.Runtime.Skia.Wayland;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;
using Uno.WinUI.Runtime.Skia.Wayland.Input;

namespace Wayland.Scanner.Tests;

[TestClass]
public sealed class TextInputSessionTests
{
	private sealed class Input : IWaylandTextInput
	{
		public event EventHandler<ZwpTextInputV3.EnterEventArgs>? Enter;
		public event EventHandler<ZwpTextInputV3.LeaveEventArgs>? Leave;
		public event EventHandler<ZwpTextInputV3.PreeditStringEventArgs>? PreeditString;
		public event EventHandler<ZwpTextInputV3.CommitStringEventArgs>? CommitString;
		public event EventHandler<ZwpTextInputV3.DeleteSurroundingTextEventArgs>? DeleteSurroundingText;
		public event EventHandler<ZwpTextInputV3.DoneEventArgs>? Done;
		public uint Serial { get; private set; }
		public bool Enabled { get; private set; }
		private bool _pendingEnabled;
		public WaylandTextInputText.SurroundingText? Surrounding { get; private set; }
		public (uint Hint, uint Purpose)? Content { get; private set; }
		public int ContentTypeRequests { get; private set; }
		private (uint Hint, uint Purpose)? _pendingContent;
		private WaylandTextInputText.SurroundingText? _pendingSurrounding;
		public void Enable()
		{
			_pendingEnabled = true;
			_pendingSurrounding = null;
		}
		public void Disable()
		{
			_pendingEnabled = false;
			_pendingSurrounding = null;
		}
		public void Commit()
		{
			Serial++;
			Enabled = _pendingEnabled;
			Surrounding = _pendingSurrounding;
			Content = _pendingContent;
		}
		public void SetTextChangeCause(uint cause) { }
		public void SetContentType(uint hint, uint purpose)
		{
			ContentTypeRequests++;
			_pendingContent = (hint, purpose);
		}
		public void SetSurroundingText(string text, int cursor, int anchor) => _pendingSurrounding = new(text, 0, cursor, anchor);
		public void SetCursorRectangle(int x, int y, int width, int height) { }
		public void EnterSurface(WlSurface surface) => Enter?.Invoke(this, new(surface));
		public void LeaveSurface(WlSurface surface) => Leave?.Invoke(this, new(surface));
		public void Preedit(string text, int begin = 0, int end = 0) => PreeditString?.Invoke(this, new(text, begin, end));
		public void Text(string text) => CommitString?.Invoke(this, new(text));
		public void Delete(uint before, uint after) => DeleteSurroundingText?.Invoke(this, new(before, after));
		public void Finish(uint? serial = null) => Done?.Invoke(this, new(serial ?? Serial));
	}

	[TestMethod]
	public void InputMethodTextResetsPendingKeyboardCompose()
	{
		var context = WaylandXkb.ContextNew(0);
		Assert.AreNotEqual((nint)0, context);
		try
		{
			using var compose = new WaylandKeyboardCompose(context, "en_US.UTF-8");
			var input = new Input();
			_ = new WaylandImeTextBoxExtension(input, new object(), WaylandConnection.Pending.Enqueue, compose.Reset);
			Assert.IsNull(compose.Feed(0xfe51, 0));
			input.Preedit("a");
			Assert.AreEqual((uint)'e', compose.Feed('e', 'e'));
			Assert.IsNull(compose.Feed(0xfe51, 0));
			input.Text("a");
			Assert.AreEqual((uint)'e', compose.Feed('e', 'e'));
		}
		finally
		{
			WaylandXkb.ContextUnref(context);
		}
	}

	private sealed class Host
	{
		public Input Input { get; } = new();
		public WlSurface Surface { get; } = (WlSurface)RuntimeHelpers.GetUninitializedObject(typeof(WlSurface));
		public WaylandImeTextBoxExtension Extension { get; }
		public TextBox Box { get; private set; } = null!;
		public ImeCompositionEventArgs? LastUpdate { get; private set; }
		public int Started { get; private set; }
		public int Ended { get; private set; }
		public Host(string text = "abc", int start = 1, int length = 0, bool readOnly = false)
		{
			WaylandConnection.Pending.Clear();
			Extension = new(Input, new(), WaylandConnection.Pending.Enqueue);
			Extension.CompositionStarted += (_, _) =>
			{
				Started++;
				Box.IsComposing = true;
				Box.CompositionStartIndex = Box.SelectionStart;
				Box.CompositionLength = Box.SelectionLength;
			};
			Extension.CompositionUpdated += (_, e) =>
			{
				LastUpdate = e;
				Replace(e.Text, e.CursorPosition);
			};
			Extension.CompositionCompleted += (_, e) =>
			{
				if (!Box.IsReadOnly)
				{
					Replace(e.Text, e.Text.Length);
				}
			};
			Extension.CompositionEnded += (_, _) =>
			{
				Ended++;
				Box.IsComposing = false;
				Box.CompositionLength = 0;
			};
			Focus(text, start, length, readOnly);
			Input.EnterSurface(Surface);
			WaylandConnection.Drain();
		}
		public void Focus(string text, int start = 0, int length = 0, bool readOnly = false)
		{
			if (Box is not null)
			{
				Extension.EndImeSession();
			}
			Box = new() { Text = text, IsReadOnly = readOnly, XamlRoot = new WaylandXamlRootHost { Surface = Surface } };
			Box.Select(start, length);
			Extension.StartImeSession(Box);
		}
		private void Replace(string text, int cursor)
		{
			var start = Box.CompositionStartIndex;
			Box.Text = Box.Text.Remove(start, Box.CompositionLength).Insert(start, text);
			Box.CompositionLength = text.Length;
			Box.Select(start + (cursor < 0 ? text.Length : cursor), 0);
		}
		public void Finish(uint? serial = null)
		{
			Input.Finish(serial);
			WaylandConnection.Drain();
		}
	}

	[TestMethod]
	public void ConfigurationChangesPublishContentTypeAndCommit()
	{
		var host = new Host();
		var serial = host.Input.Serial;
		var requests = host.Input.ContentTypeRequests;
		var hint = ZwpTextInputV3.ContentHintValues.Spellcheck;
		host.Box.IsTextPredictionEnabled = false;
		Assert.AreEqual(((uint)hint, (uint)ZwpTextInputV3.ContentPurposeValues.Normal), host.Input.Content!.Value);
		Assert.AreEqual(++serial, host.Input.Serial);
		Assert.AreEqual(++requests, host.Input.ContentTypeRequests);
		host.Box.IsSpellCheckEnabled = false;
		Assert.AreEqual((0u, (uint)ZwpTextInputV3.ContentPurposeValues.Normal), host.Input.Content!.Value);
		Assert.AreEqual(++serial, host.Input.Serial);
		Assert.AreEqual(++requests, host.Input.ContentTypeRequests);
		host.Box.InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.EmailSmtpAddress) } };
		Assert.AreEqual((0u, (uint)ZwpTextInputV3.ContentPurposeValues.Email), host.Input.Content!.Value);
		Assert.AreEqual(++serial, host.Input.Serial);
		Assert.AreEqual(++requests, host.Input.ContentTypeRequests);
		host.Box.AcceptsReturn = true;
		Assert.AreEqual(((uint)ZwpTextInputV3.ContentHintValues.Multiline, (uint)ZwpTextInputV3.ContentPurposeValues.Email), host.Input.Content!.Value);
		Assert.AreEqual(++serial, host.Input.Serial);
		Assert.AreEqual(++requests, host.Input.ContentTypeRequests);
		host.Box.AcceptsReturn = true;
		Assert.AreEqual(requests, host.Input.ContentTypeRequests, "An unchanged value must not republish hints.");
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public void ContentHintChangedInsideCompositionUpdatedPublishesAfterApply(bool deferState)
	{
		var host = new Host();
		host.Input.Preedit("k");
		host.Finish();
		Assert.IsTrue(host.Extension.IsComposing);
		var oldSerial = host.Input.Serial;
		if (deferState)
		{
			host.Box.IsSpellCheckEnabled = false;
		}
		var currentSerial = host.Input.Serial;
		var requests = host.Input.ContentTypeRequests;
		var content = host.Input.Content;
		var updates = 0;
		host.Extension.CompositionUpdated += (_, _) =>
		{
			updates++;
			host.Box.IsTextPredictionEnabled = false;
			Assert.AreEqual(requests, host.Input.ContentTypeRequests, "Hints must wait until input application completes.");
			Assert.AreEqual(currentSerial, host.Input.Serial);
		};
		host.Input.Preedit("ka");
		host.Finish(oldSerial);
		Assert.AreEqual(1, updates);
		Assert.IsTrue(host.Extension.IsComposing);
		if (deferState)
		{
			Assert.AreEqual(requests, host.Input.ContentTypeRequests, "A stale done must keep hint publication deferred.");
			Assert.AreEqual(currentSerial, host.Input.Serial);
			Assert.AreEqual(content, host.Input.Content);
			host.Input.Preedit("kan");
			host.Finish(currentSerial);
			Assert.AreEqual(2, updates);
		}
		Assert.IsTrue(host.Extension.IsComposing);
		Assert.AreEqual(requests + 1, host.Input.ContentTypeRequests);
		Assert.AreEqual(currentSerial + 1, host.Input.Serial);
		var hint = deferState ? 0u : (uint)ZwpTextInputV3.ContentHintValues.Spellcheck;
		Assert.AreEqual((hint, (uint)ZwpTextInputV3.ContentPurposeValues.Normal), host.Input.Content!.Value);
		Assert.AreEqual("abc", host.Input.Surrounding!.Value.Text, "Published surrounding text must exclude the completed preedit update.");
	}

	[TestMethod]
	public void ContentPropertySubscriptionsFollowFocusAndAreRemovedAtSessionEnd()
	{
		var host = new Host();
		var oldBox = host.Box;
		Assert.AreEqual(7, oldBox.PropertyCallbackCount);
		host.Focus("other");
		Assert.AreEqual(0, oldBox.PropertyCallbackCount);
		Assert.AreEqual(7, host.Box.PropertyCallbackCount);
		var requests = host.Input.ContentTypeRequests;
		oldBox.IsTextPredictionEnabled = false;
		oldBox.IsSpellCheckEnabled = false;
		oldBox.InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } };
		oldBox.AcceptsReturn = true;
		Assert.AreEqual(requests, host.Input.ContentTypeRequests);
		host.Box.IsTextPredictionEnabled = false;
		Assert.AreEqual(requests + 1, host.Input.ContentTypeRequests);
		host.Extension.EndImeSession();
		Assert.AreEqual(0, host.Box.PropertyCallbackCount);
		host.Box.IsSpellCheckEnabled = false;
		host.Box.InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } };
		host.Box.AcceptsReturn = true;
		Assert.AreEqual(requests + 1, host.Input.ContentTypeRequests);
	}

	[TestMethod]
	public void DeferredConfigurationPublishesAfterMatchingDone()
	{
		var host = new Host();
		var old = host.Input.Serial;
		host.Box.Select(2, 0);
		host.Extension.UpdateState(host.Box);
		var current = host.Input.Serial;
		host.Input.Text("X");
		host.Finish(old);
		var requests = host.Input.ContentTypeRequests;
		host.Box.AcceptsReturn = true;
		Assert.AreEqual(requests, host.Input.ContentTypeRequests);
		host.Finish(current);
		Assert.AreEqual(requests + 1, host.Input.ContentTypeRequests);
		Assert.AreEqual(current + 1, host.Input.Serial);
		Assert.AreNotEqual(0u, host.Input.Content!.Value.Hint & (uint)ZwpTextInputV3.ContentHintValues.Multiline);
	}

	[TestMethod]
	public void StaleSerialAppliesTextAndDefersAllStateUntilMatchingDone()
	{
		var host = new Host();
		var old = host.Input.Serial;
		host.Box.Select(2, 0);
		host.Extension.UpdateState(host.Box);
		var current = host.Input.Serial;
		host.Input.Text("X");
		host.Finish(old);
		Assert.AreEqual("abXc", host.Box.Text);
		Assert.AreEqual(current, host.Input.Serial);
		host.Extension.UpdateState(host.Box);
		Assert.AreEqual(current, host.Input.Serial);
		host.Finish(current);
		Assert.AreEqual(current + 1, host.Input.Serial);
		Assert.AreEqual("abXc", host.Input.Surrounding!.Value.Text);
	}

	[TestMethod]
	public void QueuedBatchCannotMutateReplacementSession()
	{
		var host = new Host();
		host.Input.Text("X");
		host.Input.Delete(1, 1);
		host.Input.Finish();
		host.Focus("other", 2);
		WaylandConnection.Drain();
		Assert.AreEqual("other", host.Box.Text);
	}

	[TestMethod]
	public void CommitAndPreeditRefreshSurroundingBeforeNextDeletion()
	{
		var host = new Host("abcd", 1, 2);
		host.Input.Preedit("old");
		host.Finish();
		Assert.AreEqual("ad", host.Input.Surrounding!.Value.Text);
		Assert.AreEqual(host.Input.Surrounding.Value.Cursor, host.Input.Surrounding.Value.Anchor);
		host.Input.Text("é");
		host.Input.Preedit("next");
		host.Finish();
		Assert.AreEqual("aéd", host.Input.Surrounding!.Value.Text);
		host.Input.Delete(2, 0);
		host.Input.Text("X");
		host.Finish();
		Assert.AreEqual("aXd", host.Box.Text);
	}

	[TestMethod]
	public void ReadOnlyFieldsDisableAndRejectQueuedMutations()
	{
		var host = new Host(readOnly: true);
		Assert.IsFalse(host.Input.Enabled);
		host.Box.IsReadOnly = false;
		Assert.IsTrue(host.Input.Enabled);
		host.Input.Delete(1, 1);
		host.Input.Text("X");
		host.Input.Finish();
		host.Box.IsReadOnly = true;
		Assert.IsFalse(host.Input.Enabled);
		WaylandConnection.Drain();
		Assert.AreEqual("abc", host.Box.Text);
	}

	[TestMethod]
	public void OversizedSelectionDisablesSurroundingWithoutLosingReplacement()
	{
		var host = new Host(new string('x', 5000), 0, 5000);
		Assert.IsNull(host.Input.Surrounding);
		host.Input.Delete(1, 0);
		host.Input.Text("X");
		host.Finish();
		Assert.AreEqual("X", host.Box.Text);
	}

	[TestMethod]
	public void LeaveCancelsPreeditButFocusEndKeepsIt()
	{
		var host = new Host();
		host.Input.Preedit("preview");
		host.Finish();
		host.Input.LeaveSurface(host.Surface);
		WaylandConnection.Drain();
		Assert.AreEqual("abc", host.Box.Text);
		Assert.IsFalse(host.Extension.IsComposing);
		host.Input.EnterSurface(host.Surface);
		WaylandConnection.Drain();
		host.Input.Preedit("preview");
		host.Finish();
		host.Extension.EndImeSession();
		Assert.AreEqual("apreviewbc", host.Box.Text);
	}

	[TestMethod]
	public void PreeditUpdatesKeepOneCompositionUntilCommit()
	{
		var host = new Host();
		host.Input.Preedit("k");
		host.Finish();
		var serial = host.Input.Serial;
		host.Input.Preedit("ka");
		host.Finish();
		Assert.AreEqual((1, 0), (host.Started, host.Ended));
		Assert.AreEqual(serial, host.Input.Serial);
		host.Input.Text("か");
		host.Finish();
		Assert.AreEqual((1, 1), (host.Started, host.Ended));
		Assert.AreEqual("aかbc", host.Box.Text);
	}

	[TestMethod]
	public void PreeditCursorIsUtf16AndHiddenCursorMapsToEnd()
	{
		var host = new Host();
		host.Input.Preedit("é😀z", 2, 6);
		host.Finish();
		Assert.AreEqual(1, host.LastUpdate!.CursorPosition);
		host.Input.Preedit("é😀z", -1, -1);
		host.Finish();
		Assert.AreEqual(-1, host.LastUpdate!.CursorPosition);
	}

	[TestMethod]
	public void DisableCommitsAndItsSerialIsCountedBeforeReenable()
	{
		var host = new Host();
		var serial = host.Input.Serial;
		host.Box.IsReadOnly = true;
		Assert.AreEqual(serial + 1, host.Input.Serial);
		Assert.IsFalse(host.Input.Enabled);
		host.Box.IsReadOnly = false;
		Assert.AreEqual(serial + 2, host.Input.Serial);
		host.Input.Text("X");
		host.Finish();
		Assert.AreEqual("aXbc", host.Box.Text);
	}

	[TestMethod]
	public void GrowingSelectionResetsSurroundingSupportAndInvalidatesOldBatch()
	{
		var host = new Host(new string('x', 5000));
		Assert.IsNotNull(host.Input.Surrounding);
		host.Input.Delete(1, 0);
		host.Input.Finish();
		host.Box.Select(0, 5000);
		host.Extension.UpdateState(host.Box);
		WaylandConnection.Drain();
		Assert.IsNull(host.Input.Surrounding);
		Assert.AreEqual(5000, host.Box.SelectionLength);
		host.Input.Delete(1, 0);
		host.Input.Text("X");
		host.Finish();
		Assert.AreEqual("X", host.Box.Text);
	}

	[TestMethod]
	public void StaleDeletionUsesItsCommittedSelectionRatherThanNewCaret()
	{
		var host = new Host("abcdef", 2, 2);
		var serial = host.Input.Serial;
		host.Box.Select(6, 0);
		host.Extension.UpdateState(host.Box);
		host.Input.Delete(1, 1);
		host.Input.Text("X");
		host.Finish(serial);
		Assert.AreEqual("aXf", host.Box.Text);
	}

	[TestMethod]
	public void SameLengthEditRejectsQueuedDeletionButKeepsCommit()
	{
		var host = new Host("abc", 1);
		host.Input.Delete(1, 0);
		host.Input.Text("X");
		host.Input.Finish();
		host.Box.Text = "xyz";
		WaylandConnection.Drain();
		Assert.AreEqual("xXyz", host.Box.Text);
		Assert.AreEqual("xXyz", host.Input.Surrounding!.Value.Text);
	}

	[TestMethod]
	public void EditAndRevertStillInvalidatesOldRevision()
	{
		var host = new Host();
		host.Input.Delete(1, 0);
		host.Input.Finish();
		host.Box.Text = "xyz";
		host.Box.Text = "abc";
		WaylandConnection.Drain();
		Assert.AreEqual("abc", host.Box.Text);
	}

	[TestMethod]
	public void QueuedBatchCannotDeleteTextChangedByAnotherSerial()
	{
		var host = new Host();
		host.Input.Delete(1, 0);
		host.Input.Text("X");
		host.Input.Finish();
		host.Box.Select(2, 0);
		host.Extension.UpdateState(host.Box);
		host.Input.Delete(1, 0);
		host.Input.Text("Y");
		host.Input.Finish();
		WaylandConnection.Drain();
		Assert.AreEqual("XYbc", host.Box.Text);
		Assert.AreEqual("XYbc", host.Input.Surrounding!.Value.Text);
	}

	[TestMethod]
	public void StaleCommitWithoutDeletionAppliesAfterApplicationEdit()
	{
		var host = new Host("abc", 3);
		var serial = host.Input.Serial;
		// A key the input method forwarded edits the text before the input method saw the new state.
		host.Box.Text = "ab";
		host.Box.Select(2, 0);
		host.Extension.UpdateState(host.Box);
		host.Input.Text("x");
		host.Input.Finish(serial);
		WaylandConnection.Drain();
		Assert.AreEqual("abx", host.Box.Text);
		Assert.IsTrue(host.Input.Enabled);
	}

	[TestMethod]
	public void UnacknowledgedStateHistoryIsBoundedWithoutReset()
	{
		var host = new Host();
		var serial = host.Input.Serial;
		for (var i = 0; i < 1000; i++)
		{
			host.Box.Select(i % 3, 0);
			host.Extension.UpdateState(host.Box);
		}
		// One commit per update: no disable/enable resets.
		Assert.AreEqual(serial + 1000, host.Input.Serial);
		var history = (WaylandTextInputStateHistory<TextBox>)typeof(WaylandImeTextBoxExtension)
			.GetField("_committedStates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
			.GetValue(host.Extension)!;
		var states = (System.Collections.ICollection)typeof(WaylandTextInputStateHistory<TextBox>)
			.GetField("_states", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
			.GetValue(history)!;
		Assert.AreEqual(64, states.Count);
		Assert.IsFalse(history.TryGetValue(serial, out _));
		// An evicted snapshot cannot delete, but its commit still belongs to this session.
		host.Input.Delete(1, 0);
		host.Input.Text("X");
		host.Finish(serial);
		Assert.AreEqual("Xabc", host.Box.Text);
		host.Input.Text("Y");
		host.Finish();
		Assert.AreEqual("XYabc", host.Box.Text);
	}

	[TestMethod]
	public void PendingEventsOnlyApplyOnDoneAndResetAfterIt()
	{
		var host = new Host();
		host.Input.Text("X");
		Assert.AreEqual("abc", host.Box.Text);
		host.Finish();
		Assert.AreEqual("aXbc", host.Box.Text);
		var serial = host.Input.Serial;
		host.Finish();
		Assert.AreEqual("aXbc", host.Box.Text);
		Assert.AreEqual(serial, host.Input.Serial);
	}
}
