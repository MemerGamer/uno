#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Uno.UI.Hosting;
using Uno.UI.Runtime.Wayland;
using Uno.UI.Runtime.Wayland.Protocols;
using Uno.UI.RuntimeTests.Helpers;
using static Private.Infrastructure.TestServices;

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Controls;

[TestClass]
[RunsOnUIThread]
[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWayland)]
public sealed class Given_Wayland_ImeSession
{
	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public async Task When_Injected_Batches_Reach_Editor(bool richEdit)
	{
		var (editor, host) = CreateEditor(richEdit, "aé😀z");
		var input = new InjectedTextInput();
		var posted = new Queue<Action>();
		var extension = new WaylandImeTextBoxExtension(input, new object(), posted.Enqueue);
		using var scope = ImeSessionCoordinator.SetExtensionForTesting(extension);
		try
		{
			await UITestHelper.Load(editor);
			Assert.IsTrue(editor.Focus(FocusState.Programmatic));
			await WindowHelper.WaitForIdle();
			host.SelectFromNative(2, 0);
			extension.UpdateState(host);
			input.EnterSurface(GetSurface(editor));
			Drain(posted);
			Assert.IsTrue(input.Enabled);
			Assert.AreSame(extension, ImeSessionCoordinator.GetExtension(host));

			input.Delete(2, 4);
			input.CommitText("界");
			input.Preedit("に", 3);
			Assert.AreEqual("aé😀z", host.Text, "Protocol events must wait for done.");
			input.CompleteBatch();
			Assert.AreEqual("aé😀z", host.Text, "A done batch must wait for the UI dispatch.");
			Drain(posted);
			Assert.AreEqual("a界にz", host.Text);
			Assert.AreEqual(3, host.SelectionStart);
			Assert.AreEqual(0, host.SelectionLength);
			Assert.IsTrue(host.IsComposing);

			input.CommitText("日本");
			input.CompleteBatch();
			Drain(posted);
			Assert.AreEqual("a界日本z", host.Text, "Commit must replace the preedit rather than append to it.");
			Assert.AreEqual(4, host.SelectionStart);
			Assert.IsFalse(host.IsComposing);

			input.Preedit("仮", 3);
			input.CompleteBatch();
			Drain(posted);
			Assert.AreEqual("a界日本仮z", host.Text);
			Assert.IsTrue(host.IsComposing);
			input.Preedit(string.Empty, 0);
			input.CompleteBatch();
			Drain(posted);
			Assert.AreEqual("a界日本z", host.Text, "An empty preedit must cancel only the transient text.");
			Assert.AreEqual(4, host.SelectionStart);
			Assert.IsFalse(host.IsComposing);
		}
		finally
		{
			ImeSessionCoordinator.EndSession(host);
			WindowHelper.WindowContent = null;
			await WindowHelper.WaitForIdle();
			Drain(posted);
		}
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public async Task When_Focus_Changes_Before_Injected_Batch_Is_Dispatched(bool richEdit)
	{
		var (first, firstHost) = CreateEditor(richEdit, "first");
		var (second, secondHost) = CreateEditor(!richEdit, "second");
		var input = new InjectedTextInput();
		var posted = new Queue<Action>();
		var extension = new WaylandImeTextBoxExtension(input, new object(), posted.Enqueue);
		using var scope = ImeSessionCoordinator.SetExtensionForTesting(extension);
		try
		{
			await UITestHelper.Load(new StackPanel { Children = { first, second } });
			Assert.IsTrue(first.Focus(FocusState.Programmatic));
			await WindowHelper.WaitForIdle();
			input.EnterSurface(GetSurface(first));
			Drain(posted);
			Assert.IsTrue(input.Enabled);
			input.Delete(1, 1);
			input.CommitText("stale");
			input.Preedit("stale", 5);
			input.CompleteBatch();
			Assert.AreEqual(1, posted.Count);

			Assert.IsTrue(second.Focus(FocusState.Programmatic));
			await WindowHelper.WaitForIdle();
			Drain(posted);
			Assert.AreEqual("first", firstHost.Text);
			Assert.AreEqual("second", secondHost.Text);
			Assert.IsFalse(firstHost.IsComposing);
			Assert.IsFalse(secondHost.IsComposing);
			Assert.IsNull(ImeSessionCoordinator.GetExtension(firstHost));
			Assert.AreSame(extension, ImeSessionCoordinator.GetExtension(secondHost));

			secondHost.SelectFromNative(6, 0);
			extension.UpdateState(secondHost);
			input.CommitText("!");
			input.CompleteBatch();
			Drain(posted);
			Assert.AreEqual("second!", secondHost.Text);
			Assert.AreEqual("first", firstHost.Text);
		}
		finally
		{
			ImeSessionCoordinator.EndSession(firstHost);
			ImeSessionCoordinator.EndSession(secondHost);
			WindowHelper.WindowContent = null;
			await WindowHelper.WaitForIdle();
			Drain(posted);
		}
	}

	private static (Control Editor, IImeSessionHost Host) CreateEditor(bool richEdit, string text)
	{
		if (richEdit)
		{
			var editor = new RichEditBox { Width = 240, Height = 80 };
			editor.Document.SetText(TextSetOptions.None, text);
			return (editor, (IImeSessionHost)editor);
		}
		var textBox = new TextBox { Width = 240, Text = text };
		return (textBox, ((ITextBoxHost)textBox).Core);
	}

	private static WlSurface GetSurface(Control editor)
	{
		var rootHost = XamlRootMap.GetHostForRoot(editor.XamlRoot!);
		Assert.IsInstanceOfType<WaylandXamlRootHost>(rootHost);
		return ((WaylandXamlRootHost)rootHost!).Surface;
	}

	private static void Drain(Queue<Action> posted)
	{
		while (posted.TryDequeue(out var action))
		{
			action();
		}
	}

	private sealed class InjectedTextInput : IWaylandTextInput
	{
		private uint _serial;
		public bool Enabled { get; private set; }
		public event EventHandler<ZwpTextInputV3.EnterEventArgs>? Enter;
		public event EventHandler<ZwpTextInputV3.LeaveEventArgs>? Leave
		{
			add
			{
			}
			remove
			{
			}
		}
		public event EventHandler<ZwpTextInputV3.PreeditStringEventArgs>? PreeditString;
		public event EventHandler<ZwpTextInputV3.CommitStringEventArgs>? CommitString;
		public event EventHandler<ZwpTextInputV3.DeleteSurroundingTextEventArgs>? DeleteSurroundingText;
		public event EventHandler<ZwpTextInputV3.DoneEventArgs>? Done;
		public void EnterSurface(WlSurface surface) => Enter?.Invoke(this, new(surface));
		public void Preedit(string text, int cursor) => PreeditString?.Invoke(this, new(text, cursor, cursor));
		public void CommitText(string text) => CommitString?.Invoke(this, new(text));
		public void Delete(uint before, uint after) => DeleteSurroundingText?.Invoke(this, new(before, after));
		public void CompleteBatch() => Done?.Invoke(this, new(_serial));
		public void Enable() => Enabled = true;
		public void Disable() => Enabled = false;
		public void Commit() => _serial = unchecked(_serial + 1);
		public void SetTextChangeCause(uint cause)
		{
		}
		public void SetContentType(uint hint, uint purpose)
		{
		}
		public void SetSurroundingText(string text, int cursor, int anchor)
		{
		}
		public void SetCursorRectangle(int x, int y, int width, int height)
		{
		}
	}

	[TestMethod]
	public async Task When_Focus_Moves_Between_TextBox_And_RichEditBox()
	{
		var first = new TextBox { Text = "café 😀 日本語", Width = 240 };
		var second = new TextBox { Text = "second", Width = 240 };
		var rich = new RichEditBox { Width = 240, Height = 80 };
		rich.Document.SetText(TextSetOptions.None, "العربية 😀");
		var firstHost = ((ITextBoxHost)first).Core;
		var secondHost = ((ITextBoxHost)second).Core;
		var richHost = (IImeSessionHost)rich;
		try
		{
			await UITestHelper.Load(new StackPanel { Children = { first, second, rich } });
			Assert.IsTrue(first.Focus(FocusState.Programmatic));
			await WindowHelper.WaitForIdle();
			RequireWaylandExtension(firstHost);
			first.Select(5, 2);
			await WindowHelper.WaitForIdle();
			Assert.AreEqual(5, ((IImeSessionHost)firstHost).SelectionStart);
			Assert.AreEqual(2, ((IImeSessionHost)firstHost).SelectionLength);

			Assert.IsTrue(second.Focus(FocusState.Programmatic));
			await WindowHelper.WaitForIdle();
			Assert.IsNull(ImeSessionCoordinator.GetExtension(firstHost));
			RequireWaylandExtension(secondHost);

			Assert.IsTrue(rich.Focus(FocusState.Programmatic));
			await WindowHelper.WaitForIdle();
			Assert.IsNull(ImeSessionCoordinator.GetExtension(secondHost));
			RequireWaylandExtension(richHost);
			Assert.IsFalse(richHost.IsComposing);
		}
		finally
		{
			WindowHelper.WindowContent = null;
			await WindowHelper.WaitForIdle();
		}
		Assert.IsNull(ImeSessionCoordinator.GetExtension(firstHost));
		Assert.IsNull(ImeSessionCoordinator.GetExtension(secondHost));
		Assert.IsNull(ImeSessionCoordinator.GetExtension(richHost));
	}

	private static void RequireWaylandExtension(IImeSessionHost host)
	{
		var extension = ImeSessionCoordinator.GetExtension(host);
		if (extension is null)
		{
			Assert.Inconclusive("The compositor must advertise text-input-v3 to exercise native Wayland IME sessions.");
			return;
		}
		Assert.AreEqual("Uno.UI.Runtime.Wayland", extension.GetType().Assembly.GetName().Name);
	}
}
