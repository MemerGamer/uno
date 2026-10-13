// Licensed under the MIT License.
#nullable enable
using System;
using System.Globalization;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland.Rendering;

// Renders straight into the EGL window surface (framebuffer zero), like X11OpenGLRenderer does with GLX.
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
				WaylandRenderNative.eglGetConfigAttrib(_display, config, EglMinSwapInterval, out var interval) != 0 ? interval : null);
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

	public bool Render(int width, int height, Action<SKSurface, GRContext> draw)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (width <= 0 || height <= 0)
		{
			return false;
		}
		EnsureContext(width, height);
		draw(_surface!, _grContext!);
		_grContext!.Flush();
		// eglSwapBuffers commits the wl_surface, including the frame callback requested by the scheduler.
		if (WaylandRenderNative.eglSwapBuffers(_display, _eglSurface) == 0)
		{
			var error = WaylandRenderNative.eglGetError();
			if (error != EglContextLost)
			{
				throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"eglSwapBuffers failed: 0x{error:X}."));
			}
			RecreateContext(width, height);
			return false;
		}
		return true;
	}

	public void EnsureContext(int width, int height)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_grContext!.IsAbandoned || WaylandRenderNative.eglMakeCurrent(_display, _eglSurface, _eglSurface, _context) == 0)
		{
			RecreateContext(width, height);
		}
		UpdateSurface(width, height);
	}

	private void UpdateSurface(int width, int height)
	{
		if (_surface is null || _width != width || _height != height)
		{
			_surface?.Dispose();
			_surface = null;
			_target?.Dispose();
			_target = null;
			WaylandRenderNative.wl_egl_window_resize(_window, width, height, 0, 0);
			_target = new GRBackendRenderTarget(width, height, _samples, _stencil, new GRGlFramebufferInfo(0, GlRgba8));
			_surface = SKSurface.Create(_grContext!, _target, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888)
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
		Require(WaylandRenderNative.eglMakeCurrent(_display, _eglSurface, _eglSurface, _context) != 0, "eglMakeCurrent");
		// Frame callbacks pace presentation; a swap interval would make eglSwapBuffers block while the surface is hidden.
		Require(WaylandRenderNative.eglSwapInterval(_display, 0) != 0, "eglSwapInterval");
		_glInterface = GRGlInterface.CreateGles(WaylandRenderNative.eglGetProcAddress)
			?? throw new NotSupportedException("Skia GLES interface creation failed.");
		_grContext = GRContext.CreateGl(_glInterface)
			?? throw new NotSupportedException("Skia GLES context creation failed.");
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
		if (abandon)
		{
			_grContext?.AbandonContext(releaseResources: false);
		}
		_releaseRetainedLayer();
		_surface?.Dispose();
		_surface = null;
		_target?.Dispose();
		_target = null;
		_width = _height = 0;
		_grContext?.Dispose();
		_grContext = null;
		_glInterface?.Dispose();
		_glInterface = null;
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
		DestroyContext(abandon);
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
