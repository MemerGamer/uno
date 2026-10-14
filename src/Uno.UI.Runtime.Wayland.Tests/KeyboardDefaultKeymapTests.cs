#nullable enable

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.UI.Runtime.Wayland.Input;
using Windows.System;
using Windows.UI.Core;

namespace Uno.UI.Runtime.Wayland.Tests;

[TestClass]
public sealed class KeyboardDefaultKeymapTests
{
	[TestInitialize]
	public void RequireXkbcommon()
	{
		if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libxkbcommon.so.0", out var library))
		{
			Assert.Inconclusive("libxkbcommon and its keymap tables are required.");
			return;
		}
		NativeLibrary.Free(library);
	}

	[TestMethod]
	public void NoKeyboardDeviceHasDefaultKeymapBeforeCompositorKeymap()
	{
		using var keyboard = CreateKeyboard();
		var events = new List<KeyEventArgs>();
		keyboard.KeyDown += (_, args) => events.Add(args);
		keyboard.KeyUp += (_, args) => events.Add(args);
		keyboard.UseDefaultKeymap();
		keyboard.RepeatInfo(0, 0);
		keyboard.Enter([]);
		keyboard.Key(57, 1); // evdev Space is independent of the configured alphabetic layout.
		keyboard.Key(57, 0);
		Assert.AreEqual(2, events.Count);
		Assert.AreEqual(VirtualKey.Space, events[0].VirtualKey);
		Assert.AreEqual(' ', GetText(events[0]));
		Assert.AreEqual(57u, events[0].KeyStatus.ScanCode);
		Assert.AreEqual(1u, events[0].KeyStatus.RepeatCount);
		Assert.IsTrue(events[1].KeyStatus.IsKeyReleased);
	}

	[TestMethod]
	public void CompositorKeymapReplacesDefaultAndDeviceRemovalRestoresIt()
	{
		using var keyboard = CreateKeyboard();
		var text = new List<char?>();
		keyboard.KeyDown += (_, args) => text.Add(GetText(args));
		keyboard.RepeatInfo(0, 0);
		keyboard.Enter([]);
		TapSpace(keyboard);
		var path = Path.GetTempFileName();
		try
		{
			File.WriteAllBytes(path, Encoding.UTF8.GetBytes("""
				xkb_keymap {
				  xkb_keycodes { include "evdev+aliases(qwerty)" };
				  xkb_types { include "complete" };
				  xkb_compatibility { include "complete" };
				  xkb_symbols { include "pc+us+inet(evdev)" key <SPCE> { [ a, A ] }; };
				};
				""" + "\0"));
			using var handle = File.OpenHandle(path);
			var fd = checked((int)handle.DangerousGetHandle());
			handle.SetHandleAsInvalid();
			keyboard.Keymap(1, fd, checked((uint)new FileInfo(path).Length));
			TapSpace(keyboard);
			keyboard.UpdateModifiers(1, 0, 0, 0);
			TapSpace(keyboard);
			keyboard.Leave();
			keyboard.UseDefaultKeymap();
			keyboard.Enter([]);
			TapSpace(keyboard);
			CollectionAssert.AreEqual(new char?[] { ' ', 'a', 'A', ' ' }, text);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[TestMethod]
	public void DefaultKeymapDoesNotInventNativeKeyboardFocus()
	{
		using var keyboard = CreateKeyboard();
		var events = 0;
		keyboard.KeyDown += (_, _) => events++;
		keyboard.Key(57, 1);
		Assert.AreEqual(0, events);
	}

	private static WaylandKeyboardInputSource CreateKeyboard() => new(new NoKeyboardConnection(), action => action(), _ => { });

	private static char? GetText(KeyEventArgs args)
		=> (char?)typeof(KeyEventArgs).GetProperty("UnicodeKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(args);

	private static void TapSpace(WaylandKeyboardInputSource keyboard)
	{
		keyboard.Key(57, 1);
		keyboard.Key(57, 0);
	}

	private sealed class NoKeyboardConnection : IWaylandInputConnection
	{
		public void Post(Action action) => action();
		public void SetCursor(uint enterSerial, CoreCursor? cursor) { }
		public IDisposable GetPointer(WaylandPointerInputSource sink) => throw new NotSupportedException();
		public IDisposable GetKeyboard(WaylandKeyboardInputSource sink) => throw new NotSupportedException();
		public IDisposable GetTouch(WaylandPointerInputSource sink) => throw new NotSupportedException();
	}
}
