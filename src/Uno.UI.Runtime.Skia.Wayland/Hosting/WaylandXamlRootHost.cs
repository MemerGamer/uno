// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.UI.Helpers;
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

	// Render thread state. Uno draws into a persistent layer because CompositionTarget clips later frames
	// to their damage: a GPU retained layer with EGL, a raster surface copied into the wl_buffer with wl_shm.
	private WaylandRenderer? _renderer;
	private readonly RetainedLayer _retainedLayer = new();
	private GRContext? _layerContext;
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
			_renderer = new WaylandRenderer(connection.Display.NativeHandle, surface, connection.Shm, viewport, Draw, RenderContent, ReleaseRetainedLayer, _renderThread.Post);
			_renderer.BackendChanged += (_, _) => OnBackendSelected();
			OnBackendSelected();
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
					_renderer?.ProduceUnpresentedFrame(RenderContent);
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

	// Render thread.
	private void OnBackendSelected()
	{
		var renderer = _renderer!;
		Microsoft.UI.Composition.Compositor.GetSharedCompositor().IsSoftwareRenderer = renderer.IsSoftware;
		WaylandDiagnostics.Trace($"window{_id}: renderer {(renderer.IsSoftware ? "wl_shm" : "EGL")}{(renderer.EglInitializationError is { } eglError ? $" (EGL unavailable: {eglError.Message})" : "")}");
	}

	private void Draw(SKSurface target, GRContext? gpuContext)
	{
		RenderContent(gpuContext);
		var canvas = target.Canvas;
		canvas.Clear(SKColors.White);
		if (gpuContext is not null)
		{
			_retainedLayer.Present(target);
		}
		else if (_canvasSurface is { } source)
		{
			source.Flush();
			canvas.DrawSurface(source, 0, 0);
		}
		canvas.Flush();
		_frame++;
		if (WaylandDiagnostics.IsEnabled)
		{
			var bounds = canvas.DeviceClipBounds;
			WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: present frame {_frame} buffer {bounds.Width}x{bounds.Height} {(gpuContext is null ? "wl_shm" : "EGL")}"));
			WaylandDiagnostics.DumpFrame(_id, _frame, target);
		}
	}

	private void RenderContent(GRContext? gpuContext)
	{
		if (gpuContext != _layerContext)
		{
			// A new context (recreated after loss, or the wl_shm fallback) cannot use the old layer: redraw fully.
			ReleaseRetainedLayer();
			_layerContext = gpuContext;
		}
		if (RootElement?.Visual.CompositionTarget is CompositionTarget compositionTarget)
		{
			var canvas = gpuContext is null ? _canvasSurface?.Canvas : _retainedLayer.Surface?.Canvas;
			compositionTarget.OnNativePlatformFrameRequested(canvas, size =>
			{
				var width = Math.Max((int)size.Width, 1);
				var height = Math.Max((int)size.Height, 1);
				if (gpuContext is not null)
				{
					return _retainedLayer.EnsureSurface(gpuContext, width, height, SKColors.White).Canvas;
				}
				_canvasSurface?.Dispose();
				_canvasSurface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
					?? throw new InvalidOperationException("Skia raster surface creation failed.");
				_canvasSurface.Canvas.Clear(SKColors.White);
				return _canvasSurface.Canvas;
			});
		}
	}

	private void ReleaseRetainedLayer()
	{
		_retainedLayer.Dispose();
		_canvasSurface?.Dispose();
		_canvasSurface = null;
		_layerContext = null;
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
			// Releases the wl_shm canvas; the GPU layer was released with the renderer's context.
			ReleaseRetainedLayer();
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
