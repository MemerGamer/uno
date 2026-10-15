#nullable enable

using SkiaSharp;

namespace Uno.WinUI.Runtime.Skia.Linux;

internal enum LcdMode
{
	Auto,
	On,
	Off,
}

internal enum LinuxTextGeometrySource
{
	Unknown,
	Environment,
	FontConfig,
	Kde,
	Gnome,
}

internal sealed record LinuxTextGeometry(SKPixelGeometry PixelGeometry, LinuxTextGeometrySource Source, bool ExplicitNone = false)
{
	internal static readonly LinuxTextGeometry Unknown = new(SKPixelGeometry.Unknown, LinuxTextGeometrySource.Unknown);

	internal static LinuxTextGeometry? Parse(string? value, LinuxTextGeometrySource source)
		=> value?.Trim().ToLowerInvariant() switch
		{
			"rgb" => new(SKPixelGeometry.RgbHorizontal, source),
			"bgr" => new(SKPixelGeometry.BgrHorizontal, source),
			"vrgb" => new(SKPixelGeometry.RgbVertical, source),
			"vbgr" => new(SKPixelGeometry.BgrVertical, source),
			"none" => new(SKPixelGeometry.Unknown, source, ExplicitNone: true),
			_ => null,
		};
}
