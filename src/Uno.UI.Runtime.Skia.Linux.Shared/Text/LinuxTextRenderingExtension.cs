#nullable enable

using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Documents.TextFormatting;
using SkiaSharp;
using Uno.Foundation.Logging;

namespace Uno.WinUI.Runtime.Skia.Linux;

internal sealed class LinuxTextRenderingExtension : ISkiaTextRenderingExtension
{
	internal static LinuxTextRenderingExtension Instance { get; } = new();

	private readonly ConditionalWeakTable<SKFont, SKFont> _grayscaleFonts = new();

	internal LinuxFontRenderingSettings Settings { get; }

	public bool UsesLcd => Settings.Edging == SKFontEdging.SubpixelAntialias;

	private LinuxTextRenderingExtension()
		: this(FontConfig.ReadDefault().WithOverrides(
			Environment.GetEnvironmentVariable("UNO_SKIA_TEXT_HINTING"), Environment.GetEnvironmentVariable("UNO_SKIA_TEXT_RGBA")))
	{
	}

	internal LinuxTextRenderingExtension(LinuxFontRenderingSettings settings)
	{
		Settings = settings;
		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			// SkiaSharp has no LCD filter setter: Skia's own FreeType library uses FT_LCD_FILTER_DEFAULT.
			this.Log().Debug($"Linux text: fontconfig={Settings.HasFontConfig}, antialias={Settings.Antialias}, hinting={Settings.Hinting}, geometry={Settings.PixelGeometry}, autohint={Settings.ForceAutoHinting}, fontconfig lcdfilter={Settings.LcdFilter} (Skia uses default).");
		}
	}

	public void Apply(SKFont font)
	{
		if (Settings.HasFontConfig || Settings.HintingOverridden)
		{
			font.Hinting = Settings.Hinting;
		}
		font.Edging = Settings.Edging;
		if (Settings.HasFontConfig)
		{
			font.ForceAutoHinting = Settings.ForceAutoHinting;
		}
	}

	public SKFont GetRenderFont(SKFont font, SKMatrix matrix, SKPaint paint)
		=> font.Edging == SKFontEdging.SubpixelAntialias && !LinuxFontRenderingSettings.CanUseLcd(matrix, paint)
			? _grayscaleFonts.GetValue(font, CreateGrayscaleFont) : font;

	private static SKFont CreateGrayscaleFont(SKFont font) => new(font.Typeface, font.Size, font.ScaleX, font.SkewX)
	{
		Edging = SKFontEdging.Antialias,
		Hinting = font.Hinting,
		Subpixel = font.Subpixel,
		BaselineSnap = font.BaselineSnap,
		ForceAutoHinting = font.ForceAutoHinting,
		LinearMetrics = font.LinearMetrics,
		EmbeddedBitmaps = font.EmbeddedBitmaps,
		Embolden = font.Embolden,
	};
}
