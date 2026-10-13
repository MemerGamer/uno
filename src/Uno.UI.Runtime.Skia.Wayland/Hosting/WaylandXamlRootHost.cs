// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.UI.Hosting;
using Uno.WinUI.Runtime.Skia.Wayland.Input;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;
using Uno.WinUI.Runtime.Skia.Wayland.Rendering;
using Windows.UI.Core;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal sealed class WaylandXamlRootHost : IXamlRootHost, IDisposable
{
	private readonly Window _window;
	private readonly XamlRoot _xamlRoot;
	private readonly WaylandConnection _connection;
	private readonly int _id;
	private readonly WaylandRenderThread _renderThread;
	private readonly WaylandProtocolInputConnection? _input;
	private readonly WaylandSeat? _seat;
	private volatile bool _disposed;

	// Render thread state.
	private WaylandRenderer? _renderer;
	private SKSurface? _canvasSurface;
	private int _frame;

	// Dispatch thread state.
	private WaylandCursor? _cursor;
	private WlPointer? _cursorPointer;

	internal WaylandXamlRootHost(WaylandConnection connection, int id, Window window, XamlRoot xamlRoot, WlSurface surface, WpViewport? viewport, Action<bool> keyboardFocusChanged)
	{
		_connection = connection;
		_id = id;
		_window = window;
		_xamlRoot = xamlRoot;
		Surface = surface;
		_renderThread = new WaylandRenderThread($"Uno Wayland render {id}", OnRenderError);
		_renderThread.Post(() =>
		{
			_renderer = new WaylandRenderer(connection.Display.NativeHandle, surface, connection.Shm, viewport, Draw, _renderThread.Post);
			Microsoft.UI.Composition.Compositor.GetSharedCompositor().IsSoftwareRenderer = _renderer.IsSoftware;
			WaylandDiagnostics.Trace($"window{id}: renderer {(_renderer.IsSoftware ? "wl_shm" : "EGL")}{(_renderer.EglInitializationError is { } eglError ? $" (EGL unavailable: {eglError.Message})" : "")}");
		});

		if (connection.Seat is { } wlSeat)
		{
			_input = new WaylandProtocolInputConnection(wlSeat, s => s == surface, connection.Display.Post, SetCursor);
			_input.PointerReleasing += DisposeCursor;
			_input.InputSerial += serial => connection.InputSerial = serial;
		}
		var input = (IWaylandInputConnection?)_input ?? NoInputConnection.Instance;
		KeyboardSource = new WaylandKeyboardInputSource(input, WaylandConnection.PostToUI, keyboardFocusChanged);
		PointerSource = new WaylandPointerInputSource(input, WaylandConnection.PostToUI, () => KeyboardSource.Modifiers);
		if (_input is not null)
		{
			var seat = new WaylandSeat(connection.Seat!, _input, PointerSource, KeyboardSource);
			_seat = seat;
			// Capabilities were announced at bind time; replay them for this window's devices.
			connection.Display.Post(() => seat.Capabilities(connection.SeatCapabilities));
		}

		// Input sources must exist before registration: Uno resolves them per IXamlRootHost.
		XamlRootMap.Register(xamlRoot, this);
	}

	internal WlSurface Surface { get; }
	internal WaylandPointerInputSource PointerSource { get; }
	internal WaylandKeyboardInputSource KeyboardSource { get; }
	// Written on the UI thread, read by the dispatch thread for cursor scaling.
	internal double Scale { get; set; } = 1;
	public UIElement? RootElement => _window.RootElement;

	public void InvalidateRender()
	{
		if (!_disposed)
		{
			_renderThread.Post(() =>
			{
				if (_renderer is { IsConfigured: true } renderer)
				{
					renderer.Invalidate();
				}
				else
				{
					// Uno schedules its next UI frame from the native frame request, so keep consuming
					// frames before the first configure even though nothing can be presented yet.
					RenderContent();
				}
			});
		}
	}

	// Called after the configure serial was acknowledged on the dispatch thread.
	internal void Configure(int logicalWidth, int logicalHeight, uint preferredScale, int integerScale)
	{
		_renderThread.Post(() =>
		{
			if (_renderer is not { } renderer)
			{
				return;
			}
			if (preferredScale != 0)
			{
				renderer.Scale.SetPreferredScale(preferredScale);
			}
			renderer.Scale.SetIntegerScale(integerScale);
			renderer.Configure(logicalWidth, logicalHeight);
		});
	}

	// Render thread. Uno draws into a persistent raster surface because CompositionTarget clips
	// later frames to their damage; the result is copied into the wl_buffer being presented.
	private void Draw(SKSurface target)
	{
		RenderContent();
		var canvas = target.Canvas;
		canvas.Clear(SKColors.White);
		if (_canvasSurface is { } source)
		{
			source.Flush();
			canvas.DrawSurface(source, 0, 0);
		}
		canvas.Flush();
		_frame++;
		if (WaylandDiagnostics.IsEnabled)
		{
			var bounds = canvas.DeviceClipBounds;
			var content = _canvasSurface?.Canvas.DeviceClipBounds ?? SKRectI.Empty;
			WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: present frame {_frame} buffer {bounds.Width}x{bounds.Height} content {content.Width}x{content.Height}"));
			WaylandDiagnostics.DumpFrame(_id, _frame, target);
		}
	}

	private void RenderContent()
	{
		if (RootElement?.Visual.CompositionTarget is CompositionTarget compositionTarget)
		{
			compositionTarget.OnNativePlatformFrameRequested(_canvasSurface?.Canvas, size =>
			{
				_canvasSurface?.Dispose();
				_canvasSurface = SKSurface.Create(new SKImageInfo(Math.Max((int)size.Width, 1), Math.Max((int)size.Height, 1), SKColorType.Bgra8888, SKAlphaType.Premul))
					?? throw new InvalidOperationException("Skia raster surface creation failed.");
				_canvasSurface.Canvas.Clear(SKColors.White);
				return _canvasSurface.Canvas;
			});
		}
	}

	private void OnRenderError(Exception error)
	{
		if (this.Log().IsEnabled(LogLevel.Error))
		{
			this.Log().Error($"Wayland rendering failed for window {_id}.", error);
		}
		WaylandDiagnostics.Trace($"window{_id}: render error {error}");
	}

	// Dispatch thread.
	private void SetCursor(uint serial, CoreCursor? cursor)
	{
		if (_disposed || _input?.Pointer is not { } pointer)
		{
			return;
		}
		if (_cursor is null || _cursorPointer != pointer)
		{
			DisposeCursor();
			_cursor = new WaylandCursor(
				_connection.Shm.Handle,
				new WaylandCursorSurface(pointer, _connection.Compositor.CreateSurface()),
				_connection.CursorShape?.GetPointer(pointer));
			_cursorPointer = pointer;
		}
		_cursor.Set(serial, cursor, Scale);
	}

	private void DisposeCursor()
	{
		_cursor?.Dispose();
		_cursor = null;
		_cursorPointer = null;
	}

	// UI thread. Protocol objects are destroyed after the renderer has released its buffers.
	internal void Dispose(Action destroyProtocolObjects)
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		XamlRootMap.Unregister(_xamlRoot);
		var display = _connection.Display;
		display.Post(() =>
		{
			_seat?.Dispose();
			DisposeCursor();
		});
		_renderThread.Post(() =>
		{
			_renderer?.Dispose();
			_renderer = null;
			_canvasSurface?.Dispose();
			_canvasSurface = null;
			display.Post(destroyProtocolObjects);
		});
		_renderThread.Dispose();
	}

	void IDisposable.Dispose() => Dispose(() => { });

	// Used when the compositor advertises no wl_seat: sources exist but never receive devices.
	private sealed class NoInputConnection : IWaylandInputConnection
	{
		internal static readonly NoInputConnection Instance = new();
		public IDisposable GetPointer(WaylandPointerInputSource sink) => throw new NotSupportedException();
		public IDisposable GetKeyboard(WaylandKeyboardInputSource sink) => throw new NotSupportedException();
		public IDisposable GetTouch(WaylandPointerInputSource sink) => throw new NotSupportedException();
		public void SetCursor(uint enterSerial, CoreCursor? cursor) { }
		public void Post(Action action) { }
	}
}
