// Licensed under the MIT License. See LICENSE in the project root.
#nullable enable

using System;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland.Input;

internal sealed class WaylandCursorSurface : IWaylandCursorSurface
{
	private readonly WlPointer _pointer;
	private readonly WlSurface _surface;
	private bool _disposed;

	public WaylandCursorSurface(WlPointer pointer, WlSurface surface)
	{
		_pointer = pointer;
		_surface = surface;
	}

	public void Show(uint serial, nint buffer, int width, int height, int hotspotX, int hotspotY, int scale)
	{
		if (_disposed)
		{
			return;
		}
		_pointer.SetCursor(serial, _surface, hotspotX, hotspotY);
		_surface.SetBufferScale(scale);
		_surface.Attach(buffer, 0, 0);
		_surface.Damage(0, 0, int.MaxValue, int.MaxValue);
		_surface.Commit();
	}

	public void Hide(uint serial)
	{
		if (_disposed)
		{
			return;
		}
		_pointer.SetCursor(serial, null, 0, 0);
		_surface.Attach(0, 0, 0);
		_surface.Commit();
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_surface.Attach(0, 0, 0);
		_surface.Commit();
		_surface.Dispose();
	}
}
