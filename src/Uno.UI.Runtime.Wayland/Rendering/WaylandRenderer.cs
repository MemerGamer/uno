#nullable enable

using System;
using Microsoft.UI.Xaml.Media;
using Uno.UI.Composition.Drawing;
using Uno.UI.Hosting;

namespace Uno.UI.Runtime.Wayland.Rendering;

/// <summary>
/// Drives one window's frames over its negotiated graphics context, paced by wl_surface frame callbacks.
/// All members run on the window's render thread.
/// </summary>
internal sealed class WaylandRenderer : IDisposable
{
	private readonly IXamlRootHost _host;
	private readonly IWaylandGraphicsContext _context;
	private readonly IDrawingFactory _drawingFactory;
	private readonly WaylandFrameScheduler _scheduler;
	private int _width;
	private int _height;
	private bool _configured;
	private bool _disposed;

	public WaylandRenderer(IXamlRootHost host, WaylandRenderScale scale, IWaylandGraphicsContext context, IDrawingFactory drawingFactory, Protocols.WlSurface surface, Action<Action> postToOwner)
	{
		_host = host;
		Scale = scale;
		_context = context;
		_drawingFactory = drawingFactory;
		_scheduler = new WaylandFrameScheduler(surface, Render, postToOwner);
		if (context is WaylandSoftwareGraphicsContext software)
		{
			software.BufferAvailable += OnBufferAvailable;
		}
	}

	public WaylandRenderScale Scale { get; }

	public bool IsConfigured => _configured;

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

	public void Invalidate()
	{
		if (_configured)
		{
			_scheduler.Invalidate();
		}
		else
		{
			// Uno schedules its next UI frame from the native frame request, so keep consuming frames before the
			// first configure even though nothing can be committed yet.
			Render();
		}
	}

	// Called by the graphics context right before it commits a buffer: the scale and viewport must change in the
	// same commit as the buffer they describe.
	public bool PrepareCommit()
	{
		if (!_configured || _disposed)
		{
			return false;
		}
		Scale.Apply(_width, _height);
		return true;
	}

	private void OnBufferAvailable(object? sender, EventArgs args) => _scheduler.BufferAvailable();

	private bool Render()
	{
		if (_disposed || _host.RootElement?.Visual.CompositionTarget is not CompositionTarget compositionTarget)
		{
			return false;
		}
		// Each window owns its context, so its backend factory is bound to this window's CompositionTarget.
		compositionTarget.Renderer = _drawingFactory;
		compositionTarget.OnNativePlatformFrameRequested(_context);
		return _context.Committed;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_scheduler.Dispose();
		if (_context is WaylandSoftwareGraphicsContext software)
		{
			software.BufferAvailable -= OnBufferAvailable;
		}
		// The backend factory owns GPU resources bound to the context, so it goes first, with the context current.
		(_context as IWaylandGpuTeardownContext)?.MakeCurrentForTeardown();
		(_drawingFactory as IDisposable)?.Dispose();
		_context.Dispose();
	}
}
