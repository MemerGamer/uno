// Licensed under the MIT License.
#nullable enable
using System;
using SkiaSharp;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland.Rendering;

internal sealed class WaylandRenderer : IDisposable
{
	private readonly WaylandEglRenderer? _egl;
	private readonly WaylandSoftwareRenderer? _software;
	private readonly WaylandFrameScheduler _scheduler;
	private readonly Func<SKSurface, bool> _draw;
	private int _width;
	private int _height;
	private bool _configured;
	private bool _disposed;
	public WaylandRenderScale Scale { get; }
	public bool IsSoftware => _software is not null;
	public bool IsConfigured => _configured;
	public Exception? EglInitializationError { get; }
	public WaylandRenderer(IntPtr display, WlSurface surface, WlShm shm, WpViewport? viewport, Func<SKSurface, bool> draw, Action<Action> postToOwner, bool forceSoftware = false)
	{
		_draw = draw;
		Scale = new WaylandRenderScale(surface, viewport);
		if (!forceSoftware && Environment.GetEnvironmentVariable("UNO_WAYLAND_FORCE_SOFTWARE") != "1")
		{
			try
			{
				_egl = new WaylandEglRenderer(display, surface);
			}
			catch (Exception error) when (error is InvalidOperationException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
			{
				EglInitializationError = error;
			}
		}
		if (_egl is null)
		{
			_software = new WaylandSoftwareRenderer(surface, shm, postToOwner);
			_software.BufferAvailable += OnBufferAvailable;
		}
		_scheduler = new WaylandFrameScheduler(surface, Render, postToOwner);
	}
	// Call only after acknowledging the associated xdg_surface.configure serial.
	public void Configure(int logicalWidth, int logicalHeight)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (logicalWidth <= 0 || logicalHeight <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(logicalWidth));
		}
		_width = logicalWidth;
		_height = logicalHeight;
		_configured = true;
		_scheduler.Invalidate();
	}
	public void Invalidate() => _scheduler.Invalidate();
	private void OnBufferAvailable(object? sender, EventArgs args) => _scheduler.BufferAvailable();
	private bool Render()
	{
		if (!_configured)
		{
			return false;
		}
		var size = Scale.Apply(_width, _height);
		return _egl is not null ? _egl.Render(size.Width, size.Height, _draw) : _software!.Render(size.Width, size.Height, _draw);
	}
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_scheduler.Dispose();
		if (_software is not null)
		{
			_software.BufferAvailable -= OnBufferAvailable;
		}
		_software?.Dispose();
		_egl?.Dispose();
	}
}
