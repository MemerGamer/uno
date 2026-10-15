// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;
using Uno.WinUI.Runtime.Skia.Linux;
using Uno.WinUI.Runtime.Skia.Wayland;
using Uno.WinUI.Runtime.Skia.Wayland.Rendering;

namespace Uno.WinUI.Runtime.Skia.Wayland.Tests;

[TestClass]
public sealed class RenderLogicTests
{
	[TestMethod]
	[DataRow(801, 802, 120u)]
	[DataRow(801, 802, 150u)]
	[DataRow(801, 802, 180u)]
	[DataRow(801, 802, 240u)]
	[DataRow(30, 62, 122u)]
	public void CanvasAndBufferShareIntegerContentGeometry(int width, int geometryHeight, uint preferredScale)
	{
		// CSD title height is excluded from the parent buffer; its margins live on the subsurface.
		var titleHeight = WaylandDecorationLayout.FitTitleBar(geometryHeight, 32);
		var layout = new WaylandDecorationLayout(width, geometryHeight - titleHeight, titleHeight, 8, WaylandCaptionButtons.Close);
		var scale = WaylandRenderScale.Compute(true, preferredScale, 1);
		var buffer = (WaylandRenderScale.ToBufferPixels(layout.ContentWidth, scale),
			WaylandRenderScale.ToBufferPixels(layout.ContentHeight, scale));
		var canvas = WaylandRenderScale.GetCanvasSize(buffer,
			Math.Round(layout.ContentWidth * (float)scale), Math.Round(layout.ContentHeight * (float)scale));
		Assert.AreEqual(buffer, canvas);
		if (preferredScale == 180)
		{
			Assert.AreEqual(1202d, Math.Round(layout.ContentWidth * (float)scale));
			Assert.AreEqual(1202, canvas.Width);
			Assert.AreEqual(1155, canvas.Height);
		}
	}

	[TestMethod]
	public void HalfEvenXamlSizeCannotShrinkConfiguredCanvasByOnePixel()
	{
		var buffer = (WaylandRenderScale.ToBufferPixels(802, 1.25), WaylandRenderScale.ToBufferPixels(770, 1.25));
		Assert.AreEqual((1003, 963), buffer);
		Assert.AreEqual((1002d, 962d), (Math.Round(802 * 1.25), Math.Round(770 * 1.25)));
		Assert.AreEqual(buffer, WaylandRenderScale.GetCanvasSize(buffer, 1002, 962));
		Assert.AreEqual((1280, 800), WaylandRenderScale.GetCanvasSize((1280, 800), 1280, 799));
	}

	[TestMethod]
	public void ConfigureFallbackUsesOutputRefreshPeriod()
	{
		Assert.AreEqual(1000d / 165, (WaylandOutputInfo.Unknown with { RefreshMilliHz = 165000 }).FrameInterval.TotalMilliseconds, 0.0001);
		Assert.AreEqual(1000d / 60, WaylandOutputInfo.Unknown.FrameInterval.TotalMilliseconds, 0.0001);
	}

	[TestMethod]
	[DataRow(2, SKPixelGeometry.RgbHorizontal)]
	[DataRow(3, SKPixelGeometry.BgrHorizontal)]
	[DataRow(4, SKPixelGeometry.RgbVertical)]
	[DataRow(5, SKPixelGeometry.BgrVertical)]
	public void TextGeometrySupportsEachPhysicalOrder(int subpixel, SKPixelGeometry geometry)
	{
		var output = WaylandOutputInfo.Unknown with { Subpixel = subpixel, Transform = 0 };
		Assert.AreEqual(geometry, WaylandWindowWrapper.GetTextPixelGeometry([output], LinuxFontRenderingSettings.Default, 1));
	}

