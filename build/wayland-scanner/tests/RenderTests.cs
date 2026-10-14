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
	[DataRow(false)]
	[DataRow(true)]
	public void ContentRejectionRetriesWhenANewFrameIsRecorded(bool boundsChanged)
	{
		using var peer = new ProtocolPeer();
		using var display = peer.Connect();
		using var compositor = (WlCompositor)display.Bind(WlCompositor.ProtocolName, WlCompositor.MaximumVersion);
		using var surface = compositor.CreateSurface();
		var owner = new BlockingCollection<Action>();
		var readiness = new WaylandFrameReadiness();
		readiness.ConfigureUI(1, boundsChanged);
		readiness.ConfigureRender(1);
		var canvasSize = 80;
		var draws = 0;
		var submissions = 0;
		using var scheduler = new WaylandFrameScheduler(surface, () =>
		{
			draws++;
			readiness.BeginFrame();
			if (!readiness.CanPresent(canvasSize, canvasSize, 100, 100))
			{
				return WaylandRenderResult.ContentRejected;
			}
			submissions++;
			return WaylandRenderResult.Presented;
		}, () => Assert.Fail("New content must resume presentation before the fallback is needed."), owner.Add);
		scheduler.PresentationReady();
		Assert.AreEqual(1, draws);
		Assert.AreEqual(0, submissions);
		Assert.AreEqual(boundsChanged, readiness.OnFrameRecorded(arrangedAtBounds: true));
		scheduler.ContentReady();
		Assert.AreEqual(2, draws, "Recording the layout must wake a pending content retry immediately.");
		Assert.AreEqual(0, submissions, "Layout readiness alone must not present a stale canvas.");

		canvasSize = 100;
		scheduler.Invalidate();
		Assert.AreEqual(2, draws, "A request alone must not bypass the wait for recorded content.");
		Assert.IsFalse(readiness.OnFrameRecorded(arrangedAtBounds: true));
		scheduler.ContentReady();
		Assert.AreEqual(3, draws, "A later matching picture must retry even without a new layout generation.");
		Assert.AreEqual(1, submissions, "Content must present without executing a fallback tick.");
		scheduler.ContentReady();
		scheduler.BufferAvailable();
		Assert.AreEqual(3, draws, "Readiness without pending work must not request another draw.");
		Assert.IsTrue(owner.TryTake(out var tick, TimeSpan.FromSeconds(5)));
		tick();
		Assert.AreEqual(3, draws, "The previously armed fallback must not redraw presented content.");
		Assert.IsFalse(owner.TryTake(out _, WaylandFrameScheduler.StallInterval * 3), "Successful presentation must stop retries.");
		scheduler.Invalidate();
		scheduler.ContentReady();
		Assert.AreEqual(3, draws, "New content must still wait for the committed frame callback.");
	}

	[TestMethod]
	public void RapidConfiguresAndRecordedFramesRetryWithoutFallbackTicks()
	{
		using var peer = new ProtocolPeer();
		using var display = peer.Connect();
		using var compositor = (WlCompositor)display.Bind(WlCompositor.ProtocolName, WlCompositor.MaximumVersion);
		using var surface = compositor.CreateSurface();
		var owner = new BlockingCollection<Action>();
		var readiness = new WaylandFrameReadiness();
		var configuredSize = 100;
		var canvasSize = 80;
		var draws = 0;
		var submissions = 0;
		using var scheduler = new WaylandFrameScheduler(surface, () =>
		{
			draws++;
			readiness.BeginFrame();
			if (!readiness.CanPresent(canvasSize, canvasSize, configuredSize, configuredSize))
			{
				return WaylandRenderResult.ContentRejected;
			}
			submissions++;
			return WaylandRenderResult.Presented;
		}, () => Assert.Fail("Active resize must not wait for the stall fallback."), owner.Add);
		for (var generation = 1; generation <= 10; generation++)
		{
			configuredSize += 10;
			readiness.ConfigureRender(generation);
			scheduler.PresentationReady();
			Assert.AreEqual(generation * 3 - 2, draws, "Staging each configure must immediately retry the pending frame.");
			readiness.ConfigureUI(generation, boundsChanged: true);
			scheduler.PresentationReady();
			Assert.AreEqual(generation * 3 - 1, draws, "Applying each configure to the root must request a redraw.");
			Assert.IsTrue(readiness.OnFrameRecorded(arrangedAtBounds: true));
			scheduler.ContentReady();
			Assert.AreEqual(generation * 3, draws, "Each recorded frame must retry without running a timer tick.");
			Assert.AreEqual(0, submissions, "A superseded canvas must not acknowledge the configure.");
		}
		canvasSize = configuredSize;
		scheduler.ContentReady();
		Assert.AreEqual(31, draws);
		Assert.AreEqual(1, submissions);
		scheduler.ContentReady();
		Assert.AreEqual(31, draws, "The matching content must be consumed only once.");
		Assert.IsTrue(owner.TryTake(out var tick, TimeSpan.FromSeconds(5)));
		tick();
		Assert.AreEqual(31, draws, "A fallback queued during resize must not add another draw.");
	}

	[TestMethod]
	[DataRow(60, 1)]
	[DataRow(165, 2)]
	public void ContinuousConfigureBurstPresentsAtFrameRate(int refreshRate, int scale)
	{
		using var peer = new ProtocolPeer();
		using var display = peer.Connect();
		using var compositor = (WlCompositor)display.Bind(WlCompositor.ProtocolName, WlCompositor.MaximumVersion);
		using var surface = compositor.CreateSurface();
		var owner = new BlockingCollection<Action>();
		var ui = new Queue<Action>();
		var configures = new WaylandConfigureQueue();
		var readiness = new WaylandFrameReadiness();
		var received = new Dictionary<uint, WaylandDecorationLayout>();
		var acknowledgements = new List<uint>();
		WaylandWindowConfigure? bufferConfigure = null;
		WaylandFrameScheduler scheduler = null!;
		var canvasWidth = 0;
		var canvasHeight = 0;
		var draws = 0;
		var presentations = 0;
		var framePending = false;
		var presentedOlderConfigure = false;
		uint latestSerial = 0;

		void ApplyPending()
		{
			bufferConfigure = configures.TakePending()!;
			Assert.IsNotNull(bufferConfigure);
			readiness.ConfigureUI(bufferConfigure.Generation, boundsChanged: true);
			readiness.ConfigureRender(bufferConfigure.Generation);
			scheduler.PresentationReady();
		}
		void DrainUI()
		{
			while (ui.TryDequeue(out var apply))
			{
				apply();
			}
		}
		using var frameScheduler = new WaylandFrameScheduler(surface, () =>
		{
			draws++;
			var configure = bufferConfigure!;
			var width = configure.Layout.ContentWidth * scale;
			var height = configure.Layout.ContentHeight * scale;
			readiness.BeginFrame();
			if (!readiness.CanPresent(canvasWidth, canvasHeight, width, height))
			{
				return WaylandRenderResult.ContentRejected;
			}
			Assert.AreSame(configure, configures.Current);
			Assert.AreEqual(width, canvasWidth, "The buffer must match the completed layout.");
			Assert.AreEqual(height, canvasHeight);
			configures.AcknowledgeCurrent(serial =>
			{
				Assert.IsTrue(received.ContainsKey(serial), "Never acknowledge a serial not yet received.");
				Assert.IsTrue(acknowledgements.Count == 0 || acknowledgements[^1] < serial);
				Assert.AreEqual(received[serial], configure.Layout, "The buffer must match the acknowledged geometry, including decorations.");
				acknowledgements.Add(serial);
				presentedOlderConfigure |= serial < latestSerial;
			});
			surface.Commit();
			presentations++;
			framePending = true;
			if (configures.Complete(configure.Generation))
			{
				ui.Enqueue(ApplyPending);
			}
			return WaylandRenderResult.Presented;
		}, () => Assert.Fail("Continuous resize must not need the liveness fallback."), owner.Add);
		scheduler = frameScheduler;
		display.StartDispatch();

		void PresentFrame()
		{
			var waitingForCallback = framePending;
			var before = presentations;
			Assert.IsTrue(readiness.OnFrameRecorded(arrangedAtBounds: true));
			canvasWidth = bufferConfigure!.Layout.ContentWidth * scale;
			canvasHeight = bufferConfigure.Layout.ContentHeight * scale;
			scheduler.ContentReady();
			if (waitingForCallback)
			{
				Assert.AreEqual(before, presentations, "Recorded content must respect the display's frame callback.");
				framePending = false;
				peer.CompleteFrame();
				var deadline = Stopwatch.StartNew();
				while (presentations == before && deadline.Elapsed < TimeSpan.FromSeconds(5))
				{
					Assert.IsTrue(owner.TryTake(out var callback, TimeSpan.FromSeconds(5)), "Frame callback did not reach the owner.");
					callback();
				}
			}
			Assert.AreEqual(before + 1, presentations, "Each frame opportunity must present exactly once, even while configures keep arriving.");
			var after = draws;
			scheduler.ContentReady();
			scheduler.BufferAvailable();
			Assert.AreEqual(after, draws, "Readiness notifications must not draw twice.");
			DrainUI();
		}

		// Virtual time: pointer configures every 5 ms, layout completes at the display rate.
		var nextConfigureMs = 0;
		for (var frame = 1; frame <= refreshRate; frame++)
		{
			while (nextConfigureMs < frame * 1000d / refreshRate)
			{
				latestSerial++;
				var layout = new WaylandDecorationLayout(800 + (int)latestSerial, 600 + (int)latestSerial, 32, 8, WaylandCaptionButtons.Close);
				received.Add(latestSerial, layout);
				configures.Receive(latestSerial);
				if (configures.Enqueue(layout, scale, (uint)(scale * 120), scale, WaylandToplevelStates.Resizing, configured: true))
				{
					ui.Enqueue(ApplyPending);
				}
				DrainUI();
				nextConfigureMs += 5;
			}
			PresentFrame();
		}
		Assert.AreEqual(200, received.Count);
		Assert.AreEqual(refreshRate, presentations, "Configure traffic must not reduce presentation below the simulated refresh rate.");
		Assert.IsTrue(presentedOlderConfigure, "A completed layout must present even after a newer configure arrives.");
		if (acknowledgements[^1] != latestSerial)
		{
			PresentFrame();
		}
		Assert.AreEqual(latestSerial, acknowledgements[^1], "The final pending size must also be presented.");
		display.StopDispatch();
	}

	[TestMethod]
	[DataRow(true)]
	[DataRow(false)]
	public void ContentRejectionResumesOnFallbackTicksWithoutReadinessWakeups(bool contentBecomesReady)
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
		scheduler.ContentReady();
		Assert.AreEqual(1, attempts, "Recorded content must not bypass buffer availability.");
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
