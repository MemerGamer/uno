#nullable enable

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;
using Uno.WinUI.Runtime.Skia.Linux;

namespace Uno.WinUI.Runtime.Skia.Wayland.Tests;

[TestClass]
public sealed class Given_LinuxFontRendering
{
	[TestMethod]
	public void When_PatternPropertiesAreMissingOrInvalid()
	{
		Assert.AreEqual(LinuxFontRenderingSettings.Default, LinuxFontRenderingSettings.FromPattern(null, null, null, null, null, null));
		var settings = LinuxFontRenderingSettings.FromPattern(null, null, 99, 99, null, null);
		Assert.AreEqual(SKFontHinting.Normal, settings.Hinting);
		Assert.AreEqual(SKPixelGeometry.Unknown, settings.PixelGeometry);
		Assert.AreEqual(SKFontEdging.Antialias, settings.Edging);
	}

	[TestMethod]
	public void When_FontconfigIsUnavailable()
	{
		var extension = new LinuxTextRenderingExtension(LinuxFontRenderingSettings.Unavailable);
		using var font = new SKFont { Subpixel = true, BaselineSnap = true };
		extension.Apply(font);
		Assert.IsFalse(extension.Settings.HasFontConfig);
		Assert.AreEqual(SKFontHinting.Normal, font.Hinting);
		Assert.AreEqual(SKFontEdging.Antialias, font.Edging);
		Assert.IsTrue(font.Subpixel);
		Assert.IsTrue(font.BaselineSnap);
		font.Hinting = SKFontHinting.Full;
		font.ForceAutoHinting = true;
		extension.Apply(font);
		Assert.AreEqual(SKFontHinting.Full, font.Hinting);
		Assert.IsTrue(font.ForceAutoHinting);
	}

	[TestMethod]
	public void When_FontconfigIsUnavailableAndHintingIsOverridden()
	{
		using var font = new SKFont { ForceAutoHinting = true };
		new LinuxTextRenderingExtension(LinuxFontRenderingSettings.Unavailable.WithOverrides(" slight ", null)).Apply(font);
		Assert.AreEqual(SKFontHinting.Slight, font.Hinting);
		Assert.AreEqual(SKFontEdging.Antialias, font.Edging);
		Assert.IsTrue(font.ForceAutoHinting);
		font.Hinting = SKFontHinting.Full;
		new LinuxTextRenderingExtension(LinuxFontRenderingSettings.Unavailable.WithOverrides("invalid", null)).Apply(font);
		Assert.AreEqual(SKFontHinting.Full, font.Hinting);
	}

	[TestMethod]
	[DataRow(0, SKFontHinting.None)]
	[DataRow(1, SKFontHinting.Slight)]
	[DataRow(2, SKFontHinting.Normal)]
	[DataRow(3, SKFontHinting.Full)]
	public void When_HintstyleIsMapped(int hintstyle, SKFontHinting expected)
	{
		Assert.AreEqual(expected, LinuxFontRenderingSettings.FromPattern(true, true, hintstyle, 1, 1, false).Hinting);
		Assert.AreEqual(SKFontHinting.None, LinuxFontRenderingSettings.FromPattern(true, false, hintstyle, 1, 1, false).Hinting);
	}

	[TestMethod]
	[DataRow(0, SKPixelGeometry.Unknown)]
	[DataRow(1, SKPixelGeometry.RgbHorizontal)]
	[DataRow(2, SKPixelGeometry.BgrHorizontal)]
	[DataRow(3, SKPixelGeometry.RgbVertical)]
	[DataRow(4, SKPixelGeometry.BgrVertical)]
	[DataRow(5, SKPixelGeometry.Unknown)]
	public void When_RgbaIsMapped(int rgba, SKPixelGeometry expected)
		=> Assert.AreEqual(expected, LinuxFontRenderingSettings.FromPattern(true, true, 1, rgba, 1, false).PixelGeometry);

	[TestMethod]
	public void When_AntialiasIsDisabled()
	{
		var settings = LinuxFontRenderingSettings.FromPattern(false, true, 3, 1, 2, true).WithOverrides("slight", "rgb");
		Assert.AreEqual(SKFontEdging.Alias, settings.Edging);
		Assert.AreEqual(SKPixelGeometry.Unknown, settings.GetSurfaceGeometry(1));
		Assert.AreEqual(2, settings.LcdFilter);
		Assert.IsTrue(settings.ForceAutoHinting);
	}

	[TestMethod]
	public void When_HintingOverrideTakesPrecedenceOverFontconfig()
	{
		var settings = LinuxFontRenderingSettings.FromPattern(true, false, 3, 1, 1, false);
		Assert.AreEqual(SKFontHinting.None, settings.Hinting);
		Assert.AreEqual(SKFontHinting.Full, settings.WithOverrides("full", null).Hinting);
	}