	[TestMethod]
	[DataRow("auto")]
	[DataRow("on")]
	[DataRow("off")]
	public void TextGeometryRequiresMatchingUntransformedOutputsAndIntegerPresentation(string mode)
	{
		var rgb = WaylandOutputInfo.Unknown with { Subpixel = 2, Transform = 0 };
		var settings = LinuxFontRenderingSettings.FromPattern(true, true, 1, 1, 1, false).WithOverrides(null, null, lcd: mode);
		var expected = mode == "off" ? SKPixelGeometry.Unknown : SKPixelGeometry.RgbHorizontal;
		Assert.AreEqual(expected, WaylandWindowWrapper.GetTextPixelGeometry([rgb], settings, 1));
		Assert.AreEqual(expected, WaylandWindowWrapper.GetTextPixelGeometry([rgb with { Scale = 2 }], settings, 2));
		Assert.AreEqual(SKPixelGeometry.Unknown, WaylandWindowWrapper.GetTextPixelGeometry([], settings, 1));
		Assert.AreEqual(SKPixelGeometry.Unknown, WaylandWindowWrapper.GetTextPixelGeometry([rgb, rgb with { Subpixel = 3 }], settings, 1));
		foreach (var transform in new[] { -1, 1, 2, 3, 4, 5, 6, 7 })
		{
			Assert.AreEqual(SKPixelGeometry.Unknown, WaylandWindowWrapper.GetTextPixelGeometry([rgb with { Transform = transform }], settings, 1));
		}
		foreach (var scale in new[] { 0d, 0.5, 1.25, 1.5, double.NaN, double.PositiveInfinity })
		{
			Assert.AreEqual(SKPixelGeometry.Unknown, WaylandWindowWrapper.GetTextPixelGeometry([rgb], settings, scale));
		}
		Assert.AreEqual(SKPixelGeometry.Unknown, WaylandWindowWrapper.GetTextPixelGeometry([rgb, rgb with { Scale = 2 }], settings, 2));
		Assert.AreEqual(SKPixelGeometry.Unknown, WaylandWindowWrapper.GetTextPixelGeometry([rgb], settings with { Antialias = false }, 1));
		settings = settings.WithOverrides(null, "rgb");
		Assert.AreEqual(SKPixelGeometry.Unknown, WaylandWindowWrapper.GetTextPixelGeometry([rgb with { Transform = 1 }], settings, 1));
		Assert.AreEqual(SKPixelGeometry.Unknown, WaylandWindowWrapper.GetTextPixelGeometry([rgb with { Scale = 2 }], settings, 1));
		Assert.AreEqual(SKPixelGeometry.Unknown, WaylandWindowWrapper.GetTextPixelGeometry([rgb], settings, 1.25));
	}

	[TestMethod]
	public void TextGeometryPrefersOutputOrderAndFallsBackWhenUnknown()
	{
		var settings = LinuxFontRenderingSettings.FromPattern(true, true, 1, 1, 1, false);
		var output = WaylandOutputInfo.Unknown with { Transform = 0, Subpixel = 3 };
		Assert.AreEqual(SKPixelGeometry.BgrHorizontal, WaylandWindowWrapper.GetTextPixelGeometry([output], settings, 1));
		Assert.AreEqual(SKPixelGeometry.RgbHorizontal, WaylandWindowWrapper.GetTextPixelGeometry([output with { Subpixel = 0 }], settings, 1));
		Assert.AreEqual(SKPixelGeometry.Unknown, WaylandWindowWrapper.GetTextPixelGeometry([output with { Subpixel = 1 }], settings, 1));
		Assert.AreEqual(SKPixelGeometry.Unknown, WaylandWindowWrapper.GetTextPixelGeometry([output with { Subpixel = 0 }], LinuxFontRenderingSettings.Default, 1));
		var desktop = new LinuxTextGeometry(SKPixelGeometry.RgbVertical, LinuxTextGeometrySource.Gnome);
		Assert.AreEqual(SKPixelGeometry.RgbVertical, WaylandWindowWrapper.GetTextPixelGeometry([output with { Subpixel = 0 }], LinuxFontRenderingSettings.Default, 1, desktop));
		Assert.AreEqual(SKPixelGeometry.BgrHorizontal, WaylandWindowWrapper.GetTextPixelGeometry([output], LinuxFontRenderingSettings.Default, 1, desktop));
		Assert.AreEqual(SKPixelGeometry.RgbHorizontal, WaylandWindowWrapper.GetTextPixelGeometry([output with { Subpixel = 0 }], settings, 1, desktop));
	}

