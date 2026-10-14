#nullable enable
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.WinUI.Runtime.Skia.Wayland;

namespace Wayland.Scanner.Tests;

[TestClass]
public sealed class WindowLogicTests
{
	private const uint Top = 1, Bottom = 2, Left = 4, Right = 8;

	private static byte[] Values(params uint[] values)
	{
		var bytes = new byte[values.Length * 4];
		for (var i = 0; i < values.Length; i++)
		{
			BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), values[i]);
		}
		return bytes;
	}

	[TestMethod]
	public void PresenterChangesIncludeSameSizeAndMinimizedTransitions()
	{
		Assert.IsTrue(WaylandToplevelStateParser.HasPresenterChanged(WaylandToplevelStates.None, WaylandToplevelStates.Maximized, false, false));
		Assert.IsTrue(WaylandToplevelStateParser.HasPresenterChanged(WaylandToplevelStates.Maximized, WaylandToplevelStates.Fullscreen, false, false));
		Assert.IsTrue(WaylandToplevelStateParser.HasPresenterChanged(WaylandToplevelStates.None, WaylandToplevelStates.Activated, true, false));
		Assert.IsTrue(WaylandToplevelStateParser.HasPresenterChanged(WaylandToplevelStates.None, WaylandToplevelStates.Maximized, true, true));
		Assert.IsFalse(WaylandToplevelStateParser.HasPresenterChanged(WaylandToplevelStates.None, WaylandToplevelStates.Activated | WaylandToplevelStates.Resizing, false, false));
	}

	[TestMethod]
	public void ToplevelStatesParseKnownValuesOnly()
	{
		Assert.AreEqual(WaylandToplevelStates.None, WaylandToplevelStateParser.ParseStates([]));
		Assert.AreEqual(
			WaylandToplevelStates.Maximized | WaylandToplevelStates.Activated | WaylandToplevelStates.TiledLeft,
			WaylandToplevelStateParser.ParseStates(Values(1, 4, 5, 99, 13)));
		// A truncated trailing value is ignored.
		Assert.AreEqual(WaylandToplevelStates.Fullscreen, WaylandToplevelStateParser.ParseStates(Values(2).Concat(new byte[] { 4, 0 }).ToArray()));
	}

	[TestMethod]
	public void WindowCapabilitiesParse()
	{
		Assert.AreEqual(
			WaylandWindowCapabilities.WindowMenu | WaylandWindowCapabilities.Minimize,
			WaylandToplevelStateParser.ParseCapabilities(Values(1, 4, 0, 7)));
	}

	[TestMethod]
	public void DecorationSurfaceSurroundsGeometry()
	{
		var layout = new WaylandDecorationLayout(800, 600, 32, 8, WaylandCaptionButtons.Minimize | WaylandCaptionButtons.Maximize | WaylandCaptionButtons.Close);
		Assert.AreEqual((-8, -40, 816, 648), (layout.SurfaceX, layout.SurfaceY, layout.SurfaceWidth, layout.SurfaceHeight));
		Assert.AreEqual((-32, 632), (layout.GeometryY, layout.GeometryHeight));
		Assert.IsTrue(layout.TryGetButton(WaylandDecorationPart.Close, out var x, out var y, out var width, out var height));
		Assert.AreEqual((762, 8, 46, 32), (x, y, width, height));
		Assert.IsTrue(layout.TryGetButton(WaylandDecorationPart.Minimize, out x, out _, out _, out _));
		Assert.AreEqual(670, x);
		Assert.AreEqual(670, layout.ButtonsLeft);
		Assert.IsFalse(default(WaylandDecorationLayout).IsVisible);
	}

	[TestMethod]
	public void DecorationHitTest()
	{
		var layout = new WaylandDecorationLayout(800, 600, 32, 8, WaylandCaptionButtons.Maximize | WaylandCaptionButtons.Close);
		Assert.AreEqual((WaylandDecorationPart.TitleBar, 0u), layout.HitTest(100, 20));
		Assert.AreEqual((WaylandDecorationPart.Close, 0u), layout.HitTest(780, 20));
		Assert.AreEqual((WaylandDecorationPart.Maximize, 0u), layout.HitTest(740, 20));
		// No minimize button: its slot belongs to the title bar.
		Assert.AreEqual((WaylandDecorationPart.TitleBar, 0u), layout.HitTest(690, 20));
		Assert.AreEqual((WaylandDecorationPart.None, 0u), layout.HitTest(100, 300));
		Assert.AreEqual((WaylandDecorationPart.ResizeEdge, Top), layout.HitTest(400, 2));
		Assert.AreEqual((WaylandDecorationPart.ResizeEdge, Left), layout.HitTest(2, 300));
		Assert.AreEqual((WaylandDecorationPart.ResizeEdge, Top | Left), layout.HitTest(2, 2));
		// Corner zones extend along the edges.
		Assert.AreEqual((WaylandDecorationPart.ResizeEdge, Top | Left), layout.HitTest(20, 2));
		Assert.AreEqual((WaylandDecorationPart.ResizeEdge, Bottom | Right), layout.HitTest(815, 647));
		Assert.AreEqual((WaylandDecorationPart.ResizeEdge, Bottom | Right), layout.HitTest(812, 630));
		Assert.AreEqual((WaylandDecorationPart.None, 0u), layout.HitTest(-1, 300));
	}

	[TestMethod]
	public void PendingConfiguresCoalesceUntilFrameOpportunity()
	{
		var queue = new WaylandConfigureQueue();
		var first = new WaylandDecorationLayout(800, 600, 32, 8, WaylandCaptionButtons.Close);
		queue.Receive(10);
		Assert.IsTrue(queue.Enqueue(first, 1, 0, 1, WaylandToplevelStates.Resizing, configured: true, notifyActivation: true));
		queue.Receive(11);
		Assert.IsFalse(queue.Enqueue(first, 1, 0, 1, WaylandToplevelStates.Resizing, configured: true));
		var active = queue.TakePending()!;
		Assert.AreEqual(11u, active.Serial);
		Assert.IsTrue(active.NotifyActivation, "Coalescing a scale or decoration update must preserve activation notification.");

		for (uint serial = 12; serial <= 200; serial++)
		{
			queue.Receive(serial);
			Assert.IsFalse(queue.Enqueue(first with { ContentWidth = (int)serial + 800 }, 2, 240, 2, WaylandToplevelStates.Resizing, configured: true));
			Assert.IsNull(queue.TakePending(), "A newer configure must wait for a frame opportunity.");
			Assert.AreSame(active, queue.Current);
		}
		Assert.IsFalse(queue.FrameOpportunity(active.Generation + 1));
		Assert.IsTrue(queue.FrameOpportunity(active.Generation), "A frame opportunity must advance without submission or acknowledgement.");
		Assert.IsFalse(queue.FrameOpportunity(active.Generation), "A repeated callback must not post a second UI configure.");
		var acknowledgements = new List<uint>();
		queue.AcknowledgeCurrent(acknowledgements.Add);
		queue.AcknowledgeCurrent(acknowledgements.Add);
		CollectionAssert.AreEqual(new uint[] { 11 }, acknowledgements, "Only the active geometry's serial may be acknowledged, exactly once.");
		var newest = queue.TakePending()!;
		Assert.IsFalse(queue.FrameOpportunity(active.Generation), "An old callback must not release the new generation.");
		Assert.AreEqual(200u, newest.Serial);
		Assert.AreEqual(1000, newest.Layout.ContentWidth);
		Assert.AreEqual(2d, newest.Scale);
		Assert.AreEqual(240u, newest.PreferredScale);
		queue.AcknowledgeCurrent(acknowledgements.Add);
		CollectionAssert.AreEqual(new uint[] { 11, 200 }, acknowledgements);
		Assert.IsFalse(queue.FrameOpportunity(newest.Generation));
		Assert.IsNull(queue.TakePending());
	}

	[TestMethod]
	public void StateOnlyAcknowledgementCannotConsumeAnAppliedResizeSerial()
	{
		var queue = new WaylandConfigureQueue();
		var presented = new WaylandDecorationLayout(800, 600, 0, 0, WaylandCaptionButtons.None);
		var resizing = presented with { ContentWidth = 900 };
		queue.Receive(1);
		queue.Enqueue(resizing, 1, 0, 1, WaylandToplevelStates.Resizing, configured: true);
		var active = queue.TakePending()!;
		queue.Receive(2);
		queue.Enqueue(presented, 1, 0, 1, WaylandToplevelStates.None, configured: true);
		Assert.IsFalse(queue.CanAcknowledgeLatest(presented, 1), "Returning to the presented size must wait for the intervening resize.");
		var acknowledgements = new List<uint>();
		queue.AcknowledgeCurrent(acknowledgements.Add);
		Assert.IsTrue(queue.FrameOpportunity(active.Generation));
		Assert.IsFalse(queue.CanAcknowledgeLatest(presented, 1), "Opening a frame slot must not change the applied geometry.");
		queue.TakePending();
		Assert.IsTrue(queue.CanAcknowledgeLatest(presented, 1));
		Assert.IsFalse(queue.CanAcknowledgeLatest(presented, 2), "Scale is part of the committed geometry.");
		queue.Receive(3);
		queue.AcknowledgeLatest(acknowledgements.Add);
		queue.AcknowledgeCurrent(acknowledgements.Add);
		CollectionAssert.AreEqual(new uint[] { 1, 3 }, acknowledgements, "An already consumed serial must never be acknowledged again.");
	}

	[TestMethod]
	public void ConfigureAcknowledgementsFollowReceiptOrderAcrossSerialWraparound()
	{
		var queue = new WaylandConfigureQueue();
		var layout = new WaylandDecorationLayout(800, 600, 0, 0, WaylandCaptionButtons.None);
		var acknowledgements = new List<uint>();
		queue.AcknowledgeLatest(acknowledgements.Add);
		Assert.AreEqual(0, acknowledgements.Count, "A serial must be received before it can be acknowledged.");
		foreach (var serial in new uint[] { uint.MaxValue - 1, uint.MaxValue, 0, 1 })
		{
			queue.Receive(serial);
			Assert.IsTrue(queue.Enqueue(layout, 1, 0, 1, WaylandToplevelStates.None, configured: true));
			var active = queue.TakePending()!;
			queue.AcknowledgeCurrent(acknowledgements.Add);
			queue.AcknowledgeLatest(acknowledgements.Add);
			Assert.IsFalse(queue.FrameOpportunity(active.Generation));
		}
		CollectionAssert.AreEqual(new uint[] { uint.MaxValue - 1, uint.MaxValue, 0, 1 }, acknowledgements);
	}

	[TestMethod]
	public void ResizeWaitsForFrameRecordedAfterLayout()
	{
		var readiness = new WaylandFrameReadiness();
		Assert.IsFalse(readiness.ConfigureUI(1, boundsChanged: false));
		readiness.ConfigureRender(1);
		readiness.BeginFrame();
		Assert.IsTrue(readiness.CanPresent(800, 600, 800, 600));

		Assert.IsTrue(readiness.ConfigureUI(2, boundsChanged: true));
		readiness.ConfigureRender(2);
		// The canvas already has the new size, but the recorded picture still has the old layout.
		readiness.BeginFrame();
		Assert.IsFalse(readiness.CanPresent(900, 700, 900, 700));
		Assert.IsFalse(readiness.OnFrameRecorded(arrangedAtBounds: false));
		readiness.BeginFrame();
		Assert.IsFalse(readiness.CanPresent(900, 700, 900, 700));
		Assert.IsTrue(readiness.OnFrameRecorded(arrangedAtBounds: true));
		readiness.BeginFrame();
		Assert.IsTrue(readiness.CanPresent(900, 700, 900, 700));
		Assert.IsFalse(readiness.OnFrameRecorded(arrangedAtBounds: true));
	}

	[TestMethod]
	public void PictureTakenBeforeLayoutWasRecordedIsNotPresented()
	{
		var readiness = new WaylandFrameReadiness();
		readiness.ConfigureUI(1, boundsChanged: true);
		readiness.ConfigureRender(1);
		readiness.BeginFrame();
		// The UI records the new layout while the render thread still draws the older picture.
		Assert.IsTrue(readiness.OnFrameRecorded(arrangedAtBounds: true));
		Assert.IsFalse(readiness.CanPresent(800, 600, 800, 600));
		readiness.BeginFrame();
		Assert.IsTrue(readiness.CanPresent(800, 600, 800, 600));
	}

	[TestMethod]
	public void ConfigureDuringLayoutWaitKeepsWaiting()
	{
		var readiness = new WaylandFrameReadiness();
		Assert.IsTrue(readiness.ConfigureUI(1, boundsChanged: true));
		// A state-only configure must not release the resize whose layout was not recorded yet.
		Assert.IsTrue(readiness.ConfigureUI(2, boundsChanged: false));
		readiness.ConfigureRender(2);
		readiness.BeginFrame();
		Assert.IsFalse(readiness.CanPresent(800, 600, 800, 600));
		Assert.IsTrue(readiness.OnFrameRecorded(arrangedAtBounds: true));
		readiness.BeginFrame();
		Assert.IsTrue(readiness.CanPresent(800, 600, 800, 600));
	}

	[TestMethod]
	public void SupersededGenerationNeverPresents()
	{
		var readiness = new WaylandFrameReadiness();
		readiness.ConfigureUI(2, boundsChanged: false);
		readiness.ConfigureRender(1);
		for (var i = 0; i < 2 * WaylandFrameReadiness.MaxStaleFrames; i++)
		{
			readiness.BeginFrame();
			Assert.IsFalse(readiness.CanPresent(800, 600, 800, 600));
		}
		readiness.ConfigureRender(2);
		readiness.BeginFrame();
		Assert.IsTrue(readiness.CanPresent(800, 600, 800, 600));
	}

	[TestMethod]
	public void LayoutWaitHasSafetyValveButStaleSizesNeverPresent()
	{
		var readiness = new WaylandFrameReadiness();
		readiness.ConfigureUI(1, boundsChanged: true);
		for (var i = 0; i < WaylandFrameReadiness.MaxStaleFrames; i++)
		{
			Assert.IsFalse(readiness.OnFrameRecorded(arrangedAtBounds: false));
		}
		Assert.IsTrue(readiness.OnFrameRecorded(arrangedAtBounds: false));

		readiness.ConfigureRender(1);
		readiness.BeginFrame();
		Assert.IsFalse(readiness.CanPresent(100, 100, 101, 101));
		for (var i = 0; i < 2 * WaylandFrameReadiness.MaxStaleFrames; i++)
		{
			readiness.BeginFrame();
			Assert.IsFalse(readiness.CanPresent(80, 80, 101, 101));
		}
		readiness.BeginFrame();
		Assert.IsTrue(readiness.CanPresent(101, 101, 101, 101));
	}

	[TestMethod]
	public void TinyDecorationEdgesAreValid()
	{
		uint[] validEdges = [Top, Bottom, Left, Right, Top | Left, Top | Right, Bottom | Left, Bottom | Right];
		for (var width = 1; width <= 33; width++)
		{
			for (var height = 1; height <= 33; height++)
			{
				var layout = new WaylandDecorationLayout(width, height, 0, 8, WaylandCaptionButtons.None);
				for (var x = 0; x < layout.SurfaceWidth; x++)
				{
					for (var y = 0; y < layout.SurfaceHeight; y++)
					{
						var hit = layout.HitTest(x, y);
						if (hit.Part == WaylandDecorationPart.ResizeEdge)
						{
							CollectionAssert.Contains(validEdges, hit.Edges);
						}
					}
				}
			}
		}
		Assert.AreEqual((WaylandDecorationPart.ResizeEdge, Top | Right), new WaylandDecorationLayout(20, 20, 0, 8, WaylandCaptionButtons.None).HitTest(18, 2));
	}

	[TestMethod]
	public void TitleBarFitsConstrainedGeometry()
	{
		for (var height = 1; height <= 64; height++)
		{
			var titleBar = WaylandDecorationLayout.FitTitleBar(height, 32);
			var layout = new WaylandDecorationLayout(20, height - titleBar, titleBar, 0, WaylandCaptionButtons.Close);
			Assert.AreEqual(height, layout.GeometryHeight);
			Assert.IsTrue(layout.ContentHeight >= 1);
		}
	}

	[TestMethod]
	public void MaximizedLayoutHasNoResizeBorder()
	{
		var layout = new WaylandDecorationLayout(800, 600, 32, 0, WaylandCaptionButtons.Close);
		Assert.AreEqual((0, -32, 800, 632), (layout.SurfaceX, layout.SurfaceY, layout.SurfaceWidth, layout.SurfaceHeight));
		Assert.AreEqual((WaylandDecorationPart.TitleBar, 0u), layout.HitTest(0, 0));
		Assert.AreEqual((WaylandDecorationPart.Close, 0u), layout.HitTest(799, 31));
		Assert.AreEqual((WaylandDecorationPart.None, 0u), layout.HitTest(400, 40));
	}
}