	[TestMethod]
	[DataRow("none", SKFontHinting.None)]
	[DataRow("slight", SKFontHinting.Slight)]
	[DataRow("normal", SKFontHinting.Normal)]
	[DataRow("full", SKFontHinting.Full)]
	public void When_HintingIsOverridden(string value, SKFontHinting expected)
		=> Assert.AreEqual(expected, LinuxFontRenderingSettings.Default.WithOverrides(value, null).Hinting);

	[TestMethod]
	[DataRow("none", SKPixelGeometry.Unknown)]
	[DataRow("rgb", SKPixelGeometry.RgbHorizontal)]
	[DataRow("bgr", SKPixelGeometry.BgrHorizontal)]
	[DataRow("vrgb", SKPixelGeometry.RgbVertical)]
	[DataRow("vbgr", SKPixelGeometry.BgrVertical)]
	public void When_RgbaIsOverridden(string value, SKPixelGeometry expected)
		=> Assert.AreEqual(expected, LinuxFontRenderingSettings.Default.WithOverrides(null, value).PixelGeometry);

	[TestMethod]
	public void When_OverridesAreInvalidOrPadded()
	{
		var settings = LinuxFontRenderingSettings.Default.WithOverrides("full", "bgr");
		Assert.AreEqual(settings, settings.WithOverrides("invalid", "invalid"));
		Assert.AreEqual(settings, settings.WithOverrides(null, ""));
		Assert.AreEqual(settings, LinuxFontRenderingSettings.Default.WithOverrides(" FULL ", " BGR "));
	}

	[TestMethod]
	[DataRow(1d, true)]
	[DataRow(2d, true)]
	[DataRow(1.25d, false)]
	[DataRow(1.5d, false)]
	[DataRow(0d, false)]
	[DataRow(double.NaN, false)]
	public void When_SurfacePresentationIsScaled(double scale, bool lcd)
	{
		var settings = LinuxFontRenderingSettings.Default.WithOverrides(null, "rgb");
		Assert.AreEqual(lcd ? SKPixelGeometry.RgbHorizontal : SKPixelGeometry.Unknown, settings.GetSurfaceGeometry(scale));
		Assert.AreEqual(SKPixelGeometry.Unknown, settings.GetSurfaceGeometry(scale, pixelPreserving: false));
	}

	[TestMethod]
	public void When_FontIsAppliedAndUnsafeDrawsUseGrayscale()
	{
		var extension = new LinuxTextRenderingExtension(LinuxFontRenderingSettings.Default.WithOverrides("full", "rgb"));
		using var font = new SKFont { Subpixel = true, BaselineSnap = true };
		using var paint = new SKPaint { Color = SKColors.Black };
		extension.Apply(font);
		Assert.AreEqual(SKFontHinting.Full, font.Hinting);
		Assert.AreEqual(SKFontEdging.SubpixelAntialias, font.Edging);
		Assert.IsTrue(font.Subpixel);
		Assert.IsTrue(font.BaselineSnap);
		Assert.AreSame(font, extension.GetRenderFont(font, SKMatrix.CreateScale(1.25f, 1.25f), paint));
		using var grayscale = extension.GetRenderFont(font, SKMatrix.CreateRotationDegrees(15), paint);
		Assert.AreEqual(SKFontEdging.Antialias, grayscale.Edging);
		Assert.AreEqual(font.Hinting, grayscale.Hinting);
		Assert.IsTrue(grayscale.Subpixel);
		Assert.IsTrue(grayscale.BaselineSnap);
		paint.Color = SKColors.Black.WithAlpha(128);
		Assert.AreSame(grayscale, extension.GetRenderFont(font, SKMatrix.Identity, paint));
		Assert.AreSame(grayscale, extension.GetRenderFont(font, SKMatrix.CreateScale(-1, 1), paint));
	}

	[TestMethod]
	public void When_PaintOrMatrixNeedsGrayscale()
	{
		using var paint = new SKPaint { Color = SKColors.Black };
		Assert.IsTrue(LinuxFontRenderingSettings.CanUseLcd(SKMatrix.CreateTranslation(0.25f, 0.5f), paint));
		Assert.IsFalse(LinuxFontRenderingSettings.CanUseLcd(SKMatrix.CreateSkew(0.1f, 0), paint));
		Assert.IsFalse(LinuxFontRenderingSettings.CanUseLcd(SKMatrix.Identity with { Persp0 = 0.01f }, paint));
		paint.BlendMode = SKBlendMode.Src;
		Assert.IsFalse(LinuxFontRenderingSettings.CanUseLcd(SKMatrix.Identity, paint));
	}

	[TestMethod]
	public void When_GammaIsOverridden()
	{
		Assert.IsTrue(LinuxFontRenderingSettings.Default.LinearCoverage);
		Assert.IsFalse(LinuxFontRenderingSettings.Default.WithOverrides(null, null, " Skia ").LinearCoverage);
		Assert.IsTrue(LinuxFontRenderingSettings.Default.WithOverrides(null, null, "skia").WithOverrides(null, null, "linear").LinearCoverage);
		Assert.IsFalse(LinuxFontRenderingSettings.Default.WithOverrides(null, null, "skia").WithOverrides(null, null, "invalid").LinearCoverage);
		Assert.IsTrue(LinuxFontRenderingSettings.Unavailable.LinearCoverage);
	}

