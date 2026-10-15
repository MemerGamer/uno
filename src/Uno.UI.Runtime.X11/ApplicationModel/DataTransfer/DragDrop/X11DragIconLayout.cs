// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;

namespace Uno.UI.Runtime.X11;

internal readonly record struct X11DragIconLayout(int Width, int Height, int OffsetX, int OffsetY)
{
	internal const int MaxLogicalSize = 512;

	internal static X11DragIconLayout Compute(int pixelWidth, int pixelHeight, double scale, (double X, double Y)? anchor)
	{
		scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
		pixelWidth = Math.Max(1, pixelWidth);
		pixelHeight = Math.Max(1, pixelHeight);
		var shrink = Math.Min(1, MaxLogicalSize * scale / Math.Max(pixelWidth, pixelHeight));
		var width = Math.Max(1, (int)Math.Round(pixelWidth * shrink));
		var height = Math.Max(1, (int)Math.Round(pixelHeight * shrink));
		var (x, y) = anchor is { } a ? (a.X * scale * shrink, a.Y * scale * shrink) : (-width / 2.0, -height * 0.75);
		return new(width, height, (int)Math.Round(x), (int)Math.Round(y));
	}
}
