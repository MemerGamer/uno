#nullable enable

using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;
using Uno.WinUI.Runtime.Skia.X11;

namespace Uno.WinUI.Runtime.Skia.Wayland.Tests;

[TestClass]
public sealed class Given_X11TextPixelGeometry
{
	[TestMethod]
	[DataRow(0, SKPixelGeometry.Unknown)]
	[DataRow(1, SKPixelGeometry.Unknown)]
	[DataRow(2, SKPixelGeometry.RgbHorizontal)]
	[DataRow(3, SKPixelGeometry.BgrHorizontal)]
	[DataRow(4, SKPixelGeometry.RgbVertical)]
	[DataRow(5, SKPixelGeometry.BgrVertical)]
	[DataRow(6, SKPixelGeometry.Unknown)]
	public void When_RandRSubpixelOrderIsMapped(int order, SKPixelGeometry expected)
		=> Assert.AreEqual(expected, X11TextPixelGeometryPolicy.FromSubpixelOrder((ushort)order));

	[TestMethod]
	public void When_IntersectedOrClonedOutputsDisagree()
	{
		var window = new Rectangle(10, 10, 200, 100);
		var monitor = new Rectangle(0, 0, 1920, 1080);
		X11TextPixelGeometryPolicy.Output[] outputs =
		[
			new(monitor, SKPixelGeometry.RgbHorizontal),
			new(monitor, SKPixelGeometry.RgbHorizontal),
		];
		Assert.AreEqual(SKPixelGeometry.RgbHorizontal, X11TextPixelGeometryPolicy.GetGeometry(window, outputs, SKPixelGeometry.RgbHorizontal));
		outputs[1] = new(monitor, SKPixelGeometry.BgrHorizontal);
		Assert.AreEqual(SKPixelGeometry.Unknown, X11TextPixelGeometryPolicy.GetGeometry(window, outputs, SKPixelGeometry.RgbHorizontal));
		outputs[1] = new(monitor, SKPixelGeometry.Unknown);
		Assert.AreEqual(SKPixelGeometry.Unknown, X11TextPixelGeometryPolicy.GetGeometry(window, outputs, SKPixelGeometry.RgbHorizontal));
		outputs[1] = new(new Rectangle(1920, 0, 1920, 1080), SKPixelGeometry.BgrHorizontal);
		Assert.AreEqual(SKPixelGeometry.RgbHorizontal, X11TextPixelGeometryPolicy.GetGeometry(window, outputs, SKPixelGeometry.RgbHorizontal));
	}

	[TestMethod]
	public void When_WindowMovesOutsideKnownCoverage()
	{
		X11TextPixelGeometryPolicy.Output[] outputs = [new(new Rectangle(0, 0, 1920, 1080), SKPixelGeometry.RgbHorizontal)];
		Assert.AreEqual(SKPixelGeometry.RgbHorizontal, X11TextPixelGeometryPolicy.GetGeometry(new Rectangle(10, 10, 200, 100), outputs, SKPixelGeometry.RgbHorizontal));
		Assert.AreEqual(SKPixelGeometry.Unknown, X11TextPixelGeometryPolicy.GetGeometry(new Rectangle(1900, 10, 200, 100), outputs, SKPixelGeometry.RgbHorizontal));
		Assert.AreEqual(SKPixelGeometry.Unknown, X11TextPixelGeometryPolicy.GetGeometry(new Rectangle(1920, 10, 200, 100), outputs, SKPixelGeometry.RgbHorizontal));
		Assert.AreEqual(SKPixelGeometry.Unknown, X11TextPixelGeometryPolicy.GetGeometry(new Rectangle(10, 10, 200, 100), [], SKPixelGeometry.RgbHorizontal));
		Assert.AreEqual(SKPixelGeometry.Unknown, X11TextPixelGeometryPolicy.GetGeometry(new Rectangle(10, 10, 200, 100), outputs, SKPixelGeometry.Unknown));
	}
}
