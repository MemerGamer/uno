#nullable enable

using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Input.Preview.Injection;

namespace Uno.UI.Runtime.Wayland.Tests;

[TestClass]
[DoNotParallelize]
public sealed class KeyboardInjectionTargetTests
{
	private static readonly FieldInfo _targets = typeof(InputInjector).GetField("_inputManagers", BindingFlags.Static | BindingFlags.NonPublic)!;
	private object? _previousTargets;

	[TestInitialize]
	public void SaveTargets()
	{
		_previousTargets = _targets.GetValue(null);
		_targets.SetValue(null, null);
	}

	[TestCleanup]
	public void RestoreTargets() => _targets.SetValue(null, _previousTargets);

	[TestMethod]
	public void NoNativeActivationRoutesToReadyWindowInsteadOfStartupRoot()
	{
		var startup = new Target(active: true, ready: false);
		var window = new Target(active: false, ready: true);
		InputInjector.SetTargetForCurrentThread(startup);
		var injector = InputInjector.TryCreate()!;
		InputInjector.SetTargetForCurrentThread(window);
		Tap(injector);
		Assert.AreEqual(0, startup.Keys.Count);
		Assert.AreEqual(2, window.Keys.Count);
		Assert.AreEqual(VirtualKey.A, window.Keys[0].VirtualKey);
		Assert.AreEqual(1u, window.Keys[0].KeyStatus.RepeatCount);
		Assert.IsTrue(window.Keys[1].KeyStatus.IsKeyReleased);
	}

	[TestMethod]
	public void ClosedWindowCannotStealInputWithRetainedActivation()
	{
		var closed = new Target(active: true, ready: false);
		var window = new Target(active: true, ready: true);
		InputInjector.SetTargetForCurrentThread(closed);
		InputInjector.SetTargetForCurrentThread(window);
		Tap(InputInjector.TryCreate()!);
		Assert.AreEqual(0, closed.Keys.Count);
		Assert.AreEqual(2, window.Keys.Count);
	}

	[TestMethod]
	public void NativeActiveWindowWinsOverHeadlessFallback()
	{
		var inactive = new Target(active: false, ready: true);
		var active = new Target(active: true, ready: true);
		InputInjector.SetTargetForCurrentThread(inactive);
		InputInjector.SetTargetForCurrentThread(active);
		Tap(InputInjector.TryCreate()!);
		Assert.AreEqual(0, inactive.Keys.Count);
		Assert.AreEqual(2, active.Keys.Count);
	}

	private static void Tap(InputInjector injector)
		=> injector.InjectKeyboardInput(new[]
		{
			new InjectedInputKeyboardInfo { VirtualKey = (ushort)VirtualKey.A },
			new InjectedInputKeyboardInfo { VirtualKey = (ushort)VirtualKey.A, KeyOptions = InjectedInputKeyOptions.KeyUp }
		});

	private sealed class Target(bool active, bool ready) : IInputInjectorTarget
	{
		internal List<KeyEventArgs> Keys { get; } = new();
		public bool IsActive => active;
		public bool IsReadyForKeyboardInput => ready;
		public void InjectKeyDown(KeyEventArgs args) => Keys.Add(args);
		public void InjectKeyUp(KeyEventArgs args) => Keys.Add(args);
		public void InjectPointerAdded(PointerEventArgs args) => throw new NotSupportedException();
		public void InjectPointerUpdated(PointerEventArgs args) => throw new NotSupportedException();
		public void InjectPointerRemoved(PointerEventArgs args) => throw new NotSupportedException();
	}
}
