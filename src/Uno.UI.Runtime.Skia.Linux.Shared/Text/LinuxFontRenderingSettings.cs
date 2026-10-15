#nullable enable

using System;
using SkiaSharp;

namespace Uno.WinUI.Runtime.Skia.Linux;

// A startup snapshot: cached fonts and recorded pictures cannot follow live fontconfig changes.
internal sealed record LinuxFontRenderingSettings(bool Antialias, SKFontHinting Hinting, SKPixelGeometry PixelGeometry, int LcdFilter, bool ForceAutoHinting)
{
	internal static readonly LinuxFontRenderingSettings Default = new(true, SKFontHinting.Normal, SKPixelGeometry.Unknown, 1, false);

	internal static readonly LinuxFontRenderingSettings Unavailable = Default with { HasFontConfig = false };

	internal bool HasFontConfig { get; init; } = true;
	internal bool HintingOverridden { get; init; }
	internal LcdMode LcdMode { get; init; }
	internal LinuxTextGeometrySource GeometrySource { get; init; }
	internal bool ExplicitNone { get; init; }

	internal LinuxTextGeometry GetConfiguredGeometry(LinuxTextGeometry? desktop = null)
		=> PixelGeometry != SKPixelGeometry.Unknown || ExplicitNone
			? new(PixelGeometry, GeometrySource, ExplicitNone) : desktop ?? LinuxTextGeometry.Unknown;

	internal SKPixelGeometry GetPreferredGeometry(LinuxTextGeometry? desktop = null)
	{
		var configured = GetConfiguredGeometry(desktop);
		return !Antialias || LcdMode == LcdMode.Off || (LcdMode == LcdMode.Auto && configured.ExplicitNone)
			? SKPixelGeometry.Unknown
			: configured.PixelGeometry != SKPixelGeometry.Unknown ? configured.PixelGeometry
			: LcdMode == LcdMode.On ? SKPixelGeometry.RgbHorizontal : SKPixelGeometry.Unknown;
	}

	// Skia darkens or lightens glyph coverage per text colour (SK_GAMMA_EXPONENT/SK_GAMMA_CONTRAST); FreeType consumers
	// such as Qt and cairo blend the raw coverage. Linear coverage matches them, which matters most for dark text at 1x.
	internal bool LinearCoverage { get; init; } = true;

	// Unknown surface geometry converts LCD requests to A8 until an output order is available.
	internal SKFontEdging Edging => !Antialias ? SKFontEdging.Alias
		: LcdMode == LcdMode.Off || (LcdMode == LcdMode.Auto && ExplicitNone) ? SKFontEdging.Antialias : SKFontEdging.SubpixelAntialias;

	internal static LinuxFontRenderingSettings FromPattern(bool? antialias, bool? hinting, int? hintstyle, int? rgba, int? lcdFilter, bool? autohint)
		=> new(antialias ?? Default.Antialias,
			hinting == false ? SKFontHinting.None : hintstyle switch
			{
				0 => SKFontHinting.None,
				1 => SKFontHinting.Slight,
				2 => SKFontHinting.Normal,
				3 => SKFontHinting.Full,
				_ => Default.Hinting,
			},
			rgba switch
			{
				1 => SKPixelGeometry.RgbHorizontal,
				2 => SKPixelGeometry.BgrHorizontal,
				3 => SKPixelGeometry.RgbVertical,
				4 => SKPixelGeometry.BgrVertical,
				_ => SKPixelGeometry.Unknown,
			},
			lcdFilter ?? Default.LcdFilter, autohint ?? Default.ForceAutoHinting)
		{
			GeometrySource = rgba is >= 1 and <= 5 ? LinuxTextGeometrySource.FontConfig : LinuxTextGeometrySource.Unknown,
			ExplicitNone = rgba == 5,
		};

	// Startup overrides: UNO_SKIA_TEXT_HINTING=none|slight|normal|full, UNO_SKIA_TEXT_RGBA=none|rgb|bgr|vrgb|vbgr and
	// UNO_SKIA_TEXT_GAMMA=linear|skia, UNO_SKIA_TEXT_LCD=auto|on|off. Invalid values keep the current preference.
	internal LinuxFontRenderingSettings WithOverrides(string? hinting, string? rgba, string? gamma = null, string? lcd = null)
	{
		var geometry = LinuxTextGeometry.Parse(rgba, LinuxTextGeometrySource.Environment);
		return this with
		{
			LcdMode = lcd?.Trim().ToLowerInvariant() switch
			{
				"auto" => LcdMode.Auto,
				"on" => LcdMode.On,
				"off" => LcdMode.Off,
				_ => LcdMode,
			},
			GeometrySource = geometry?.Source ?? GeometrySource,
			ExplicitNone = geometry?.ExplicitNone ?? ExplicitNone,
			LinearCoverage = gamma?.Trim().ToLowerInvariant() switch
			{
				"linear" => true,
				"skia" => false,
				_ => LinearCoverage,
			},
			HintingOverridden = HintingOverridden || hinting?.Trim().ToLowerInvariant() is "none" or "slight" or "normal" or "full",
			Hinting = hinting?.Trim().ToLowerInvariant() switch
			{
				"none" => SKFontHinting.None,
				"slight" => SKFontHinting.Slight,
				"normal" => SKFontHinting.Normal,
				"full" => SKFontHinting.Full,
				_ => Hinting,
			},
			PixelGeometry = geometry?.PixelGeometry ?? PixelGeometry,
		};
	}

	internal SKPixelGeometry GetSurfaceGeometry(double scale, bool pixelPreserving = true)
		=> Antialias && pixelPreserving && double.IsFinite(scale) && scale >= 1 && scale == Math.Truncate(scale)
			? GetPreferredGeometry() : SKPixelGeometry.Unknown;

	// Skia allows LCD under any affine transform, so Uno enforces axis alignment. A translucent SrcOver colour blends
	// each subpixel's coverage correctly over the opaque destination the frame gate requires (Fluent text brushes
	// are translucent). Ordinary saveLayer/effect surfaces do lose LCD geometry.
	internal static bool CanUseLcd(SKMatrix matrix, SKPaint paint)
		=> paint.Shader is null && paint.ColorFilter is null
			&& paint.MaskFilter is null && paint.ImageFilter is null && paint.BlendMode == SKBlendMode.SrcOver
			&& matrix.SkewX == 0 && matrix.SkewY == 0 && matrix.Persp0 == 0 && matrix.Persp1 == 0 && matrix.Persp2 == 1
			&& matrix.ScaleX > 0 && matrix.ScaleY > 0 && float.IsFinite(matrix.ScaleX) && float.IsFinite(matrix.ScaleY);
}
