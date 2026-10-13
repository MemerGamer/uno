// Licensed under the MIT License.
#nullable enable
using System;
using System.Globalization;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland.Rendering;

// Draws offscreen with a surfaceless context; binds the window only to copy and swap the completed frame.
internal sealed class WaylandEglRenderer : IDisposable
{
	private const int EglNone = 0x3038;
	private const int EglSurfaceType = 0x3033;
	private const int EglWindowBit = 4;
	private const int EglRenderableType = 0x3040;
	private const int EglGles2Bit = 4;
	private const int EglRedSize = 0x3024;
	private const int EglGreenSize = 0x3023;
	private const int EglBlueSize = 0x3022;
	private const int EglAlphaSize = 0x3021;
	private const int EglStencilSize = 0x3026;
	private const int EglSampleBuffers = 0x3032;
	private const int EglSamples = 0x3031;
	private const int EglMinSwapInterval = 0x303B;
	private const int EglContextClientVersion = 0x3098;
	private const int EglContextLost = 0x300E;
	private const uint EglGlesApi = 0x30A0;
	private const uint GlRgba8 = 0x8058;

	private readonly IntPtr _window;
	private readonly Action _releaseRetainedLayer;
	private IntPtr _display;
	private IntPtr _config;
	private IntPtr _context;
	private IntPtr _eglSurface;
	private GRGlInterface? _glInterface;
	private GRContext? _grContext;
	private GRBackendRenderTarget? _target;
	private SKSurface? _surface;
	private SKSurface? _windowSurface;
	private bool _surfaceless = true;
	private bool _swapIntervalSet;
	private int _width;
	private int _height;
	private int _samples;
	private int _stencil;
	private bool _disposed;