	[TestMethod]
	[DataRow("auto")]
	[DataRow("on")]
	public void TextGeometryEnvironmentOrderOverridesEveryOutput(string mode)
	{
		var settings = LinuxFontRenderingSettings.Default.WithOverrides(null, "vbgr", lcd: mode);
		var output = WaylandOutputInfo.Unknown with { Transform = 0, Subpixel = 2 };
		Assert.AreEqual(SKPixelGeometry.BgrVertical, WaylandWindowWrapper.GetTextPixelGeometry(
			[output, output with { Subpixel = 3 }, output with { Subpixel = 1 }, output with { Subpixel = 0 }], settings, 1));
	}

	[TestMethod]
	[DataRow("auto")]
	[DataRow("on")]
	public void TextGeometryExplicitNoneDependsOnMode(string mode)
	{
		var output = WaylandOutputInfo.Unknown with { Transform = 0, Subpixel = 3 };
		var expected = mode == "on" ? SKPixelGeometry.BgrHorizontal : SKPixelGeometry.Unknown;
		var settings = LinuxFontRenderingSettings.FromPattern(true, true, 1, 5, 1, false).WithOverrides(null, null, lcd: mode);
		Assert.AreEqual(expected, WaylandWindowWrapper.GetTextPixelGeometry([output], settings, 1));
		settings = LinuxFontRenderingSettings.Default.WithOverrides(null, "none", lcd: mode);
		Assert.AreEqual(expected, WaylandWindowWrapper.GetTextPixelGeometry([output], settings, 1));
		settings = LinuxFontRenderingSettings.Default.WithOverrides(null, null, lcd: mode);
		var desktop = new LinuxTextGeometry(SKPixelGeometry.Unknown, LinuxTextGeometrySource.Kde, ExplicitNone: true);
		Assert.AreEqual(expected, WaylandWindowWrapper.GetTextPixelGeometry([output], settings, 1, desktop));
	}

	[TestMethod]
	public void TextGeometryOnAssumesRgbAndIgnoresOutputNone()
	{
		var output = WaylandOutputInfo.Unknown with { Transform = 0 };
		var settings = LinuxFontRenderingSettings.Default.WithOverrides(null, null, lcd: "on");
		Assert.AreEqual(SKPixelGeometry.RgbHorizontal, WaylandWindowWrapper.GetTextPixelGeometry([output], settings, 1));
		Assert.AreEqual(SKPixelGeometry.RgbHorizontal, WaylandWindowWrapper.GetTextPixelGeometry([output with { Subpixel = 1 }], settings, 1));
		settings = LinuxFontRenderingSettings.FromPattern(true, true, 1, 2, 1, false).WithOverrides(null, null, lcd: "on");
		Assert.AreEqual(SKPixelGeometry.BgrHorizontal, WaylandWindowWrapper.GetTextPixelGeometry([output with { Subpixel = 1 }], settings, 1));
		settings = LinuxFontRenderingSettings.FromPattern(true, true, 1, 5, 1, false).WithOverrides(null, null, lcd: "on");
		Assert.AreEqual(SKPixelGeometry.RgbHorizontal, WaylandWindowWrapper.GetTextPixelGeometry([output with { Subpixel = 1 }], settings, 1));
	}

	[TestMethod]
	public void EglConfigRejectsMultisamplingAndUnknownSampleCounts()
	{
		IntPtr[] configs = [new(1), new(2), new(3), new(4)];
		Assert.AreEqual(configs[3], WaylandEglConfig.Select(configs, _ => 0,
			config => config == configs[0] ? 1 : config == configs[2] ? null : 0,
			config => config == configs[1] ? 4 : 0));
		Assert.ThrowsExactly<NotSupportedException>(() => WaylandEglConfig.Select(configs, _ => 0, _ => 1, _ => 4));
	}

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
}
