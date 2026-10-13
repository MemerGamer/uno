#nullable enable

using System;
using Uno.Foundation.Logging;
using Uno.UI.Composition.Drawing;
using Uno.UI.Runtime.Wayland.Protocols;

namespace Uno.UI.Runtime.Wayland.Rendering;

/// <summary>
/// OpenGL ES graphics context: an EGL window surface on a wl_egl_window. The frame is composed into a retained
/// framebuffer, blitted to the default framebuffer and swapped at present. The context is current on the render
/// thread only between <see cref="AcquireRenderTarget"/> and <see cref="Present"/>.
/// </summary>
internal sealed class WaylandEglGraphicsContext : IWaylandGraphicsContext, IGLDeviceContext, IWaylandGpuTeardownContext
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
	private const uint DefaultFramebuffer = 0;

	private readonly Func<bool> _prepareCommit;
	private readonly GLRetainedFramebuffer _retained = new(WaylandRenderNative.eglGetProcAddress);
	private IntPtr _display;
	private IntPtr _window;
	private IntPtr _context;
	private IntPtr _eglSurface;
	private int _samples;
	private int _stencil;
	private int _windowWidth = 1;
	private int _windowHeight = 1;
	private bool _contentsPreserved;
	private bool _frameAcquired;
	private bool _disposed;
	private EglRenderTarget? _target;

	public WaylandEglGraphicsContext(IntPtr wlDisplay, WlSurface surface, Func<bool> prepareCommit)
	{
		_prepareCommit = prepareCommit;
		try
		{
			_display = WaylandEglDisplay.Acquire(wlDisplay);
			Require(WaylandRenderNative.eglBindAPI(EglGlesApi) != 0, "eglBindAPI");
			Require(WaylandRenderNative.eglChooseConfig(_display,
				[EglSurfaceType, EglWindowBit, EglRenderableType, EglGles2Bit, EglRedSize, 8, EglGreenSize, 8, EglBlueSize, 8, EglAlphaSize, 8, EglStencilSize, 8, EglNone],
				out var config, 1, out var count) != 0 && count > 0, "eglChooseConfig");
			Require(WaylandRenderNative.eglGetConfigAttrib(_display, config, EglSamples, out _samples) != 0, "EGL_SAMPLES");
			Require(WaylandRenderNative.eglGetConfigAttrib(_display, config, EglStencilSize, out _stencil) != 0, "EGL_STENCIL_SIZE");
			_window = WaylandRenderNative.wl_egl_window_create(surface.Handle, _windowWidth, _windowHeight);
			Require(_window != IntPtr.Zero, "wl_egl_window_create");
			_context = WaylandRenderNative.eglCreateContext(_display, config, IntPtr.Zero, [EglContextClientVersion, 2, EglNone]);
			Require(_context != IntPtr.Zero, "eglCreateContext");
			_eglSurface = WaylandRenderNative.eglCreateWindowSurface(_display, config, _window, [EglNone]);
			Require(_eglSurface != IntPtr.Zero, "eglCreateWindowSurface");
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	public GraphicsContextKind Kind => GraphicsContextKind.OpenGLES;

	public Func<string, nint> GetProcAddress => WaylandRenderNative.eglGetProcAddress;

	// A swap leaves the default framebuffer undefined; the retained framebuffer carries the previous frame forward.
	public bool PreservesContents => _contentsPreserved;

	public bool Committed { get; private set; }

	public IRenderTarget AcquireRenderTarget(int width, int height)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		width = Math.Max(1, width);
		height = Math.Max(1, height);
		if (width != _windowWidth || height != _windowHeight)
		{
			WaylandRenderNative.wl_egl_window_resize(_window, width, height, 0, 0);
			_windowWidth = width;
			_windowHeight = height;
		}
		MakeCurrent();
		var retained = _retained.TryResize(width, height, out _contentsPreserved);
		var id = retained ? _retained.FramebufferId : DefaultFramebuffer;
		if (_target is null || _target.Width != width || _target.Height != height || _target.FramebufferId != id)
		{
			// Single-sampled when retained: the blit cannot resolve a multisampled attachment.
			_target = new EglRenderTarget(width, height, retained ? 0 : _samples, retained ? _retained.StencilBits : _stencil, id);
		}
		_frameAcquired = true;
		return _target;
	}

	public void Present()
	{
		Committed = false;
		if (!_frameAcquired)
		{
			return;
		}
		_frameAcquired = false;
		// eglSwapBuffers commits the surface, so a frame is only swapped once the surface may take a buffer.
		if (_prepareCommit())
		{
			_retained.BlitToDefault();
			if (WaylandRenderNative.eglSwapBuffers(_display, _eglSurface) != 0)
			{
				Committed = true;
			}
			else
			{
				this.LogError()?.Error("eglSwapBuffers failed.");
			}
		}
		WaylandRenderNative.eglMakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
	}

	public void MakeCurrentForTeardown() => MakeCurrent();

	private void MakeCurrent() => Require(WaylandRenderNative.eglMakeCurrent(_display, _eglSurface, _eglSurface, _context) != 0, "eglMakeCurrent");

	private static void Require(bool success, string operation)
	{
		if (!success)
		{
			throw new InvalidOperationException($"{operation} failed.");
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		if (_display != IntPtr.Zero)
		{
			// The GL objects belong to this context, so they have to go while it is still current.
			if (_context != IntPtr.Zero && _eglSurface != IntPtr.Zero
				&& WaylandRenderNative.eglMakeCurrent(_display, _eglSurface, _eglSurface, _context) != 0)
			{
				_retained.Dispose();
			}
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

	private sealed class EglRenderTarget(int width, int height, int sampleCount, int stencilBits, uint framebufferId) : IGLRenderTarget
	{
		public uint FramebufferId => framebufferId;
		public int SampleCount => sampleCount;
		public int StencilBits => stencilBits;
		public int Width => width;
		public int Height => height;
		public GraphicsColorFormat ColorFormat => GraphicsColorFormat.Rgba8888;
		public void Dispose() { }
	}
}
