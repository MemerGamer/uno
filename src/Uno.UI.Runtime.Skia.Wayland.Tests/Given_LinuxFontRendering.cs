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
		Assert.AreEqual(SKFontEdging.SubpixelAntialias, settings.Edging);
	}

	[TestMethod]
	public void When_FontconfigIsUnavailable()
	{
		var extension = new LinuxTextRenderingExtension(LinuxFontRenderingSettings.Unavailable);
		using var font = new SKFont { Subpixel = true, BaselineSnap = true };
		extension.Apply(font);
		Assert.IsFalse(extension.Settings.HasFontConfig);
		Assert.AreEqual(SKFontHinting.Normal, font.Hinting);
		Assert.AreEqual(SKFontEdging.SubpixelAntialias, font.Edging);
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
		Assert.AreEqual(SKFontEdging.SubpixelAntialias, font.Edging);
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
	[DataRow(null, false)]
	[DataRow(0, false)]
	[DataRow(5, true)]
	public void When_ExplicitNoneIsDistinctFromUnknown(int? rgba, bool explicitNone)
	{
		var settings = LinuxFontRenderingSettings.FromPattern(true, true, 1, rgba, 1, false);
		Assert.AreEqual(SKPixelGeometry.Unknown, settings.PixelGeometry);
		Assert.AreEqual(explicitNone, settings.ExplicitNone);
		Assert.AreEqual(explicitNone ? LinuxTextGeometrySource.FontConfig : LinuxTextGeometrySource.Unknown, settings.GeometrySource);
		Assert.AreEqual(explicitNone ? SKFontEdging.Antialias : SKFontEdging.SubpixelAntialias, settings.Edging);
		Assert.AreEqual(SKFontEdging.SubpixelAntialias, settings.WithOverrides(null, null, lcd: "on").Edging);
	}

	[TestMethod]
	[DataRow(null, "Auto")]
	[DataRow("auto", "Auto")]
	[DataRow(" ON ", "On")]
	[DataRow(" Off ", "Off")]
	[DataRow("invalid", "Auto")]
	[DataRow("", "Auto")]
	public void When_LcdModeIsOverridden(string? value, string expected)
	{
		var settings = LinuxFontRenderingSettings.Default.WithOverrides(null, null, lcd: value);
		Assert.AreEqual(expected, settings.LcdMode.ToString());
		Assert.AreEqual(settings, settings.WithOverrides(null, null, lcd: "invalid"));
	}

	[TestMethod]
	[DataRow("off", null, SKFontEdging.Antialias, SKPixelGeometry.Unknown)]
	[DataRow("off", "rgb", SKFontEdging.Antialias, SKPixelGeometry.Unknown)]
	[DataRow("auto", null, SKFontEdging.SubpixelAntialias, SKPixelGeometry.Unknown)]
	[DataRow("auto", "none", SKFontEdging.Antialias, SKPixelGeometry.Unknown)]
	[DataRow("auto", "bgr", SKFontEdging.SubpixelAntialias, SKPixelGeometry.BgrHorizontal)]
	[DataRow("on", null, SKFontEdging.SubpixelAntialias, SKPixelGeometry.RgbHorizontal)]
	[DataRow("on", "none", SKFontEdging.SubpixelAntialias, SKPixelGeometry.RgbHorizontal)]
	[DataRow("on", "bgr", SKFontEdging.SubpixelAntialias, SKPixelGeometry.BgrHorizontal)]
	public void When_LcdModeSelectsEdgingAndPreference(string mode, string? rgba, SKFontEdging edging, SKPixelGeometry geometry)
	{
		var settings = LinuxFontRenderingSettings.Default.WithOverrides(null, rgba, lcd: mode);
		Assert.AreEqual(edging, settings.Edging);
		Assert.AreEqual(geometry, settings.GetPreferredGeometry());
		Assert.AreEqual(geometry, settings.GetSurfaceGeometry(1));
		Assert.AreEqual(SKPixelGeometry.Unknown, settings.GetSurfaceGeometry(1.25));
		Assert.AreEqual(SKPixelGeometry.Unknown, settings.GetSurfaceGeometry(1, pixelPreserving: false));
		var extension = new LinuxTextRenderingExtension(settings);
		using var font = new SKFont();
		extension.Apply(font);
		Assert.AreEqual(edging, font.Edging);
		Assert.AreEqual(edging == SKFontEdging.SubpixelAntialias, extension.UsesLcd);
		settings = settings with { Antialias = false };
		Assert.AreEqual(SKFontEdging.Alias, settings.Edging);
		Assert.AreEqual(SKPixelGeometry.Unknown, settings.GetPreferredGeometry());
	}

	[TestMethod]
	public void When_DesktopGeometryIsOnlyAFallback()
	{
		var desktop = new LinuxTextGeometry(SKPixelGeometry.BgrHorizontal, LinuxTextGeometrySource.Kde);
		var settings = LinuxFontRenderingSettings.Default;
		Assert.AreEqual(desktop, settings.GetConfiguredGeometry(desktop));
		Assert.AreEqual(SKPixelGeometry.BgrHorizontal, settings.GetPreferredGeometry(desktop));
		var desktopNone = new LinuxTextGeometry(SKPixelGeometry.Unknown, LinuxTextGeometrySource.Gnome, ExplicitNone: true);
		Assert.AreEqual(SKPixelGeometry.Unknown, settings.GetPreferredGeometry(desktopNone));
		Assert.AreEqual(SKPixelGeometry.RgbHorizontal, settings.WithOverrides(null, null, lcd: "on").GetPreferredGeometry(desktopNone));
		settings = LinuxFontRenderingSettings.FromPattern(true, true, 1, 1, 1, false);
		Assert.AreEqual(LinuxTextGeometrySource.FontConfig, settings.GetConfiguredGeometry(desktop).Source);
		Assert.AreEqual(SKPixelGeometry.RgbHorizontal, settings.GetPreferredGeometry(desktop));
		Assert.AreEqual(SKPixelGeometry.RgbHorizontal, settings.GetPreferredGeometry(desktopNone));
		settings = LinuxFontRenderingSettings.FromPattern(true, true, 1, 5, 1, false);
		Assert.IsTrue(settings.GetConfiguredGeometry(desktop).ExplicitNone);
		Assert.AreEqual(SKPixelGeometry.Unknown, settings.GetPreferredGeometry(desktop));
		settings = settings.WithOverrides(null, "bgr");
		Assert.AreEqual(LinuxTextGeometrySource.Environment, settings.GeometrySource);
		Assert.IsFalse(settings.ExplicitNone);
		Assert.AreEqual(SKFontEdging.SubpixelAntialias, settings.Edging);
		settings = settings.WithOverrides(null, "none");
		Assert.AreEqual(LinuxTextGeometrySource.Environment, settings.GeometrySource);
		Assert.IsTrue(settings.ExplicitNone);
		Assert.AreEqual(SKFontEdging.Antialias, settings.Edging);
		Assert.AreEqual(SKPixelGeometry.Unknown, settings.GetPreferredGeometry(desktop));
	}

	[TestMethod]
	[DataRow("[General]\nXftSubPixel=rgb", SKPixelGeometry.RgbHorizontal, false)]
	[DataRow("[General]\nXftSubPixel=bgr", SKPixelGeometry.BgrHorizontal, false)]
	[DataRow("[General]\nXftSubPixel=vrgb", SKPixelGeometry.RgbVertical, false)]
	[DataRow("[General]\nXftSubPixel=vbgr", SKPixelGeometry.BgrVertical, false)]
	[DataRow("[General]\n XftSubPixel = none ", SKPixelGeometry.Unknown, true)]
	[DataRow("[General]\nOther=rgb", SKPixelGeometry.Unknown, false)]
	[DataRow("[Other]\nXftSubPixel=rgb", SKPixelGeometry.Unknown, false)]
	[DataRow("[General]\n[Other]\nXftSubPixel=rgb", SKPixelGeometry.Unknown, false)]
	[DataRow("garbage", SKPixelGeometry.Unknown, false)]
	[DataRow("[General]\nXftSubPixel=garbage", SKPixelGeometry.Unknown, false)]
	[DataRow("[General]\nXftSubPixel", SKPixelGeometry.Unknown, false)]
	[DataRow("# comment\n[General]\n; XftSubPixel=none\nXftSubPixel=rgb\n[Other]\nXftSubPixel=bgr", SKPixelGeometry.RgbHorizontal, false)]
	public void When_KdeGeometryIsParsed(string contents, SKPixelGeometry expected, bool explicitNone)
	{
		var geometry = KdeTextGeometry.Parse(contents);
		Assert.AreEqual(expected, geometry.PixelGeometry);
		Assert.AreEqual(explicitNone, geometry.ExplicitNone);
		Assert.AreEqual(expected != SKPixelGeometry.Unknown || explicitNone ? LinuxTextGeometrySource.Kde : LinuxTextGeometrySource.Unknown, geometry.Source);
	}

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
		Assert.AreEqual(SKFontEdging.Antialias, extension.GetRenderFont(font, SKMatrix.Identity, paint, grayscale: true).Edging);
		using var grayscale = extension.GetRenderFont(font, SKMatrix.CreateRotationDegrees(15), paint);
		Assert.AreEqual(SKFontEdging.Antialias, grayscale.Edging);
		Assert.AreEqual(font.Hinting, grayscale.Hinting);
		Assert.IsTrue(grayscale.Subpixel);
		Assert.IsTrue(grayscale.BaselineSnap);
		paint.Color = SKColors.Black.WithAlpha(128);
		Assert.AreSame(font, extension.GetRenderFont(font, SKMatrix.Identity, paint));
		paint.BlendMode = SKBlendMode.Multiply;
		Assert.AreSame(grayscale, extension.GetRenderFont(font, SKMatrix.Identity, paint));
		paint.BlendMode = SKBlendMode.SrcOver;
		Assert.AreSame(grayscale, extension.GetRenderFont(font, SKMatrix.CreateScale(-1, 1), paint));
	}

	[TestMethod]
	public void When_PaintOrMatrixNeedsGrayscale()
	{
		using var paint = new SKPaint { Color = SKColors.Black };
		Assert.IsTrue(LinuxFontRenderingSettings.CanUseLcd(SKMatrix.CreateTranslation(0.25f, 0.5f), paint));
		Assert.IsFalse(LinuxFontRenderingSettings.CanUseLcd(SKMatrix.CreateSkew(0.1f, 0), paint));
		Assert.IsFalse(LinuxFontRenderingSettings.CanUseLcd(SKMatrix.Identity with { Persp0 = 0.01f }, paint));
		Assert.IsTrue(LinuxFontRenderingSettings.CanUseLcd(SKMatrix.Identity, new SKPaint { Color = new SKColor(0, 0, 0, 0xE4) }));
		paint.BlendMode = SKBlendMode.Src;
		Assert.IsFalse(LinuxFontRenderingSettings.CanUseLcd(SKMatrix.Identity, paint));
	}

	[TestMethod]
	public void When_TranslucentLcdTextBlendsPerSubpixel()
	{
		using var typeface = SKTypeface.Default;
		if (typeface is null || typeface.GlyphCount == 0)
		{
			Assert.Inconclusive("No default typeface is available.");
		}

		// Fluent text brushes are translucent (TextFillColorPrimary is #E4000000). Over an opaque destination each
		// subpixel must blend as coverage * alpha, i.e. the opaque LCD result faded towards the background.
		var extension = new LinuxTextRenderingExtension(LinuxFontRenderingSettings.Default);
		using var font = new SKFont(typeface, 14) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true, Hinting = SKFontHinting.Slight };
		using var paint = new SKPaint { IsAntialias = true, Color = SKColors.Black };
		var opaque = DrawTextPixels(font, paint, extension, SKPixelGeometry.RgbHorizontal);
		paint.Color = new SKColor(0, 0, 0, 0xE4);
		var translucent = DrawTextPixels(font, paint, extension, SKPixelGeometry.RgbHorizontal);
		var chromatic = false;
		for (var i = 0; i < opaque.Length; i++)
		{
			chromatic |= Math.Abs(translucent[i].Red - translucent[i].Blue) > 8;
			Assert.AreEqual(255, translucent[i].Alpha);
			Assert.AreEqual(Fade(opaque[i].Red), translucent[i].Red, 2, $"Red at pixel {i}.");
			Assert.AreEqual(Fade(opaque[i].Green), translucent[i].Green, 2, $"Green at pixel {i}.");
			Assert.AreEqual(Fade(opaque[i].Blue), translucent[i].Blue, 2, $"Blue at pixel {i}.");
		}
		Assert.IsTrue(chromatic, "Translucent text should keep LCD coverage.");

		static double Fade(byte channel) => 255 - (255 - channel) * 0xE4 / 255.0;
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

	[TestMethod]
	public void When_LinearCoveragePreservesLcdAndUnknownGeometryUsesGrayscale()
	{
		using var typeface = SKTypeface.Default;
		if (typeface is null || typeface.GlyphCount == 0)
		{
			Assert.Inconclusive("No default typeface is available.");
		}

		var extension = new LinuxTextRenderingExtension(LinuxFontRenderingSettings.Default);
		using var font = new SKFont(typeface, 14) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true, Hinting = SKFontHinting.Slight };
		using var paint = new SKPaint { IsAntialias = true, Color = SKColors.Black };
		Assert.AreSame(font, extension.GetRenderFont(font, SKMatrix.Identity, paint));
		Assert.AreNotSame(paint, extension.GetRenderPaint(paint));
		var lcd = DrawTextPixels(font, paint, extension, SKPixelGeometry.RgbHorizontal);
		var unknown = DrawTextPixels(font, paint, extension, SKPixelGeometry.Unknown);
		font.Edging = SKFontEdging.Antialias;
		var grayscale = DrawTextPixels(font, paint, extension, SKPixelGeometry.Unknown);
		var chromatic = false;
		for (var i = 0; i < lcd.Length; i++)
		{
			chromatic |= Math.Abs(lcd[i].Red - lcd[i].Green) > 8 || Math.Abs(lcd[i].Green - lcd[i].Blue) > 8;
			Assert.AreEqual(unknown[i].Red, unknown[i].Green, $"Unknown geometry at pixel {i}.");
			Assert.AreEqual(unknown[i].Green, unknown[i].Blue, $"Unknown geometry at pixel {i}.");
		}
		Assert.IsTrue(chromatic, "LCD geometry must produce chromatic pixels with the linear coverage paint.");
		CollectionAssert.AreEqual(grayscale, unknown, "Unknown geometry must rasterize LCD requests identically to grayscale.");
	}

	private static SKColor[] DrawTextPixels(SKFont font, SKPaint paint, LinuxTextRenderingExtension extension, SKPixelGeometry geometry)
	{
		using var properties = new SKSurfaceProperties(geometry);
		using var surface = SKSurface.Create(new SKImageInfo(160, 32, SKColorType.Bgra8888, SKAlphaType.Premul), properties);
		surface.Canvas.Clear(SKColors.White);
		var renderFont = extension.GetRenderFont(font, SKMatrix.Identity, paint);
		surface.Canvas.DrawText("Downloads", 4, 22, SKTextAlign.Left, renderFont, extension.GetRenderPaint(paint));
		using var image = surface.Snapshot();
		using var pixels = image.PeekPixels();
		var colors = new SKColor[pixels.Width * pixels.Height];
		for (var y = 0; y < pixels.Height; y++)
		{
			for (var x = 0; x < pixels.Width; x++)
			{
				colors[y * pixels.Width + x] = pixels.GetPixelColor(x, y);
			}
		}
		return colors;
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
