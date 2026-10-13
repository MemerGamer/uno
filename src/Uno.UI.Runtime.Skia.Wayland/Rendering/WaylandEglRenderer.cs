// Licensed under the MIT License.
#nullable enable
using System;
using SkiaSharp;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland.Rendering;

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
	private const int EglContextClientVersion = 0x3098;
	private const uint EglGlesApi = 0x30A0;
	private const uint GlRgba8 = 0x8058;

	private IntPtr _display;
	private IntPtr _window;
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
	public WaylandEglRenderer(IntPtr display, WlSurface surface)
	{
		try
		{
			_display = WaylandEglDisplay.Acquire(display);
			Require(WaylandRenderNative.eglBindAPI(EglGlesApi) != 0, "eglBindAPI");
			Require(WaylandRenderNative.eglChooseConfig(_display,
				[EglSurfaceType, EglWindowBit, EglRenderableType, EglGles2Bit, EglRedSize, 8, EglGreenSize, 8, EglBlueSize, 8, EglAlphaSize, 8, EglStencilSize, 8, EglNone],
				out var config, 1, out var count) != 0 && count > 0, "eglChooseConfig");
			Require(WaylandRenderNative.eglGetConfigAttrib(_display, config, EglSamples, out _samples) != 0, "EGL_SAMPLES");
			Require(WaylandRenderNative.eglGetConfigAttrib(_display, config, EglStencilSize, out _stencil) != 0, "EGL_STENCIL_SIZE");
			_window = WaylandRenderNative.wl_egl_window_create(surface.Handle, 1, 1);
			Require(_window != IntPtr.Zero, "wl_egl_window_create");
			_context = WaylandRenderNative.eglCreateContext(_display, config, IntPtr.Zero, [EglContextClientVersion, 2, EglNone]);
			Require(_context != IntPtr.Zero, "eglCreateContext");
			_eglSurface = WaylandRenderNative.eglCreateWindowSurface(_display, config, _window, [EglNone]);
			Require(_eglSurface != IntPtr.Zero, "eglCreateWindowSurface");
			MakeCurrent();
			_glInterface = GRGlInterface.CreateGles(WaylandRenderNative.eglGetProcAddress)
				?? throw new NotSupportedException("Skia GLES interface creation failed.");
			_grContext = GRContext.CreateGl(_glInterface)
				?? throw new NotSupportedException("Skia GLES context creation failed.");
		}
		catch
		{
			Dispose();
			throw;
		}
	}
	public bool Render(int width, int height, Func<SKSurface, bool> draw)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (width <= 0 || height <= 0)
		{
			return false;
		}
		MakeCurrent();
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
		if (!draw(_surface))
		{
			return false;
		}
		_surface.Canvas.Flush();
		_grContext!.Flush();
		Require(WaylandRenderNative.eglSwapBuffers(_display, _eglSurface) != 0, "eglSwapBuffers");
		return true;
	}
	private void MakeCurrent() => Require(WaylandRenderNative.eglMakeCurrent(_display, _eglSurface, _eglSurface, _context) != 0, "eglMakeCurrent");
	private static void Require(bool success, string operation)
	{
		if (!success) { throw new InvalidOperationException($"{operation} failed."); }
	}
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		if (_context != IntPtr.Zero && _eglSurface != IntPtr.Zero)
		{
			WaylandRenderNative.eglMakeCurrent(_display, _eglSurface, _eglSurface, _context);
		}
		_surface?.Dispose();
		_target?.Dispose();
		_grContext?.Dispose();
		_glInterface?.Dispose();
		if (_display != IntPtr.Zero)
		{
			WaylandRenderNative.eglMakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
			if (_eglSurface != IntPtr.Zero)
			{
				WaylandRenderNative.eglDestroySurface(_display, _eglSurface);
			}
			if (_context != IntPtr.Zero)
			{
				WaylandRenderNative.eglDestroyContext(_display, _context);
			}
		}
		if (_window != IntPtr.Zero)
		{
			WaylandRenderNative.wl_egl_window_destroy(_window);
		}
		if (_display != IntPtr.Zero)
		{
			WaylandEglDisplay.Release(_display);
		}
	}
}
