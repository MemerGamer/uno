// Licensed under the MIT License.
#nullable enable
using System;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland.Rendering;

internal sealed class WaylandRenderScale
{
	private readonly WlSurface _surface;
	private readonly WpViewport? _viewport;
	private int _integerScale = 1;
	private uint _preferredScale;
	public WaylandRenderScale(WlSurface surface, WpViewport? viewport)
	{
		_surface = surface;
		_viewport = viewport;
	}
	public double EffectiveScale => Compute(_viewport is not null, _preferredScale, _integerScale);
	public event EventHandler? Changed;
	public void SetIntegerScale(int scale)
	{
		if (scale < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(scale));
		}
		var previous = EffectiveScale;
		_integerScale = scale;
		if (previous != EffectiveScale)
		{
			Changed?.Invoke(this, EventArgs.Empty);
		}
	}
	public void SetPreferredScale(uint scale)
	{
		if (scale == 0)
		{
			throw new ArgumentOutOfRangeException(nameof(scale));
		}
		var previous = EffectiveScale;
		_preferredScale = scale;
		if (previous != EffectiveScale)
		{
			Changed?.Invoke(this, EventArgs.Empty);
		}
	}
	public (int Width, int Height) Apply(int width, int height)
	{
		if (width <= 0 || height <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(width));
		}
		if (_surface.Version >= 3)
		{
			_surface.SetBufferScale(_viewport is not null ? 1 : _integerScale);
		}
		_viewport?.SetDestination(width, height);
		return (ToBufferPixels(width, EffectiveScale), ToBufferPixels(height, EffectiveScale));
	}

	// wp_fractional_scale_v1: buffer size is the logical size times scale, rounded to nearest with halves up.
	public static int ToBufferPixels(double logical, double scale)
		=> checked((int)Math.Floor(logical * scale + 0.5));

	// A configured canvas uses the buffer's integer geometry, not independently rounded XamlRoot bounds.
	internal static (int Width, int Height) GetCanvasSize((int Width, int Height)? bufferSize, double requestedWidth, double requestedHeight)
		=> bufferSize ?? (Math.Max(ToBufferPixels(requestedWidth, 1), 1), Math.Max(ToBufferPixels(requestedHeight, 1), 1));

	public static double Compute(bool hasViewport, uint preferredScale, int integerScale)
		=> hasViewport && preferredScale != 0 ? preferredScale / 120d : Math.Max(integerScale, 1);
}