	public WaylandEglRenderer(IntPtr display, WlSurface surface, Action releaseRetainedLayer)
	{
		_releaseRetainedLayer = releaseRetainedLayer;
		try
		{
			_display = WaylandEglDisplay.Acquire(display);
			Require(WaylandRenderNative.eglBindAPI(EglGlesApi) != 0, "eglBindAPI");
			int[] attributes = [EglSurfaceType, EglWindowBit, EglRenderableType, EglGles2Bit, EglRedSize, 8, EglGreenSize, 8, EglBlueSize, 8, EglAlphaSize, 8, EglStencilSize, 8, EglNone];
			Require(WaylandRenderNative.eglChooseConfig(_display, attributes, null, 0, out var count) != 0 && count > 0, "eglChooseConfig");
			var configs = new IntPtr[count];
			Require(WaylandRenderNative.eglChooseConfig(_display, attributes, configs, configs.Length, out count) != 0, "eglChooseConfig");
			_config = WaylandEglConfig.Select(configs.AsSpan(0, count), config =>
				WaylandRenderNative.eglGetConfigAttrib(_display, config, EglMinSwapInterval, out var interval) != 0 ? interval : null,
				config => WaylandRenderNative.eglGetConfigAttrib(_display, config, EglSampleBuffers, out var buffers) != 0 ? buffers : null,
				config => WaylandRenderNative.eglGetConfigAttrib(_display, config, EglSamples, out var samples) != 0 ? samples : null);
			Require(WaylandRenderNative.eglGetConfigAttrib(_display, _config, EglSamples, out _samples) != 0, "EGL_SAMPLES");
			Require(WaylandRenderNative.eglGetConfigAttrib(_display, _config, EglStencilSize, out _stencil) != 0, "EGL_STENCIL_SIZE");
			_window = WaylandRenderNative.wl_egl_window_create(surface.Handle, 1, 1);
			Require(_window != IntPtr.Zero, "wl_egl_window_create");
			CreateContext();
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	// Set while a lost context is being replaced; a failure then means the GPU path is unusable.
	public bool IsContextLost { get; private set; }

	// Call EnsureContext before using this context for an unpresented frame.
	public GRContext? GpuContext => _grContext;

	// Content rejection needs a paced retry; a recovered context needs a fresh frame immediately.
	public WaylandRenderResult Render(int width, int height, Func<SKSurface, GRContext, bool> draw, Func<Func<bool>, bool> prepareCommit)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (width <= 0 || height <= 0)
		{
			return WaylandRenderResult.BufferUnavailable;
		}
		try
		{
			EnsureContext(width, height);
			if (!draw(_surface!, _grContext!))
			{
				return WaylandRenderResult.ContentRejected;
			}
			_grContext!.Flush();
			var result = WaylandRenderResult.BufferUnavailable;
			var presented = prepareCommit(() =>
			{
				if (!MakeCurrent(window: true))
				{
					RecreateContext(width, height);
					result = WaylandRenderResult.ContextRecovered;
					return false;
				}
				_grContext.ResetContext();
				_windowSurface ??= SKSurface.Create(_grContext, _target!, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888)
					?? throw new InvalidOperationException("Skia window surface creation failed.");
				_windowSurface.Canvas.Clear(SKColors.White);
				_windowSurface.Canvas.DrawSurface(_surface!, 0, 0);
				_windowSurface.Canvas.Flush();
				_grContext.Flush();
				if (WaylandRenderNative.eglSwapBuffers(_display, _eglSurface) != 0)
				{
					return true;
				}
				var error = WaylandRenderNative.eglGetError();
				if (error != EglContextLost)
				{
					throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"eglSwapBuffers failed: 0x{error:X}."));
				}
				RecreateContext(width, height);
				result = WaylandRenderResult.ContextRecovered;
				return false;
			});
			return presented ? WaylandRenderResult.Presented : result;
		}
		finally
		{
			_ = WaylandRenderNative.eglMakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
		}
	}

	public void EnsureContext(int width, int height)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		// Resize before any fallback window bind can reserve its next buffer.
		WaylandRenderNative.wl_egl_window_resize(_window, width, height, 0, 0);
		if (_grContext!.IsAbandoned || !MakeCurrent(window: false))
		{
			RecreateContext(width, height);
		}
		UpdateSurface(width, height);
	}

	private void UpdateSurface(int width, int height)
	{
		if (_surface is null || _width != width || _height != height)
		{
			_windowSurface?.Dispose();
			_windowSurface = null;
			_surface?.Dispose();
			_surface = null;
			_target?.Dispose();
			_target = null;
			_target = new GRBackendRenderTarget(width, height, _samples, _stencil, new GRGlFramebufferInfo(0, GlRgba8));
			_surface = SKSurface.Create(_grContext!, budgeted: true, new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
				?? throw new InvalidOperationException("Skia GPU surface creation failed.");
			_width = width;
			_height = height;
		}
	}

	private void CreateContext()
	{
		_context = WaylandRenderNative.eglCreateContext(_display, _config, IntPtr.Zero, [EglContextClientVersion, 2, EglNone]);
		Require(_context != IntPtr.Zero, "eglCreateContext");
		_eglSurface = WaylandRenderNative.eglCreateWindowSurface(_display, _config, _window, [EglNone]);
		Require(_eglSurface != IntPtr.Zero, "eglCreateWindowSurface");
		_swapIntervalSet = false;
		Require(MakeCurrent(window: false), "eglMakeCurrent");
		_glInterface = GRGlInterface.CreateGles(WaylandRenderNative.eglGetProcAddress)
			?? throw new NotSupportedException("Skia GLES interface creation failed.");
		_grContext = GRContext.CreateGl(_glInterface)
			?? throw new NotSupportedException("Skia GLES context creation failed.");
	}

	private bool MakeCurrent(bool window)
	{
		if (!window && _surfaceless)
		{
			if (WaylandRenderNative.eglMakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, _context) != 0)
			{
				return true;
			}
			_surfaceless = false;
		}
		if (WaylandRenderNative.eglMakeCurrent(_display, _eglSurface, _eglSurface, _context) == 0)
		{
			return false;
		}
		if (!_swapIntervalSet)
		{
			Require(WaylandRenderNative.eglSwapInterval(_display, 0) != 0, "eglSwapInterval");
			_swapIntervalSet = true;
		}
		return true;
	}

	private void RecreateContext(int width, int height)
	{
		if (this.Log().IsEnabled(LogLevel.Warning))
		{
			this.Log().Warn("The EGL context was lost; recreating it.");
		}
		IsContextLost = true;
		DestroyContext(abandon: true);
		CreateContext();
		UpdateSurface(width, height);
		IsContextLost = false;
	}

	private void DestroyContext(bool abandon)
	{
		try
		{
			if (abandon)
			{
				_grContext?.AbandonContext(releaseResources: false);
			}
			WaylandCleanup.Run(
				_releaseRetainedLayer,
				() => _windowSurface?.Dispose(),
				() => _surface?.Dispose(),
				() => _target?.Dispose(),
				() => _grContext?.Dispose(),
				() => _glInterface?.Dispose());
		}
		finally
		{
			_windowSurface = null;
			_surface = null;
			_target = null;
			_grContext = null;
			_glInterface = null;
			_width = _height = 0;
			if (_display != IntPtr.Zero)
			{
				_ = WaylandRenderNative.eglMakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
				if (_eglSurface != IntPtr.Zero)
				{
					_ = WaylandRenderNative.eglDestroySurface(_display, _eglSurface);
				}
				if (_context != IntPtr.Zero)
				{
					_ = WaylandRenderNative.eglDestroyContext(_display, _context);
				}
			}
			_eglSurface = IntPtr.Zero;
			_context = IntPtr.Zero;
		}
	}

	private static void Require(bool success, string operation)
	{
		if (!success)
		{
			throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{operation} failed: 0x{WaylandRenderNative.eglGetError():X}."));
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		var abandon = IsContextLost || _grContext?.IsAbandoned == true;
		if (_context != IntPtr.Zero && _eglSurface != IntPtr.Zero)
		{
			abandon |= WaylandRenderNative.eglMakeCurrent(_display, _eglSurface, _eglSurface, _context) == 0;
		}
		try
		{
			DestroyContext(abandon);
		}
		finally
		{
			if (_window != IntPtr.Zero)
			{
				WaylandRenderNative.wl_egl_window_destroy(_window);
			}
			if (_display != IntPtr.Zero)
			{
				WaylandEglDisplay.Release(_display);
				_display = IntPtr.Zero;
			}
		}
	}
}