	[TestMethod]
	public void When_RenderPaintHidesLuminance()
	{
		var extension = new LinuxTextRenderingExtension(LinuxFontRenderingSettings.Default);
		using var paint = new SKPaint { Color = new SKColor(0, 0, 0, 0xE4), IsAntialias = true, BlendMode = SKBlendMode.Multiply };
		var renderPaint = extension.GetRenderPaint(paint);
		Assert.AreNotSame(paint, renderPaint);
		Assert.IsNotNull(renderPaint.Shader);
		Assert.AreEqual((byte)0xE4, renderPaint.Color.Alpha);
		Assert.AreEqual(SKBlendMode.Multiply, renderPaint.BlendMode);
		Assert.IsTrue(renderPaint.IsAntialias);
		Assert.IsNull(paint.Shader);

		// The shared paint is reused and the shader is cached per opaque colour.
		var shader = renderPaint.Shader;
		paint.Color = SKColors.Black;
		Assert.AreSame(renderPaint, extension.GetRenderPaint(paint));
		Assert.AreSame(shader, extension.GetRenderPaint(paint).Shader);
		paint.Color = SKColors.Red;
		Assert.AreNotSame(shader, extension.GetRenderPaint(paint).Shader);
	}

	[TestMethod]
	public void When_RenderPaintIsNotApplicable()
	{
		var extension = new LinuxTextRenderingExtension(LinuxFontRenderingSettings.Default);
		using var aliased = new SKPaint { Color = SKColors.Black, IsAntialias = false };
		Assert.AreSame(aliased, extension.GetRenderPaint(aliased));
		using var shaded = new SKPaint { Color = SKColors.Black, IsAntialias = true, Shader = SKShader.CreateColor(SKColors.Blue) };
		Assert.AreSame(shaded, extension.GetRenderPaint(shaded));
		using var filtered = new SKPaint { Color = SKColors.Black, IsAntialias = true, ColorFilter = SKColorFilter.CreateBlendMode(SKColors.Red, SKBlendMode.SrcIn) };
		Assert.AreSame(filtered, extension.GetRenderPaint(filtered));

		var skiaGamma = new LinuxTextRenderingExtension(LinuxFontRenderingSettings.Default.WithOverrides(null, null, "skia"));
		using var plain = new SKPaint { Color = SKColors.Black, IsAntialias = true };
		Assert.AreSame(plain, skiaGamma.GetRenderPaint(plain));
	}

	[TestMethod]
	public void When_RenderPaintCoverageIsLinear()
	{
		using var typeface = SKTypeface.Default;
		if (typeface is null || typeface.GlyphCount == 0)
		{
			Assert.Inconclusive("No default typeface is available.");
		}

		var extension = new LinuxTextRenderingExtension(LinuxFontRenderingSettings.Default);
		using var font = new SKFont(typeface, 14) { Edging = SKFontEdging.Antialias, Subpixel = true, Hinting = SKFontHinting.Slight };
		using var paint = new SKPaint { IsAntialias = true };

		var plainDark = Ink(font, paint, SKColors.Black, SKColors.White, extension: null);
		var plainLight = Ink(font, paint, SKColors.White, SKColors.Black, extension: null);
		var linearDark = Ink(font, paint, SKColors.Black, SKColors.White, extension);
		var linearLight = Ink(font, paint, SKColors.White, SKColors.Black, extension);
		Assert.IsTrue(plainDark > 0 && linearDark > 0, "Nothing was drawn.");

		// Skia's luminance tables thin dark text and thicken light text; the render paint gives both the same coverage.
		Assert.IsTrue(plainLight > plainDark * 1.1, $"Skia gamma expected: dark {plainDark}, light {plainLight}.");
		Assert.IsTrue(Math.Abs(linearDark - linearLight) <= linearDark * 0.02, $"Linear coverage expected: dark {linearDark}, light {linearLight}.");
		Assert.IsTrue(linearDark > plainDark, $"Dark text should gain coverage: {plainDark} -> {linearDark}.");
	}

	private static long Ink(SKFont font, SKPaint paint, SKColor foreground, SKColor background, LinuxTextRenderingExtension? extension)
	{
		using var surface = SKSurface.Create(new SKImageInfo(160, 32, SKColorType.Bgra8888, SKAlphaType.Premul));
		surface.Canvas.Clear(background);
		paint.Color = foreground;
		surface.Canvas.DrawText("Downloads", 4, 22, SKTextAlign.Left, font, extension?.GetRenderPaint(paint) ?? paint);
		using var image = surface.Snapshot();
		using var pixels = image.PeekPixels();
		long ink = 0;
		for (var y = 0; y < pixels.Height; y++)
		{
			for (var x = 0; x < pixels.Width; x++)
			{
				var green = pixels.GetPixelColor(x, y).Green;
				ink += background == SKColors.White ? 255 - green : green;
			}
		}

		return ink;
	}
}
