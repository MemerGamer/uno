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

	internal SKFontEdging Edging => !Antialias ? SKFontEdging.Alias
		: PixelGeometry == SKPixelGeometry.Unknown ? SKFontEdging.Antialias : SKFontEdging.SubpixelAntialias;

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
			lcdFilter ?? Default.LcdFilter, autohint ?? Default.ForceAutoHinting);

	// Startup overrides: UNO_SKIA_TEXT_HINTING=none|slight|normal|full and
	// UNO_SKIA_TEXT_RGBA=none|rgb|bgr|vrgb|vbgr. Invalid values keep fontconfig's preference.
	internal LinuxFontRenderingSettings WithOverrides(string? hinting, string? rgba)
		=> this with
		{
			HintingOverridden = HintingOverridden || hinting?.Trim().ToLowerInvariant() is "none" or "slight" or "normal" or "full",
			Hinting = hinting?.Trim().ToLowerInvariant() switch
			{
				"none" => SKFontHinting.None,
				"slight" => SKFontHinting.Slight,
				"normal" => SKFontHinting.Normal,
				"full" => SKFontHinting.Full,
				_ => Hinting,
			},
			PixelGeometry = rgba?.Trim().ToLowerInvariant() switch
			{
				"none" => SKPixelGeometry.Unknown,
				"rgb" => SKPixelGeometry.RgbHorizontal,
				"bgr" => SKPixelGeometry.BgrHorizontal,
				"vrgb" => SKPixelGeometry.RgbVertical,
				"vbgr" => SKPixelGeometry.BgrVertical,
				_ => PixelGeometry,
			},
		};

	internal SKPixelGeometry GetSurfaceGeometry(double scale, bool pixelPreserving = true)
		=> Antialias && pixelPreserving && double.IsFinite(scale) && scale >= 1 && scale == Math.Truncate(scale)
			? PixelGeometry : SKPixelGeometry.Unknown;

	// Skia's raster path allows LCD on translucent paint and affine transforms; it does not
	// enforce our presentation policy. Ordinary saveLayer/effect surfaces do lose LCD geometry.
	internal static bool CanUseLcd(SKMatrix matrix, SKPaint paint)
		=> paint.Color.Alpha == 255 && paint.Shader is null && paint.ColorFilter is null
			&& paint.MaskFilter is null && paint.ImageFilter is null && paint.BlendMode == SKBlendMode.SrcOver
			&& matrix.SkewX == 0 && matrix.SkewY == 0 && matrix.Persp0 == 0 && matrix.Persp1 == 0 && matrix.Persp2 == 1
			&& matrix.ScaleX > 0 && matrix.ScaleY > 0 && float.IsFinite(matrix.ScaleX) && float.IsFinite(matrix.ScaleY);
}
