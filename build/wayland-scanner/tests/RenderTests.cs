#nullable enable
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.WinUI.Runtime.Skia.Wayland;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;
using Uno.WinUI.Runtime.Skia.Wayland.Rendering;

namespace Wayland.Scanner.Tests;

public sealed partial class DisplayTests
{
	[TestMethod]
	public void EglConfigPrefersNonblockingSwaps()
	{
		IntPtr[] configs = [new(1), new(2), new(3)];
		Assert.AreEqual(configs[2], WaylandEglConfig.Select(configs, config => config == configs[0] ? 1 : config == configs[1] ? null : 0));
	}

	[TestMethod]
	public void EglConfigRejectsClampedOrUnknownSwapIntervals()
	{
		IntPtr[] configs = [new(1), new(2)];
		Assert.ThrowsExactly<NotSupportedException>(() => WaylandEglConfig.Select(configs, config => config == configs[0] ? 1 : null));
		Assert.ThrowsExactly<NotSupportedException>(() => WaylandEglConfig.Select([], _ => 0));
	}

	[TestMethod]
	public void FrameSchedulerProducesFramesWhileFrameCallbacksAreWithheld()
	{
		using var peer = new ProtocolPeer();
		using var display = peer.Connect();
		using var compositor = (WlCompositor)display.Bind(WlCompositor.ProtocolName, WlCompositor.MaximumVersion);
		using var surface = compositor.CreateSurface();
		var owner = new BlockingCollection<Action>();
		var renders = 0;
		var stalls = 0;
		// The peer never answers wl_surface.frame, like a compositor hiding the surface.
		using var scheduler = new WaylandFrameScheduler(surface, () => ++renders > 0, () => stalls++, owner.Add);
		scheduler.Invalidate();
		scheduler.Invalidate();
		Assert.AreEqual(1, renders, "The second frame must wait for the frame callback.");
		Assert.IsTrue(owner.TryTake(out var tick, TimeSpan.FromSeconds(5)), "The stall timer did not fire.");
		tick();
		Assert.AreEqual(1, stalls);
		Assert.AreEqual(1, renders);
		Assert.IsFalse(owner.TryTake(out _, WaylandFrameScheduler.StallInterval * 3), "An idle UI must not keep producing frames.");
		scheduler.Invalidate();
		Assert.IsTrue(owner.TryTake(out tick, TimeSpan.FromSeconds(5)));
		tick();
		Assert.AreEqual(2, stalls);
	}

	[TestMethod]
	public void FrameSchedulerDoesNotStallWithoutPendingFrames()
	{
		using var peer = new ProtocolPeer();
		using var display = peer.Connect();
		using var compositor = (WlCompositor)display.Bind(WlCompositor.ProtocolName, WlCompositor.MaximumVersion);
		using var surface = compositor.CreateSurface();
		var owner = new BlockingCollection<Action>();
		var stalls = 0;
		using var scheduler = new WaylandFrameScheduler(surface, () => true, () => stalls++, owner.Add);
		scheduler.Invalidate();
		Assert.IsFalse(owner.TryTake(out _, WaylandFrameScheduler.StallInterval * 3));
		Assert.AreEqual(0, stalls);
	}

	[TestMethod]
	public void IntegerScaleFollowsTheLargestEnteredOutput()
	{
		var low = WaylandOutputInfo.Unknown with { Name = "A", Scale = 1 };
		var high = WaylandOutputInfo.Unknown with { Name = "B", Scale = 2 };
		Assert.AreEqual(2, WaylandOutput.GetIntegerScale([low, high], 1));
		Assert.AreEqual(1, WaylandOutput.GetIntegerScale([low], 2));
		Assert.AreEqual(2, WaylandOutput.GetIntegerScale([], 2), "Without outputs the previous scale is kept.");
	}

	[TestMethod]
	public void OutputDiagonalNeedsPhysicalSize()
	{
		Assert.IsNull(WaylandOutputInfo.Unknown.DiagonalSizeInInches);
		var output = WaylandOutputInfo.Unknown with { PhysicalWidthMm = 300, PhysicalHeightMm = 400 };
		Assert.AreEqual(500 / 25.4, output.DiagonalSizeInInches!.Value, 1e-9);
	}

	[TestMethod]
	public void StartupFailsInsteadOfHangingOnAnUnresponsiveCompositor()
	{
		using var peer = new ProtocolPeer(answerSync: false);
		var clock = Stopwatch.StartNew();
		Assert.ThrowsExactly<IOException>(() => peer.Connect());
		Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(30), "Startup must give up after its timeout.");
	}

	[TestMethod]
	public void CompositorDisconnectStopsDispatchWithAnIOException()
	{
		var peer = new ProtocolPeer();
		using var display = peer.Connect();
		display.StartDispatch();
		peer.Dispose();
		Assert.IsTrue(SpinWait.SpinUntil(() =>
		{
			lock (display.SyncRoot)
			{
				return display.DispatchError is not null;
			}
		}, TimeSpan.FromSeconds(5)), "A closed connection must stop the dispatch loop.");
		Assert.IsInstanceOfType<IOException>(display.DispatchError);
		Assert.ThrowsExactly<IOException>(() => display.Sync());
	}
}
