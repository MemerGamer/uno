#nullable enable

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.UI.Xaml;
using Uno.UI.Composition.Drawing;
using Uno.UI.Hosting;
using Uno.UI.Runtime.Wayland;
using Uno.UI.Runtime.Wayland.Protocols;
using Uno.UI.Runtime.Wayland.Rendering;

namespace Uno.UI.Runtime.Wayland.Tests;

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
	[DataRow(60)]
	[DataRow(165)]
	public void ConfigureFloodPresentsAtDisplayRate(int refreshRate)
	{
		using var peer = new ProtocolPeer();
		using var display = peer.Connect();
		using var compositor = (WlCompositor)display.Bind(WlCompositor.ProtocolName, WlCompositor.MaximumVersion);
		using var surface = compositor.CreateSurface();
		using var owner = new BlockingCollection<Action>();
		var ui = new Queue<Action>();
		var readiness = new WaylandFrameReadiness();
		var acknowledgements = new List<uint>();
		var presentations = new List<WaylandWindowConfiguration>();
		var draws = 0;
		long received = 0;
		WaylandFrameScheduler? frames = null;
		WaylandWindowConfiguration? applied = null;
		var configures = new WaylandConfigureScheduler(ui.Enqueue, configuration =>
		{
			applied = configuration;
			readiness.ConfigureUI(configuration.Generation, boundsChanged: true);
			readiness.ConfigureRender(configuration.Generation);
			frames!.Invalidate();
		});
		using var scheduler = frames = new WaylandFrameScheduler(surface, () =>
		{
			draws++;
			readiness.BeginFrame();
			var configuration = configures.Current!;
			var layout = configuration.Layout;
			var width = WaylandRenderScale.ToBufferPixels(layout.ContentWidth, configuration.Scale);
			var height = WaylandRenderScale.ToBufferPixels(layout.ContentHeight, configuration.Scale);
			if (!readiness.CanPresent(width, height, width, height))
			{
				return false;
			}
			Assert.AreEqual(applied, configuration, "The buffer must reflect the UI's applied configure.");
			Assert.IsTrue(configuration.SerialSequence <= received, "Only received serials may be acknowledged.");
			Assert.IsFalse(readiness.CanPresent(width - 2, height, width, height), "A stale-size buffer must never commit.");
			configures.Acknowledge(configuration, acknowledgements.Add);
			surface.Commit();
			presentations.Add(configuration);
			configures.Complete(configuration.Generation);
			return true;
		}, () => { }, owner.Add);
		display.StartDispatch();

		void Receive(long generation)
		{
			received = generation;
			configures.Enqueue(new(generation, new(800 + (int)generation, 600 + (int)generation, 32, 8, WaylandCaptionButtons.Close),
				1.25, 150, 1, WaylandToplevelStates.Resizing, "test", true, (uint)generation, generation));
		}
		void ApplyPending()
		{
			Assert.IsTrue(ui.Count <= 1, "Configure arrivals must coalesce into one dispatcher turn.");
			if (ui.TryDequeue(out var apply))
			{
				apply();
			}
		}
		void RecordAndPresent(int tick)
		{
			Assert.IsTrue(readiness.OnFrameRecorded(arrangedAtBounds: true));
			var before = presentations.Count;
			var drawsBefore = draws;
			scheduler.Invalidate();
			if (before != 0)
			{
				Assert.AreEqual(before, presentations.Count, "A pending callback must prevent a second draw.");
				Assert.AreEqual(drawsBefore, draws, "Invalidation must not draw while a callback is pending.");
				peer.CompleteFrame((uint)(tick / 33));
				var deadline = Stopwatch.StartNew();
				while (presentations.Count == before && deadline.Elapsed < TimeSpan.FromSeconds(5))
				{
					Assert.IsTrue(owner.TryTake(out var action, TimeSpan.FromSeconds(5)));
					action();
				}
			}
			Assert.AreEqual(before + 1, presentations.Count);
			Assert.AreEqual(drawsBefore + 1, draws, "Each display opportunity must draw exactly once.");
		}

		// Virtual time: 33,000 ticks/s represents both 200 configures/s and 60/165 display callbacks/s exactly.
		Receive(1);
		ApplyPending();
		RecordAndPresent(0);
		for (var tick = 1; tick <= 33000; tick++)
		{
			if (tick < 33000 && tick % 165 == 0)
			{
				Receive(1 + tick / 165);
			}
			ApplyPending();
			if (tick % (33000 / refreshRate) == 0)
			{
				RecordAndPresent(tick);
			}
		}
		Assert.AreEqual(refreshRate + 1, presentations.Count, "Resizing must keep presenting before the configure stream stops.");
		Assert.IsTrue(acknowledgements.Zip(acknowledgements.Skip(1), (previous, next) => next > previous).All(ordered => ordered));
		Assert.IsTrue(acknowledgements.All(serial => serial <= 200), "Only received serials may be acknowledged.");
		ApplyPending();
		RecordAndPresent(33000 + 33000 / refreshRate);
		Assert.AreEqual(200u, acknowledgements[^1], "The last pending geometry must eventually be presented.");
		Assert.AreEqual(0, ui.Count);
		display.StopDispatch();
	}

	[TestMethod]
	public void RenderTargetUsesAppliedConfigureSizeEvenWhenCanvasSizeIsStale()
	{
		using var peer = new ProtocolPeer();
		using var display = peer.Connect();
		using var compositor = (WlCompositor)display.Bind(WlCompositor.ProtocolName, WlCompositor.MaximumVersion);
		using var surface = compositor.CreateSurface();
		var scale = new WaylandRenderScale(surface, null);
		scale.SetIntegerScale(2);
		using var renderer = new WaylandRenderer(new EmptyHost(), new EmptyFrameGate(), scale, new SizeContext(), null!, surface, _ => { });
		renderer.Configure(801, 601);
		using var target = ((ISwapChain)renderer).AcquireRenderTarget(800, 600);
		Assert.AreEqual((1602, 1202), (target.Width, target.Height));
	}

	private sealed class EmptyHost : IXamlRootHost
	{
		public UIElement? RootElement => null;
		public void InvalidateRender() { }
	}

	private sealed class EmptyFrameGate : IWaylandFrameGate
	{
		public void BeginFrame() { }
		public bool CanPresent(int frameWidth, int frameHeight, int bufferWidth, int bufferHeight) => true;
		public void EndFrame() { }
	}

	private sealed class SizeContext : IWaylandGraphicsContext
	{
		public GraphicsContextKind Kind => GraphicsContextKind.Software;
		public bool Committed => false;
		public IRenderTarget AcquireRenderTarget(int width, int height) => new SizeTarget(width, height);
		public void Present() { }
		public void Dispose() { }
	}

	private sealed class SizeTarget(int width, int height) : IRenderTarget
	{
		public int Width => width;
		public int Height => height;
		public GraphicsColorFormat ColorFormat => GraphicsColorFormat.Bgra8888;
		public void Dispose() { }
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
