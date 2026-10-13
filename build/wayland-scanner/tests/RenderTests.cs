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
		Assert.AreEqual(configs[2], WaylandEglConfig.Select(configs, config => config == configs[0] ? 1 : config == configs[1] ? null : 0, _ => 0, _ => 0));
	}

	[TestMethod]
	public void EglConfigRejectsClampedOrUnknownSwapIntervals()
	{
		IntPtr[] configs = [new(1), new(2)];
		Assert.ThrowsExactly<NotSupportedException>(() => WaylandEglConfig.Select(configs, config => config == configs[0] ? 1 : null, _ => 0, _ => 0));
		Assert.ThrowsExactly<NotSupportedException>(() => WaylandEglConfig.Select([], _ => 0, _ => 0, _ => 0));
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
		using var scheduler = new WaylandFrameScheduler(surface, () =>
		{
			renders++;
			return WaylandRenderResult.Presented;
		}, () => stalls++, owner.Add);
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
		using var scheduler = new WaylandFrameScheduler(surface, () => WaylandRenderResult.Presented, () => stalls++, owner.Add);
		scheduler.Invalidate();
		Assert.IsFalse(owner.TryTake(out _, WaylandFrameScheduler.StallInterval * 3));
		Assert.AreEqual(0, stalls);
	}

	[TestMethod]
	[DataRow(true)]
	[DataRow(false)]
	public void ContentRejectionResumesOnPacedTicksWithoutReadinessWakeups(bool contentBecomesReady)
	{
		using var peer = new ProtocolPeer();
		using var display = peer.Connect();
		using var compositor = (WlCompositor)display.Bind(WlCompositor.ProtocolName, WlCompositor.MaximumVersion);
		using var surface = compositor.CreateSurface();
		var owner = new BlockingCollection<Action>();
		var readiness = new WaylandFrameReadiness();
		readiness.ConfigureUI(1, boundsChanged: false);
		readiness.ConfigureRender(1);
		var canvasSize = 80;
		var draws = 0;
		var submissions = 0;
		var retryClock = Stopwatch.StartNew();
		using var scheduler = new WaylandFrameScheduler(surface, () =>
		{
			if (draws > 0)
			{
				Assert.IsTrue(retryClock.Elapsed >= WaylandFrameScheduler.StallInterval / 2, "Content retries must not form an immediate loop.");
			}
			retryClock.Restart();
			draws++;
			readiness.BeginFrame();
			if (!readiness.CanPresent(canvasSize, canvasSize, 100, 100))
			{
				return WaylandRenderResult.ContentRejected;
			}
			submissions++;
			return WaylandRenderResult.Presented;
		}, () => Assert.Fail("Content rejection must retry presentation on the paced tick."), owner.Add);
		scheduler.Invalidate();
		Assert.AreEqual(1, draws);
		Assert.AreEqual(0, submissions);
		for (var i = 0; i < 10; i++)
		{
			scheduler.Invalidate();
		}
		Assert.AreEqual(1, draws, "Invalidations must not bypass content retry pacing.");
		var ticks = 0;
		while (submissions == 0 && ticks < WaylandFrameReadiness.MaxStaleFrames)
		{
			if (contentBecomesReady && ticks == 2)
			{
				canvasSize = 100;
			}
			var previousDraws = draws;
			Assert.IsTrue(owner.TryTake(out var tick, TimeSpan.FromSeconds(5)), "Pending content must retry without a readiness wakeup.");
			tick();
			ticks++;
			Assert.AreEqual(previousDraws + 1, draws, "Each paced tick must retry only once.");
		}
		Assert.AreEqual(1, submissions, "Correct content or the stale-size safety valve must resume presentation.");
		Assert.AreEqual(contentBecomesReady ? 3 : WaylandFrameReadiness.MaxStaleFrames, ticks);
		Assert.IsFalse(owner.TryTake(out _, WaylandFrameScheduler.StallInterval * 3), "Successful presentation must stop content retries.");
		scheduler.Invalidate();
		Assert.AreEqual(ticks + 1, draws, "Presentation must resume normal frame callback pacing.");
	}

	[TestMethod]
	public void BufferUnavailableWaitsForReleaseWithoutDrawing()
	{
		using var peer = new ProtocolPeer();
		using var display = peer.Connect();
		using var compositor = (WlCompositor)display.Bind(WlCompositor.ProtocolName, WlCompositor.MaximumVersion);
		using var surface = compositor.CreateSurface();
		var owner = new BlockingCollection<Action>();
		var bufferAvailable = false;
		var attempts = 0;
		var draws = 0;
		var stalls = 0;
		using var scheduler = new WaylandFrameScheduler(surface, () =>
		{
			attempts++;
			if (!bufferAvailable)
			{
				return WaylandRenderResult.BufferUnavailable;
			}
			draws++;
			return WaylandRenderResult.Presented;
		}, () => stalls++, owner.Add);
		scheduler.Invalidate();
		for (var i = 0; i < 3; i++)
		{
			scheduler.Invalidate();
			Assert.IsTrue(owner.TryTake(out var tick, TimeSpan.FromSeconds(5)));
			tick();
			Assert.AreEqual(i + 1, stalls);
			Assert.AreEqual(1, attempts, "Stall ticks must not retry buffer acquisition.");
			Assert.AreEqual(0, draws, "Buffer unavailability must not draw content.");
		}
		Assert.IsFalse(owner.TryTake(out _, WaylandFrameScheduler.StallInterval * 3));
		bufferAvailable = true;
		scheduler.BufferAvailable();
		Assert.AreEqual(2, attempts);
		Assert.AreEqual(1, draws);
		scheduler.BufferAvailable();
		Assert.AreEqual(2, attempts, "A release must consume the coalesced pending frame only once.");
	}

	[TestMethod]
	[DataRow(false, false)]
	[DataRow(true, false)]
	[DataRow(false, true)]
	[DataRow(true, true)]
	public void RejectedCommitWaitsForAvailabilityAfterAtMostOneRecoveryRetry(bool recoverContext, bool layoutReady)
	{
		using var peer = new ProtocolPeer();
		using var display = peer.Connect();
		using var compositor = (WlCompositor)display.Bind(WlCompositor.ProtocolName, WlCompositor.MaximumVersion);
		using var surface = compositor.CreateSurface();
		var owner = new BlockingCollection<Action>();
		var draws = 0;
		var submissions = 0;
		var stalls = 0;
		var decorationBufferAvailable = false;
		bool PrepareCommit(Func<bool> submit) => decorationBufferAvailable && submit();
		using var scheduler = new WaylandFrameScheduler(surface, () =>
		{
			draws++;
			if (recoverContext && draws == 1)
			{
				return WaylandRenderResult.ContextRecovered;
			}
			var presented = PrepareCommit(() =>
			{
				submissions++;
				return true;
			});
			return presented ? WaylandRenderResult.Presented : WaylandRenderResult.BufferUnavailable;
		}, () => stalls++, owner.Add);
		scheduler.Invalidate();
		if (recoverContext)
		{
			Assert.IsTrue(owner.TryTake(out var retry, TimeSpan.FromSeconds(5)));
			retry();
		}
		var expectedDraws = recoverContext ? 2 : 1;
		Assert.AreEqual(expectedDraws, draws);
		Assert.AreEqual(0, submissions);
		for (var i = 0; i < 10; i++)
		{
			scheduler.Invalidate();
		}
		Assert.AreEqual(expectedDraws, draws, "Animation invalidations must wait for presentation readiness.");
		Assert.IsTrue(owner.TryTake(out var tick, TimeSpan.FromSeconds(5)));
		tick();
		Assert.AreEqual(1, stalls, "A rejected commit must yield to the paced stall callback.");
		Assert.AreEqual(expectedDraws, draws, "Stalling must not redraw a rejected presentation.");
		Assert.IsFalse(owner.TryTake(out _, WaylandFrameScheduler.StallInterval * 3), "Rejection must not queue immediate retries.");
		for (var i = 0; i < 10; i++)
		{
			scheduler.Invalidate();
		}
		Assert.AreEqual(expectedDraws, draws, "Animation invalidations after a stall must still wait for availability.");
		Assert.AreEqual(0, submissions);
		decorationBufferAvailable = true;
		if (layoutReady)
		{
			scheduler.PresentationReady();
		}
		else
		{
			scheduler.BufferAvailable();
		}
		Assert.AreEqual(expectedDraws + 1, draws);
		Assert.AreEqual(1, submissions);
		scheduler.BufferAvailable();
		Assert.AreEqual(expectedDraws + 1, draws, "Availability must consume the coalesced pending frame only once.");
		scheduler.Invalidate();
		Assert.AreEqual(expectedDraws + 1, draws, "The submitted frame must wait for its frame callback.");
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
