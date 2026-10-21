#nullable enable

using System;
using System.Runtime.InteropServices;
using Uno.UI.Runtime.Linux;
using Uno.UI.Runtime.Wayland.Protocols;
using Uno.UI.Runtime.Wayland.Rendering;

namespace Uno.UI.Runtime.Wayland;

// The wl_surface shown under the pointer during an outgoing drag: the snapshot Uno's DragView would show,
// or a generic icon when none was rendered yet. Created and disposed on the dispatch thread.
internal sealed class WaylandDragIcon : IDisposable
{
	private readonly WlSurface _surface;
	private readonly WpViewport? _viewport;
	private readonly WaylandShmBuffer _buffer;
	private readonly WaylandDragIconLayout _layout;
	private bool _disposed;

	private WaylandDragIcon(WlSurface surface, WpViewport? viewport, WaylandShmBuffer buffer, WaylandDragIconLayout layout)
	{
		_surface = surface;
		_viewport = viewport;
		_buffer = buffer;
		_layout = layout;
	}

	internal WlSurface Surface => _surface;

	/// <summary>Dispatch thread: creates the surface without a buffer; it is attached by <see cref="Present"/> once it has the icon role.</summary>
	internal static WaylandDragIcon Create(WaylandConnection connection, LinuxDragIconImage? image, double scale)
	{
		var hasViewport = connection.Viewporter is not null;
		var layout = image is null
			? WaylandDragIconLayout.Compute((int)Math.Round(LinuxDragIconPixels.FallbackLogicalSize * scale), (int)Math.Round(LinuxDragIconPixels.FallbackLogicalSize * scale), scale, (-8, -8), hasViewport)
			: WaylandDragIconLayout.Compute(image.Width, image.Height, scale, image.Anchor, hasViewport);
		var surface = connection.Compositor.CreateSurface();
		WpViewport? viewport = null;
		WaylandShmBuffer? buffer = null;
		try
		{
			viewport = connection.Viewporter?.GetViewport(surface);
			buffer = new WaylandShmBuffer(connection.Shm, layout.BufferWidth, layout.BufferHeight, released: null);
			var pixels = new byte[buffer.Length];
			LinuxDragIconPixels.Render(pixels, layout.BufferWidth, layout.BufferHeight, image);
			Marshal.Copy(pixels, 0, buffer.Memory, pixels.Length);
			return new WaylandDragIcon(surface, viewport, buffer, layout);
		}
		catch
		{
			buffer?.Dispose();
			viewport?.Dispose();
			surface.Dispose();
			throw;
		}
	}

	/// <summary>Dispatch thread, after wl_data_device.start_drag gave the surface its role.</summary>
	internal void Present()
	{
		if (_disposed)
		{
			return;
		}
		if (_surface.Version >= 3)
		{
			_surface.SetBufferScale(_layout.BufferScale);
		}
		_viewport?.SetDestination(_layout.LogicalWidth, _layout.LogicalHeight);
		// The offset places the icon relative to the hotspot; attach carried it before wl_surface v5.
		if (_surface.Version >= 5)
		{
			_surface.Offset(_layout.OffsetX, _layout.OffsetY);
			_surface.Attach(_buffer.Proxy, 0, 0);
		}
		else
		{
			_surface.Attach(_buffer.Proxy, _layout.OffsetX, _layout.OffsetY);
		}
		_surface.Damage(0, 0, int.MaxValue, int.MaxValue);
		_surface.Commit();
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_viewport?.Dispose();
		_surface.Dispose();
		_buffer.Dispose();
	}
}
