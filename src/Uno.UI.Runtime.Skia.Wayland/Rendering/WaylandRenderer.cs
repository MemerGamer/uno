// Licensed under the MIT License.
#nullable enable
using System;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland.Rendering;

internal sealed class WaylandRenderer : IDisposable
{
	private readonly WlSurface _surface;
	private readonly WlShm _shm;
	private readonly Action<Action> _postToOwner;
	private readonly WaylandFrameScheduler _scheduler;
	private readonly Func<SKSurface, GRContext?, bool> _draw;
	private readonly Func<Func<bool>, bool> _prepareCommit;
	private WaylandEglRenderer? _egl;
	private WaylandSoftwareRenderer? _software;
	private int _width;
	private int _height;
	private bool _configured;
	private bool _disposed;
	public WaylandRenderScale Scale { get; }
	public bool IsSoftware => _software is not null;
	public bool IsConfigured => _configured;
	public Exception? EglInitializationError { get; private set; }
	// Null for wl_shm; otherwise the context the retained layer must be created with.
	public GRContext? GpuContext => _egl?.GpuContext;
	// Raised on the owner thread when an unrecoverable GPU failure switched rendering to wl_shm.
	public event EventHandler? BackendChanged;

	// Both frame paths validate the GPU context before the host draws its retained layer.
	public WaylandRenderer(IntPtr display, WlSurface surface, WlShm shm, WpViewport? viewport, Func<SKSurface, GRContext?, bool> draw, Action<GRContext?> produceUnpresentedFrame, Action releaseRetainedLayer, Action<Action> postToOwner, Func<Func<bool>, bool> prepareCommit, Func<long> getGeneration, Func<long, bool> frameOpportunity, Action<bool> attemptActive, bool forceSoftware = false)
	{
		_surface = surface;
		_shm = shm;
		_draw = draw;
		_prepareCommit = prepareCommit;
		_postToOwner = postToOwner;
		Scale = new WaylandRenderScale(surface, viewport);
		try
		{
			if (!forceSoftware && Environment.GetEnvironmentVariable("UNO_WAYLAND_FORCE_SOFTWARE") != "1")
			{
				try
				{
					_egl = new WaylandEglRenderer(display, surface, releaseRetainedLayer);
				}
				catch (Exception error) when (error is InvalidOperationException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
				{
					EglInitializationError = error;
				}
			}
			if (_egl is null)
			{
				UseSoftware();
			}
			_scheduler = new WaylandFrameScheduler(surface, Render, () => ProduceUnpresentedFrame(produceUnpresentedFrame), postToOwner, getGeneration, frameOpportunity, attemptActive);
		}
		catch
		{
			WaylandCleanup.Run(
				() => _software?.Dispose(),
				() => _egl?.Dispose());
			throw;
		}
	}

	// Stage size; the host acknowledges the configure immediately before a matching commit.
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
		_scheduler.PresentationReady();
	}

	public void Invalidate() => _scheduler.Invalidate();

	public void ContentReady() => _scheduler.ContentReady();

	public void PresentationReady() => _scheduler.PresentationReady();

	public void ProduceUnpresentedFrame(Action<GRContext?> produce)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_egl is { } egl)
		{
			try
			{
				var size = BufferSize;
				egl.EnsureContext(size.Width, size.Height);
			}
			catch (Exception error) when (egl.IsContextLost)
			{
				FallBackToSoftware(error);
			}
		}
		produce(GpuContext);
	}

	private void UseSoftware()
	{
		_software = new WaylandSoftwareRenderer(_surface, _shm, _postToOwner);
		_software.BufferAvailable += OnBufferAvailable;
	}

	private void OnBufferAvailable(object? sender, EventArgs args) => _scheduler.BufferAvailable();

	private WaylandRenderResult Render()
	{
		if (!_configured)
		{
			return WaylandRenderResult.BufferUnavailable;
		}
		var size = BufferSize;
		if (_egl is { } egl)
		{
			try
			{
				return egl.Render(size.Width, size.Height, (surface, context) => _draw(surface, context), _prepareCommit);
			}
			catch (Exception error) when (egl.IsContextLost)
			{
				FallBackToSoftware(error);
				return WaylandRenderResult.ContextRecovered;
			}
		}
		return _software!.Render(size.Width, size.Height, surface => _draw(surface, null), _prepareCommit);
	}

	internal (int Width, int Height) BufferSize
		=> (WaylandRenderScale.ToBufferPixels(Math.Max(_width, 1), Scale.EffectiveScale),
			WaylandRenderScale.ToBufferPixels(Math.Max(_height, 1), Scale.EffectiveScale));

	internal void ApplyScale() => Scale.Apply(_width, _height);

	private void FallBackToSoftware(Exception error)
	{
		if (this.Log().IsEnabled(LogLevel.Error))
		{
			this.Log().Error("The EGL context could not be recreated; falling back to wl_shm rendering.", error);
		}
		EglInitializationError = error;
		_egl!.Dispose();
		_egl = null;
		UseSoftware();
		BackendChanged?.Invoke(this, EventArgs.Empty);
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
