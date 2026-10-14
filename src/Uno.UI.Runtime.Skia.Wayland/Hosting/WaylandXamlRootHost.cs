// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.UI.Composition;
using Uno.UI.Helpers;
using Uno.UI.Hosting;
using Uno.WinUI.Runtime.Skia.Linux;
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
	private SKSizeI _canvasSize;
	private SKPixelGeometry _outputGeometry;
	private double _outputScale;
	private readonly WaylandFrameReadiness _readiness = new();
	private readonly Func<long, Func<bool>, bool> _preparePresentation;

	// UI thread state.
	private CompositionTarget? _frameTarget;
	private bool _layoutRetryPosted;

	// Dispatch thread state.
	private WaylandCursor? _cursor;
	private WlPointer? _cursorPointer;

	internal WaylandXamlRootHost(WaylandConnection connection, int id, Window window, XamlRoot xamlRoot, WlSurface surface, WpViewport? viewport, Action<bool> keyboardFocusChanged, Func<long, Func<bool>, bool> preparePresentation, Func<long, bool> frameOpportunity, Action<bool> attemptActive, Action rollbackProtocolObjects)
	{
		_preparePresentation = preparePresentation;
		_connection = connection;
		_id = id;
		_window = window;
		_xamlRoot = xamlRoot;
		Surface = surface;
		try
		{
			_renderThread = new WaylandRenderThread($"Uno Wayland render {id}", OnRenderError);
			var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			_renderThread.Post(() =>
			{
				try
				{
					_renderer = new WaylandRenderer(connection.Display.NativeHandle, surface, connection.Shm, viewport, Draw, RenderContent, ReleaseRetainedLayer, _renderThread.Post, PrepareCommit, () => _readiness.RenderGeneration, frameOpportunity, attemptActive);
					_renderer.BackendChanged += (_, _) => OnBackendSelected();
					OnBackendSelected();
					initialized.TrySetResult();
				}
				catch (Exception error)
				{
					initialized.TrySetException(error);
				}
			});
			initialized.Task.GetAwaiter().GetResult();

			if (connection.Seat is { } wlSeat)
			{
				_input = new WaylandProtocolInputConnection(wlSeat, s => s == surface, connection.Display.Post, SetCursor);
				_input.PointerReleasing += (_, _) => DisposeCursor();
				_input.InputSerial += (_, serial) => connection.InputSerial = serial;
			}
			var input = (IWaylandInputConnection?)_input ?? NoInputConnection.Instance;
			KeyboardSource = new WaylandKeyboardInputSource(input, _connection.PostToUI, keyboardFocusChanged);
			lock (connection.Display.SyncRoot)
			{
				connection.TextInputReceived += OnTextInputReceived;
			}
			PointerSource = new WaylandPointerInputSource(input, _connection.PostToUI, () => KeyboardSource.Modifiers);
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
		catch
		{
			if (_renderThread is null)
			{
				try
				{
					lock (connection.Display.SyncRoot)
					{
						rollbackProtocolObjects();
					}
				}
				catch (Exception cleanupError)
				{
					connection.TrackWindowTeardown(Task.FromException(cleanupError));
				}
			}
			else
			{
				connection.TrackWindowTeardown(Completion);
				Dispose(rollbackProtocolObjects);
			}
			throw;
		}
	}

	internal WlSurface Surface { get; }
	internal WaylandPointerInputSource PointerSource { get; }
	internal WaylandProtocolInputConnection? Input => _input;
	internal WaylandKeyboardInputSource KeyboardSource { get; }
	// Written on the UI thread, read by the dispatch thread for cursor scaling.
	internal double Scale { get; set; } = 1;
	public UIElement? RootElement => _window.RootElement;

	public void InvalidateRender() => InvalidateRender(presentationReady: false);

	internal void PresentationReady() => InvalidateRender(presentationReady: true);

	// Dispatch thread publishes a conservative geometry for all entered outputs.
	internal void SetOutputGeometry(SKPixelGeometry geometry, double scale)
	{
		_renderThread.Post(() =>
		{
			if (_outputGeometry != geometry || _outputScale != scale)
			{
				_outputGeometry = geometry;
				_outputScale = scale;
				_renderer?.Invalidate();
			}
		});
	}

	private void InvalidateRender(bool presentationReady)
	{
		if (!_disposed)
		{
			_renderThread.Post(() =>
			{
				if (_renderer is { IsConfigured: true } renderer)
				{
					if (presentationReady)
					{
						renderer.PresentationReady();
					}
					else
					{
						renderer.Invalidate();
					}
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

	internal void BeginConfigureUI(long generation) => _readiness.BeginConfigureUI(generation);

	// UI thread: the current configure still waits for a frame recorded at its bounds.
	internal bool IsLayoutPending => _readiness.IsLayoutPending;

	// UI thread, after that frame was recorded (or the wait gave up).
	internal Action? LayoutRecorded { get; set; }

	// UI thread, after applying bounds and scale; Uno records their layout in a later frame.
	internal void ConfigureUI(long generation, bool boundsChanged)
	{
		if (RootElement?.Visual.CompositionTarget is not CompositionTarget compositionTarget)
		{
			// Nothing was recorded yet, so no older layout can be presented.
			_readiness.ConfigureUI(generation, boundsChanged: false);
			return;
		}
		if (_frameTarget is null)
		{
			_frameTarget = compositionTarget;
			compositionTarget.FrameRendered += OnFrameRendered;
		}
		var waits = _readiness.ConfigureUI(generation, boundsChanged);
		if (WaylandDiagnostics.IsEnabled)
		{
			WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: UI configure generation {generation}{(waits ? " waits for layout" : "")}"));
		}
		if (waits)
		{
			// Records a frame even when layout leaves the visuals unchanged.
			((ICompositionTarget)compositionTarget).RequestNewFrame();
		}
	}

	// UI thread.
	private void OnFrameRendered()
	{
		var bounds = _xamlRoot.Bounds;
		// The recorded picture reflects the new bounds only if no layout pass was still pending.
		var arranged = SkiaRenderHelper.CanRecordPicture(RootElement)
			&& Math.Abs(RootElement.RenderSize.Width - bounds.Width) * Scale <= 1
			&& Math.Abs(RootElement.RenderSize.Height - bounds.Height) * Scale <= 1;
		if (_readiness.OnFrameRecorded(arranged))
		{
			if (WaylandDiagnostics.IsEnabled)
			{
				WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: layout recorded at {bounds.Width}x{bounds.Height}{(arranged ? "" : " (gave up waiting)")}"));
			}
			LayoutRecorded?.Invoke();
		}
		if (_readiness.IsLayoutPending && RootElement?.Visual.CompositionTarget is CompositionTarget target)
		{
			if (!_layoutRetryPosted)
			{
				_layoutRetryPosted = true;
				_ = RetryLayoutFrame(target);
			}
		}
		// A later picture can fix a canvas-size rejection even after this generation's layout became ready.
		if (!_disposed)
		{
			_renderThread.Post(() => _renderer?.ContentReady());
		}
	}

	private async Task RetryLayoutFrame(CompositionTarget target)
	{
		// A rejected native draw has no frame callback. Retry the UI recording at the liveness
		// interval instead of creating an immediate record/reject loop.
		await Task.Delay(WaylandFrameScheduler.StallInterval).ConfigureAwait(false);
		_connection.PostToUI(() =>
		{
			_layoutRetryPosted = false;
			if (!_disposed && _readiness.IsLayoutPending)
			{
				((ICompositionTarget)target).RequestNewFrame();
				PresentationReady();
			}
		});
	}

	internal void Configure(int logicalWidth, int logicalHeight, uint preferredScale, int integerScale, long generation)
	{
		_renderThread.Post(() =>
		{
			if (_renderer is not { } renderer)
			{
				return;
			}
			_readiness.ConfigureRender(generation);
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
		if (WaylandDiagnostics.IsEnabled)
		{
			WaylandDiagnostics.Trace($"window{_id}: renderer {(renderer.IsSoftware ? "wl_shm" : "EGL")}{(renderer.EglInitializationError is { } eglError ? $" (EGL unavailable: {eglError.Message})" : "")}");
		}
	}

	// Returns false to keep the previous buffer while the content still has the size of an older
	// configure or scale, so a frame rendered for stale geometry is never presented.
	private bool Draw(SKSurface target, GRContext? gpuContext)
	{
		_readiness.BeginFrame();
		if (!_readiness.IsGenerationCurrent())
		{
			// The UI is still laying out a newer configure; replaying its last picture would be wasted,
			// but Uno schedules its next UI frame from this request, so that frame must still be released.
			if (RootElement?.Visual.CompositionTarget is CompositionTarget skippedTarget)
			{
				skippedTarget.OnNativePlatformFrameSkipped();
			}
			if (WaylandDiagnostics.IsEnabled)
			{
				WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: skip stale generation {_readiness.RenderGeneration} before drawing"));
			}
			return false;
		}
		RenderContent(gpuContext);
		var canvas = target.Canvas;
		var bounds = canvas.DeviceClipBounds;
		var content = _canvasSize;
		if (!_readiness.CanPresent(content.Width, content.Height, bounds.Width, bounds.Height))
		{
			if (WaylandDiagnostics.IsEnabled)
			{
				WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: skip stale content {content.Width}x{content.Height} generation {_readiness.RenderGeneration} for buffer {bounds.Width}x{bounds.Height}"));
			}
			return false;
		}
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
			WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: draw frame {_frame} buffer {bounds.Width}x{bounds.Height} content {content.Width}x{content.Height} {(gpuContext is null ? "wl_shm" : "EGL")}"));
			WaylandDiagnostics.DumpFrame(_id, _frame, target);
		}
		return true;
	}

	private bool PrepareCommit(Func<bool> submit)
		=> _connection.Display.SubmitPresentation(() => _preparePresentation(_readiness.RenderGeneration, () =>
		{
			_renderer!.ApplyScale();
			return submit();
		}));

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
			(int Width, int Height)? bufferSize = _renderer is { IsConfigured: true } renderer ? renderer.BufferSize : null;
			if (bufferSize is { } configured && (_canvasSize.Width != configured.Width || _canvasSize.Height != configured.Height))
			{
				canvas = null;
			}
			var surfaceGeometry = LinuxTextRenderingExtension.Instance.Settings.GetSurfaceGeometry(_renderer!.Scale.EffectiveScale,
				pixelPreserving: _outputGeometry != SKPixelGeometry.Unknown && _outputScale == _renderer.Scale.EffectiveScale);
			compositionTarget.OnNativePlatformFrameRequested(canvas, (size, pixelGeometry) =>
			{
				// CompositionTarget rounds current XamlRoot bounds to even. The retained layer and
				// wl_buffer instead share the configure's integer geometry and halves-up rounding.
				var (width, height) = WaylandRenderScale.GetCanvasSize(bufferSize, size.Width, size.Height);
				_canvasSize = new SKSizeI(width, height);
				if (gpuContext is not null)
				{
					return _retainedLayer.EnsureSurface(gpuContext, width, height, SKColors.White, pixelGeometry).Canvas;
				}
				_canvasSurface?.Dispose();
				using var properties = new SKSurfaceProperties(pixelGeometry);
				_canvasSurface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), properties)
					?? throw new InvalidOperationException("Skia raster surface creation failed.");
				_canvasSurface.Canvas.Clear(SKColors.White);
				return _canvasSurface.Canvas;
			}, surfaceGeometry);
		}
	}

	private void ReleaseRetainedLayer()
	{
		_retainedLayer.Dispose();
		_canvasSurface?.Dispose();
		_canvasSurface = null;
		_canvasSize = SKSizeI.Empty;
		_layerContext = null;
	}

	private void OnRenderError(Exception error)
	{
		if (_connection.Display.DispatchError is null && this.Log().IsEnabled(LogLevel.Error))
		{
			this.Log().Error($"Wayland rendering failed for window {_id}.", error);
		}
		if (_connection.Display.DispatchError is null && WaylandDiagnostics.IsEnabled)
		{
			WaylandDiagnostics.Trace($"window{_id}: render error {error}");
		}
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

	// Completes once Dispose has released the renderer and destroyed the protocol objects.
	internal Task Completion => _renderThread.Completion;

	// UI thread. Protocol objects are destroyed after the renderer has released its buffers.
	internal void Dispose(Action destroyProtocolObjects)
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		var display = _connection.Display;
		Exception? inputError = null;
		try
		{
			WaylandCleanup.Run(
				() =>
				{
					if (_frameTarget is not null)
					{
						_frameTarget.FrameRendered -= OnFrameRendered;
						_frameTarget = null;
					}
				},
				() => XamlRootMap.Unregister(_xamlRoot),
				() =>
				{
					lock (display.SyncRoot)
					{
						_connection.TextInputReceived -= OnTextInputReceived;
						WaylandCleanup.Run(
							() => _seat?.Dispose(),
							() =>
							{
								if (_seat is null)
								{
									KeyboardSource?.Dispose();
								}
							},
							DisposeCursor);
					}
				});
		}
		catch (Exception error)
		{
			inputError = error;
		}
		_renderThread.Complete(() =>
		{
			try
			{
				WaylandCleanup.Run(
					() => _renderer?.Dispose(),
					ReleaseRetainedLayer);
				_renderer = null;
			}
			finally
			{
				var destroyed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				display.TryPost(() =>
				{
					try
					{
						destroyProtocolObjects();
						destroyed.TrySetResult();
					}
					catch (Exception error)
					{
						destroyed.TrySetException(error);
					}
				}, () => destroyed.TrySetException(new InvalidOperationException("Wayland dispatch stopped before window destruction.")));
				WaylandCleanup.Run(
					() => destroyed.Task.GetAwaiter().GetResult(),
					() =>
					{
						if (inputError is not null)
						{
							throw inputError;
						}
					});
			}
		});
		_renderThread.Dispose();
	}

	void IDisposable.Dispose() => Dispose(() => { });

	// Dispatch thread: input method text ends a pending keyboard Compose sequence.
	private void OnTextInputReceived(object? sender, EventArgs e) => KeyboardSource.ResetCompose();

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
