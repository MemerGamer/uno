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
	public double EffectiveScale => _viewport is not null && _preferredScale != 0 ? _preferredScale / 120d : _integerScale;
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
		_surface.SetBufferScale(_viewport is not null ? 1 : _integerScale);
		_viewport?.SetDestination(width, height);
		return (checked((int)Math.Ceiling(width * EffectiveScale)), checked((int)Math.Ceiling(height * EffectiveScale)));
	}
}
