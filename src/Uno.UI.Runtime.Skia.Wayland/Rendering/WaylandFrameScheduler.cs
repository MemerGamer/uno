// Licensed under the MIT License.
#nullable enable
using System;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland.Rendering;

// All calls, including protocol events, must run on the host's render owner thread.
internal sealed class WaylandFrameScheduler : IDisposable
{
	private readonly WlSurface _surface;
	private readonly Func<bool> _render;
	private WlCallback? _callback;
	private bool _dirty;
	private bool _disposed;
	public WaylandFrameScheduler(WlSurface surface, Func<bool> render)
	{
		_surface = surface;
		_render = render;
	}
	public void Invalidate()
	{
		if (_disposed)
		{
			return;
		}
		_dirty = true;
		TryRender();
	}
	public void BufferAvailable() => TryRender();
	private void TryRender()
	{
		if (_disposed || !_dirty || _callback is not null)
		{
			return;
		}
		_dirty = false;
		_callback = _surface.Frame();
		_callback.Done += OnDone;
		try
		{
			if (!_render())
			{
				CancelCallback();
				_dirty = true;
			}
		}
		catch
		{
			CancelCallback();
			_dirty = true;
			throw;
		}
	}
	private void OnDone(object? sender, EventArgs args)
	{
		CancelCallback();
		TryRender();
	}
	private void CancelCallback()
	{
		if (_callback is
		{
		}
		callback)
		{
			_callback = null;
			callback.Done -= OnDone;
			callback.Dispose();
		}
	}
	public void Dispose()
	{
		_disposed = true;
		CancelCallback();
	}
}
