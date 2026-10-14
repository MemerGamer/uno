#nullable enable

using System;
using Microsoft.UI.Xaml.Media;
using Uno.UI.Composition.Drawing;
using Uno.UI.Hosting;

namespace Uno.UI.Runtime.Wayland.Rendering;

/// <summary>
/// Decides whether a frame may be committed: the frame drawn must show the layout of the configure it would
/// acknowledge. All members run on the window's render thread.
/// </summary>
internal interface IWaylandFrameGate
{
	/// <summary>Called before Uno draws its latest recorded frame.</summary>
	void BeginFrame();

	/// <summary>Called right before the commit, with the drawn frame's size and the configured buffer size.</summary>
	bool CanPresent(int frameWidth, int frameHeight, int bufferWidth, int bufferHeight);

	/// <summary>Called after a frame was committed or consumed by the liveness fallback.</summary>
	void EndFrame();
}

/// <summary>
/// Drives one window's frames over its negotiated graphics context, paced by wl_surface frame callbacks.
/// All members run on the window's render thread.
/// </summary>
internal sealed class WaylandRenderer : IDisposable, ISwapChain
{
	private readonly IXamlRootHost _host;
	private readonly IWaylandFrameGate _gate;
	private readonly WaylandFrameScheduler _scheduler;
	private IWaylandGraphicsContext _context;
	private IDrawingFactory _drawingFactory;
	private int _width;
	private int _height;
	private bool _configured;
	private bool _commitAllowed;
	private bool _disposed;

	public WaylandRenderer(IXamlRootHost host, IWaylandFrameGate gate, WaylandRenderScale scale, IWaylandGraphicsContext context, IDrawingFactory drawingFactory, Protocols.WlSurface surface, Action<Action> postToOwner)
	{
		_host = host;
		_gate = gate;
		Scale = scale;
		_context = context;
		_drawingFactory = drawingFactory;
		_scheduler = new WaylandFrameScheduler(surface, Render, ProduceUnpresentedFrame, postToOwner);
		Attach(context);
	}

	public WaylandRenderScale Scale { get; }

	public bool IsConfigured => _configured;

	// Stages the size; the configure is acknowledged right before the first matching commit.
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
			ProduceUnpresentedFrame();
		}
	}

	// Replaces a lost context with a newly negotiated one; the backend factory is bound to its context.
	public void ReplaceGraphics(IWaylandGraphicsContext context, IDrawingFactory drawingFactory)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ReleaseGraphics();
		_context = context;
		_drawingFactory = drawingFactory;
		Attach(context);
		Invalidate();
	}

	// Called by the graphics context right before it commits a buffer: the configure acknowledgement, scale and
	// viewport must change in the same commit as the buffer they describe.
	public bool PrepareCommit(int width, int height)
	{
		if (!_configured || _disposed || !_commitAllowed)
		{
			return false;
		}
		var scale = Scale.EffectiveScale;
		if (!_gate.CanPresent(width, height, WaylandRenderScale.ToBufferPixels(_width, scale), WaylandRenderScale.ToBufferPixels(_height, scale)))
		{
			return false;
		}
		return true;
	}

	internal void ApplyScale() => Scale.Apply(_width, _height);

	private bool Render() => Draw(commit: true);

	// Compositors withhold frame callbacks from hidden surfaces; Uno keeps getting frames, which are not committed.
	private void ProduceUnpresentedFrame() => Draw(commit: false);

	private bool Draw(bool commit)
	{
		if (_disposed || _context is WaylandEglGraphicsContext { IsLost: true })
		{
			return false;
		}
		_gate.BeginFrame();
		if (_host.RootElement?.Visual.CompositionTarget is not CompositionTarget compositionTarget)
		{
			_gate.EndFrame();
			return false;
		}
		_commitAllowed = commit;
		// Each window owns its context, so its backend factory is bound to this window's CompositionTarget.
		compositionTarget.Renderer = _drawingFactory;
		compositionTarget.OnNativePlatformFrameRequested(this);
		var presented = commit && _context.Committed;
		if (presented || !commit)
		{
			_gate.EndFrame();
		}
		return presented;
	}

	GraphicsContextKind IGraphicsContext.Kind => _context.Kind;
	bool ISwapChain.PreservesContents => _context.PreservesContents;

	// Even the bounded layout fallback must draw into a buffer matching the configure being acknowledged.
	IRenderTarget ISwapChain.AcquireRenderTarget(int width, int height)
		=> _configured
			? _context.AcquireRenderTarget(WaylandRenderScale.ToBufferPixels(_width, Scale.EffectiveScale), WaylandRenderScale.ToBufferPixels(_height, Scale.EffectiveScale))
			: _context.AcquireRenderTarget(width, height);

	void ISwapChain.Present() => _context.Present();

	private void Attach(IWaylandGraphicsContext context)
	{
		if (context is WaylandSoftwareGraphicsContext software)
		{
			software.BufferAvailable += OnBufferAvailable;
		}
	}

	private void OnBufferAvailable(object? sender, EventArgs args) => _scheduler.BufferAvailable();

	private void ReleaseGraphics()
	{
		if (_context is WaylandSoftwareGraphicsContext software)
		{
			software.BufferAvailable -= OnBufferAvailable;
		}
		ReleaseGraphics(_context, _drawingFactory);
	}

	internal static void ReleaseGraphics(IWaylandGraphicsContext context, IDrawingFactory drawingFactory)
	{
		try
		{
			// The backend factory owns GPU resources bound to the context, so it goes first, with the context current.
			// A context that cannot be made current is abandoned so the backend issues no GL calls into it.
			if (context is IWaylandGpuTeardownContext gpu && !gpu.TryMakeCurrentForTeardown())
			{
				(drawingFactory as IGpuDrawingFactory)?.AbandonContext();
			}
			(drawingFactory as IDisposable)?.Dispose();
		}
		finally
		{
			context.Dispose();
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_scheduler.Dispose();
		ReleaseGraphics();
	}
}
