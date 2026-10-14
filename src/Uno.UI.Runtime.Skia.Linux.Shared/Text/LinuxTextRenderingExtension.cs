#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Documents.TextFormatting;
using SkiaSharp;
using Uno.Foundation.Logging;

namespace Uno.WinUI.Runtime.Skia.Linux;

internal sealed class LinuxTextRenderingExtension : ISkiaTextRenderingExtension
{
	internal static LinuxTextRenderingExtension Instance { get; } = new();

	private const int MaxCachedShaders = 64;

	private readonly ConditionalWeakTable<SKFont, SKFont> _grayscaleFonts = new();
	private readonly Dictionary<uint, SKShader> _colorShaders = new();
	private readonly SKPaint _renderPaint = new();

	internal LinuxFontRenderingSettings Settings { get; }

	public bool UsesLcd => Settings.Edging == SKFontEdging.SubpixelAntialias;

	private LinuxTextRenderingExtension()
		: this(FontConfig.ReadDefault().WithOverrides(
			Environment.GetEnvironmentVariable("UNO_SKIA_TEXT_HINTING"), Environment.GetEnvironmentVariable("UNO_SKIA_TEXT_RGBA"),
			Environment.GetEnvironmentVariable("UNO_SKIA_TEXT_GAMMA")))
	{
	}

	internal LinuxTextRenderingExtension(LinuxFontRenderingSettings settings)
	{
		Settings = settings;
		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			// SkiaSharp has no LCD filter setter: Skia's own FreeType library uses FT_LCD_FILTER_DEFAULT.
			this.Log().Debug($"Linux text: fontconfig={Settings.HasFontConfig}, antialias={Settings.Antialias}, hinting={Settings.Hinting}, geometry={Settings.PixelGeometry}, autohint={Settings.ForceAutoHinting}, linear coverage={Settings.LinearCoverage}, fontconfig lcdfilter={Settings.LcdFilter} (Skia uses default).");
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

	// Skia derives its coverage gamma/contrast table from the paint's luminance colour, but only when the paint is
	// "just a colour". A colour shader behind a colour filter reports no luminance, so Skia uses its neutral
	// mid-grey table, which is within a few percent of the raw FreeType coverage Qt and cairo blend.
	public SKPaint GetRenderPaint(SKPaint paint)
	{
		if (!Settings.LinearCoverage || !paint.IsAntialias || paint.Shader is not null || paint.ColorFilter is not null)
		{
			return paint;
		}

		var color = paint.Color;
		_renderPaint.Color = color; // only the alpha applies when a shader is set
		_renderPaint.BlendMode = paint.BlendMode;
		_renderPaint.Style = paint.Style;
		_renderPaint.StrokeWidth = paint.StrokeWidth;
		_renderPaint.MaskFilter = paint.MaskFilter;
		_renderPaint.ImageFilter = paint.ImageFilter;
		_renderPaint.IsAntialias = true;
		_renderPaint.Shader = GetColorShader(color.WithAlpha(255));
		return _renderPaint;
	}

	private SKShader GetColorShader(SKColor opaqueColor)
	{
		if (!_colorShaders.TryGetValue((uint)opaqueColor, out var shader))
		{
			if (_colorShaders.Count >= MaxCachedShaders)
			{
				foreach (var cached in _colorShaders.Values)
				{
					cached.Dispose();
				}
				_colorShaders.Clear();
			}

			using var identity = SKColorFilter.CreateColorMatrix(
			[
				1, 0, 0, 0, 0,
				0, 1, 0, 0, 0,
				0, 0, 1, 0, 0,
				0, 0, 0, 1, 0,
			]);
			shader = SKShader.CreateColor(opaqueColor).WithColorFilter(identity);
			_colorShaders.Add((uint)opaqueColor, shader);
		}

		return shader;
	}

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
