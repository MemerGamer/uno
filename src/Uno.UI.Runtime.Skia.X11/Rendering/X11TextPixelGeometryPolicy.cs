#nullable enable

using System.Collections.Generic;
using System.Drawing;
using SkiaSharp;

namespace Uno.WinUI.Runtime.Skia.X11;

internal static class X11TextPixelGeometryPolicy
{
	internal readonly record struct Output(Rectangle Bounds, SKPixelGeometry Geometry);

	internal static SKPixelGeometry FromSubpixelOrder(ushort order) => order switch
	{
		2 => SKPixelGeometry.RgbHorizontal,
		3 => SKPixelGeometry.BgrHorizontal,
		4 => SKPixelGeometry.RgbVertical,
		5 => SKPixelGeometry.BgrVertical,
		_ => SKPixelGeometry.Unknown,
	};

	internal static SKPixelGeometry GetGeometry(Rectangle window, IReadOnlyList<Output> outputs, SKPixelGeometry preference)
	{
		if (preference == SKPixelGeometry.Unknown || window.Width <= 0 || window.Height <= 0)
		{
			return SKPixelGeometry.Unknown;
		}
		var covered = false;
		foreach (var output in outputs)
		{
			if (window.IntersectsWith(output.Bounds))
			{
				if (output.Geometry != preference)
				{
					return SKPixelGeometry.Unknown;
				}
				covered |= output.Bounds.Contains(window);
			}
		}
		// Conservatively require one CRTC to cover the window; gaps and spanning layouts stay grayscale.
		return covered ? preference : SKPixelGeometry.Unknown;
	}
}
