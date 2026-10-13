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
	public void MaximizedLayoutHasNoResizeBorder()
	{
		var layout = new WaylandDecorationLayout(800, 600, 32, 0, WaylandCaptionButtons.Close);
		Assert.AreEqual((0, -32, 800, 632), (layout.SurfaceX, layout.SurfaceY, layout.SurfaceWidth, layout.SurfaceHeight));
		Assert.AreEqual((WaylandDecorationPart.TitleBar, 0u), layout.HitTest(0, 0));
		Assert.AreEqual((WaylandDecorationPart.Close, 0u), layout.HitTest(799, 31));
		Assert.AreEqual((WaylandDecorationPart.None, 0u), layout.HitTest(400, 40));
	}
}
