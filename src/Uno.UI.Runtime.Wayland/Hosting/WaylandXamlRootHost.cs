#nullable enable

using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Uno.Foundation.Logging;
using Uno.UI.Composition.Drawing;
using Uno.UI.Hosting;
using Uno.UI.Runtime.Wayland.Input;
using Uno.UI.Runtime.Wayland.Protocols;
using Uno.UI.Runtime.Wayland.Rendering;
using Windows.UI.Core;

namespace Uno.UI.Runtime.Wayland;

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

		// The host only turns a negotiated context kind into a context for this surface; the backend is chosen by
		// GraphicsRegistry, honouring the kinds the host builder disabled.
		GraphicsRegistry.ContextFactory = kind => Task.FromResult<ISwapChain?>(CreateContext(kind, connection, surface));
		var init = GraphicsRegistry.Initialize();
		Microsoft.UI.Composition.Compositor.GetSharedCompositor().IsSoftwareRenderer = init.Context.Kind == GraphicsContextKind.Software;
		WaylandDiagnostics.Trace($"window{id}: graphics context {init.Context.Kind}");
		var scale = new WaylandRenderScale(surface, viewport);
		_renderThread.Post(() => _renderer = new WaylandRenderer(this, scale, (IWaylandGraphicsContext)init.Context, init.Renderer, surface, _renderThread.Post));

		if (connection.Seat is { } wlSeat)
		{
			_input = new WaylandProtocolInputConnection(wlSeat, s => s == surface, connection.Display.Post, SetCursor);
			_input.PointerReleasing += (_, _) => DisposeCursor();
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

	public Windows.UI.Color? BackgroundColor
	{
		get
		{
			switch (_window.Background)
			{
				case SolidColorBrush brush:
					return brush.Color;
				case not null:
					this.LogError()?.Error("This platform only supports SolidColorBrush for the Window background");
					return null;
				default:
					return null;
			}
		}
	}

	public void InvalidateRender()
	{
		if (!_disposed)
		{
			_renderThread.Post(() => _renderer?.Invalidate());
		}
	}

	private IWaylandGraphicsContext? CreateContext(GraphicsContextKind kind, WaylandConnection connection, WlSurface surface)
	{
		bool PrepareCommit() => _renderer?.PrepareCommit() ?? false;
		switch (kind)
		{
			case GraphicsContextKind.OpenGLES:
				try
				{
					return new WaylandEglGraphicsContext(connection.Display.NativeHandle, surface, PrepareCommit);
				}
				catch (Exception error) when (error is InvalidOperationException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
				{
					if (this.Log().IsEnabled(LogLevel.Information))
					{
						this.Log().Info($"EGL OpenGL ES context creation failed ({error.Message}); falling through.");
					}
					return null;
				}

			case GraphicsContextKind.Software:
				return new WaylandSoftwareGraphicsContext(surface, connection.Shm, _renderThread.Post, PrepareCommit);

			default:
				return null;
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
