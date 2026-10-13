// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SkiaSharp;
using Uno.Disposables;
using Uno.Foundation.Logging;
using Uno.UI.Xaml.Controls;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;
using Uno.WinUI.Runtime.Skia.Wayland.Rendering;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// One wl_surface + xdg_surface + xdg_toplevel per Uno Window, sharing the host connection.
// Window state follows xdg-shell: the compositor's configure is authoritative for size, maximized,
// fullscreen and activation; requests made here are hints that come back through configure.
internal sealed class WaylandWindowWrapper : NativeWindowWrapperBase, IWaylandDecorationsOwner
{
	private const WaylandToplevelStates ConstrainedStates = WaylandToplevelStates.Maximized | WaylandToplevelStates.Fullscreen | WaylandToplevelStates.Tiled;
	private static readonly bool _forceClientDecorations = Environment.GetEnvironmentVariable("UNO_WAYLAND_FORCE_CSD") == "1";
	private static int _nextId;

	private readonly WaylandConnection _connection;
	private readonly WaylandXamlRootHost _host;
	private readonly int _id;
	private readonly WlSurface _surface;
	private readonly XdgSurface _xdgSurface;
	private readonly XdgToplevel _toplevel;
	private readonly WpViewport? _viewport;
	private readonly WpFractionalScaleV1? _fractionalScale;
	private readonly ZxdgToplevelDecorationV1? _decoration;
	private readonly bool _darkDecorations;
	private string _title = string.Empty;
	private volatile bool _closed;
	private WaylandDisplayInformationExtension? _displayInformation;

	// UI thread state.
	private WaylandToplevelStates _uiStates;
	private bool _minimizeRequested;

	// Dispatch thread state.
	private int _pendingWidth;
	private int _pendingHeight;
	private WaylandToplevelStates _pendingStates;
	private WaylandToplevelStates _states;
	private WaylandWindowCapabilities _capabilities = WaylandWindowCapabilities.All;
	private uint _pendingDecorationMode;
	private uint _requestedDecorationMode;
	private bool _serverSideDecorations;
	private WaylandClientDecorations? _clientDecorations;
	private WaylandDecorationLayout _layout;
	private string _dispatchTitle = string.Empty;
	private bool _hasTitleBar = true;
	private bool _extendsContent;
	private bool _isResizable = true;
	private bool _isMaximizable = true;
	private bool _isMinimizable = true;
	private int? _minWidthPixels;
	private int? _minHeightPixels;
	private int? _maxWidthPixels;
	private int? _maxHeightPixels;
	private (int Width, int Height) _sentMinSize;
	private (int Width, int Height) _sentMaxSize;
	private int _geometryWidth;
	private int _geometryHeight;
	private int _floatingWidth;
	private int _floatingHeight;
	private uint _preferredScale;
	private int _integerScale = 1;
	private bool _configured;
	// Most recently entered last; the last one is the window's current output.
	private readonly List<WaylandOutput> _enteredOutputs = new();
	private volatile WaylandOutputInfo? _outputInfo;

	internal WaylandWindowWrapper(WaylandConnection connection, Window window, XamlRoot root)
		: base(window, root)
	{
		_connection = connection;
		_id = Interlocked.Increment(ref _nextId);
		OwnerWindow = window;
		_darkDecorations = Application.Current?.RequestedTheme == ApplicationTheme.Dark;
		var package = Windows.ApplicationModel.Package.Current;
		var appId = connection.AppId ??= package.Id.Name is { Length: > 0 } name ? name : "uno.app";
		var titleBar = window.AppWindow.TitleBar;
		_extendsContent = titleBar.ExtendsContentIntoTitleBar;
		var display = connection.Display;
		// Create and subscribe inside one SyncRoot section so no event is dispatched before its handler exists.
		lock (display.SyncRoot)
		{
			_surface = connection.Compositor.CreateSurface();
			_surface.PreferredBufferScale += OnPreferredBufferScale;
			_surface.Enter += OnSurfaceEnter;
			_surface.Leave += OnSurfaceLeave;
			connection.OutputRemoved += OnOutputRemoved;
			_xdgSurface = connection.WmBase.GetXdgSurface(_surface);
			_xdgSurface.Configure += OnXdgSurfaceConfigure;
			_toplevel = _xdgSurface.GetToplevel();
			_toplevel.Configure += OnToplevelConfigure;
			_toplevel.Close += OnToplevelClose;
			_toplevel.WmCapabilities += OnWmCapabilities;
			_toplevel.SetAppId(appId);
			if (connection.Viewporter is { } viewporter)
			{
				_viewport = viewporter.GetViewport(_surface);
				if (connection.FractionalScale is { } fractional)
				{
					_fractionalScale = fractional.GetFractionalScale(_surface);
					_fractionalScale.PreferredScale += OnPreferredFractionalScale;
				}
			}
			if (connection.Decorations is { } decorations)
			{
				_decoration = decorations.GetToplevelDecoration(_toplevel);
				_decoration.Configure += OnDecorationConfigure;
				_ = RequestDecorationMode();
			}
		}
		// Activation follows the configure activated state rather than keyboard focus.
		_host = new WaylandXamlRootHost(connection, _id, window, root, _surface, _viewport, static _ => { });
		connection.AddWindow(this);
		RasterizationScale = 1;
		titleBar.ExtendsContentIntoTitleBarChanged += ExtendContentIntoTitleBar;
		CoreApplication.GetCurrentView().TitleBar.ExtendViewIntoTitleBarChanged += OnCoreTitleBarChanged;
		// Publish a provisional size so content can load and trigger Show; configure replaces it.
		// Posted because Uno subscribes to SizeChanged only after the factory returns this wrapper.
		WaylandConnection.PostToUI(() =>
		{
			if (!_configured)
			{
				ApplyConfigure(InitialWidth, InitialHeight, 0, 1, WaylandToplevelStates.None);
			}
		});
		UpdateWindowPropertiesFromPackage(package, window);
		WaylandDiagnostics.Trace($"window{_id}: created (app_id={appId}, viewport={_viewport is not null}, fractional={_fractionalScale is not null}, decoration={_decoration is not null}, subcompositor={connection.Subcompositor is not null}, seat={connection.Seat is not null})");
	}

	internal Window OwnerWindow { get; }
	internal WaylandXamlRootHost Host => _host;

	public override object NativeWindow => _surface;

	public override string Title
	{
		get => _title;
		set
		{
			_title = value;
			Post(() =>
			{
				_dispatchTitle = value;
				_toplevel.SetTitle(value);
				_clientDecorations?.SetTitle(value);
			});
		}
	}

	internal double Scale => RasterizationScale > 0 ? RasterizationScale : 1;

	// UI thread. Minimized is not reported by xdg-shell: it is assumed from the request until the next activation.
	internal OverlappedPresenterState PresenterState
		=> _minimizeRequested ? OverlappedPresenterState.Minimized
			: (_uiStates & WaylandToplevelStates.Maximized) != 0 ? OverlappedPresenterState.Maximized
			: OverlappedPresenterState.Restored;

	internal void SetDisplayInformationExtension(WaylandDisplayInformationExtension extension) => _displayInformation = extension;

	// Like the X11 host: package display name as title, package logo as icon.
	private void UpdateWindowPropertiesFromPackage(Windows.ApplicationModel.Package package, Window window)
	{
		if (!string.IsNullOrEmpty(package.DisplayName))
		{
			ApplicationView.GetForWindowId(window.AppWindow.Id).Title = package.DisplayName;
		}
		if (_connection.IconManager is not null && WaylandWindowIcon.ResolvePackageLogo() is { } iconPath)
		{
			SetIcon(iconPath);
		}
	}

	public override void SetIcon(string iconPath)
	{
		if (_connection.IconManager is null)
		{
			return;
		}
		_ = Task.Run(() =>
		{
			SKBitmap? bitmap = null;
			try
			{
				bitmap = WaylandWindowIcon.Decode(iconPath);
			}
			catch (Exception error)
			{
				if (this.Log().IsEnabled(LogLevel.Error))
				{
					this.Log().Error($"Failed to load the window icon from [{iconPath}].", error);
				}
			}
			if (bitmap is not null)
			{
				_connection.Display.Post(() =>
				{
					using (bitmap)
					{
						if (!_closed)
						{
							WaylandWindowIcon.Apply(_connection, _toplevel, bitmap, _id);
						}
					}
				});
			}
		});
	}

	protected override void ShowCore()
	{
		Post(() =>
		{
			// The initial commit without a buffer requests the first configure; rendering waits for its ack.
			_surface.Commit();
			WaylandDiagnostics.Trace($"window{_id}: initial commit");
		});
	}

	internal protected override void Activate()
		=> Post(() =>
		{
			if ((_states & WaylandToplevelStates.Activated) == 0)
			{
				WaylandDiagnostics.Trace($"window{_id}: activation requested");
				_connection.Activate(_surface, () => !_closed);
			}
		});

	public override void Resize(SizeInt32 size)
	{
		if (size.Width <= 0 || size.Height <= 0)
		{
			return;
		}
		var scale = Scale;
		var width = (int)Math.Round(size.Width / scale);
		var height = (int)Math.Round(size.Height / scale);
		Post(() =>
		{
			// The size includes the client-side title bar, like the outer window size on other hosts.
			_floatingWidth = width;
			_floatingHeight = height;
			// Maximized, fullscreen and tiled sizes are compositor constraints; floating sizes are hints.
			if (_configured && (_states & ConstrainedStates) == 0)
			{
				_geometryWidth = width;
				_geometryHeight = height;
				ApplyGeometry();
				Publish();
			}
		});
	}

	public override void ExtendContentIntoTitleBar(bool extend)
	{
		base.ExtendContentIntoTitleBar(extend);
		Post(() =>
		{
			_extendsContent = extend;
			RefreshDecorations();
		});
	}

	private void OnCoreTitleBarChanged() => ExtendContentIntoTitleBar(CoreApplication.GetCurrentView().TitleBar.ExtendViewIntoTitleBar);

	protected override IDisposable ApplyOverlappedPresenter(OverlappedPresenter presenter)
	{
		presenter.SetNative(new WaylandNativeOverlappedPresenter(this));
		return Disposable.Create(() => presenter.SetNative(null));
	}

	protected override IDisposable ApplyFullScreenPresenter()
	{
		Post(() => _toplevel.SetFullscreen(null));
		return Disposable.Create(() => Post(() => _toplevel.UnsetFullscreen()));
	}

	// Overlapped presenter, UI thread.
	internal void Maximize() => Post(() => _toplevel.SetMaximized());

	internal void Minimize()
	{
		_minimizeRequested = true;
		Post(() => _toplevel.SetMinimized());
	}

	internal void Restore(bool activateWindow)
	{
		Post(() =>
		{
			if ((_states & WaylandToplevelStates.Maximized) != 0)
			{
				_toplevel.UnsetMaximized();
			}
		});
		// A minimized window can only come back through activation.
		if (activateWindow || _minimizeRequested)
		{
			Activate();
		}
	}

	internal void SetIsResizable(bool value) => Post(() =>
	{
		_isResizable = value;
		RefreshDecorations();
	});

	internal void SetIsMaximizable(bool value) => Post(() =>
	{
		_isMaximizable = value;
		RefreshDecorations();
	});

	internal void SetIsMinimizable(bool value) => Post(() =>
	{
		_isMinimizable = value;
		RefreshDecorations();
	});

	internal void SetHasTitleBar(bool value) => Post(() =>
	{
		_hasTitleBar = value;
		RefreshDecorations();
	});

	// Physical pixels like the other hosts; xdg_toplevel sizes are logical window geometry sizes.
	internal void SetSizeConstraints(int? minWidth, int? minHeight, int? maxWidth, int? maxHeight) => Post(() =>
	{
		_minWidthPixels = minWidth;
		_minHeightPixels = minHeight;
		_maxWidthPixels = maxWidth;
		_maxHeightPixels = maxHeight;
		ApplySizeConstraints();
		_host.InvalidateRender();
	});

	// Decorations owner, dispatch thread.
	void IWaylandDecorationsOwner.RequestParentCommit() => _host.InvalidateRender();

	void IWaylandDecorationsOwner.ToggleMaximized()
	{
		if ((_states & WaylandToplevelStates.Maximized) != 0)
		{
			_toplevel.UnsetMaximized();
		}
		else
		{
			_toplevel.SetMaximized();
		}
	}

	void IWaylandDecorationsOwner.Minimize() => WaylandConnection.PostToUI(Minimize);

	void IWaylandDecorationsOwner.RequestClose() => WaylandConnection.PostToUI(RequestClose);

	// Runs on the dispatch thread unless the window is closed.
	private void Post(Action action) => _connection.Display.Post(() =>
	{
		if (!_closed)
		{
			action();
		}
	});

	// The output the window was last seen entering, or the first advertised one before it was mapped.
	internal WaylandOutputInfo? OutputInfo => _outputInfo ?? _connection.DefaultOutputInfo;

	// Dispatch thread handlers: copy state, acknowledge, then post to the UI and render owners.
	private void OnToplevelConfigure(object? sender, XdgToplevel.ConfigureEventArgs args)
	{
		_pendingWidth = args.Width;
		_pendingHeight = args.Height;
		_pendingStates = WaylandToplevelStateParser.ParseStates(args.States);
	}

	private void OnWmCapabilities(object? sender, XdgToplevel.WmCapabilitiesEventArgs args)
		=> _capabilities = WaylandToplevelStateParser.ParseCapabilities(args.Capabilities);

	private void OnDecorationConfigure(object? sender, ZxdgToplevelDecorationV1.ConfigureEventArgs args)
		=> _pendingDecorationMode = args.Mode;

	private void OnXdgSurfaceConfigure(object? sender, XdgSurface.ConfigureEventArgs args)
	{
		if (_closed)
		{
			return;
		}
		_states = _pendingStates;
		ApplyDecorationMode();
		if (_floatingWidth <= 0 || _floatingHeight <= 0)
		{
			_floatingWidth = InitialWidth;
			_floatingHeight = InitialHeight + TitleBarHeight;
		}
		// Zero means the client picks; restore the last floating size, e.g. after unmaximize.
		_geometryWidth = _pendingWidth > 0 ? _pendingWidth : _floatingWidth;
		_geometryHeight = _pendingHeight > 0 ? _pendingHeight : _floatingHeight;
		if ((_states & ConstrainedStates) == 0)
		{
			_floatingWidth = _geometryWidth;
			_floatingHeight = _geometryHeight;
		}
		ApplyGeometry();
		_xdgSurface.AckConfigure(args.Serial);
		_configured = true;
		var activated = (_states & WaylandToplevelStates.Activated) != 0;
		if (activated)
		{
			_connection.ActiveSurface = _surface;
		}
		else if (_connection.ActiveSurface == _surface)
		{
			_connection.ActiveSurface = null;
		}
		WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: configure serial={args.Serial} suggested={_pendingWidth}x{_pendingHeight} geometry={_geometryWidth}x{_geometryHeight} content={_layout.ContentWidth}x{_layout.ContentHeight} states={_states} decorations={(_serverSideDecorations ? "server" : "client")} -> ack"));
		WaylandConnection.PostToUI(() => ActivationState = activated ? CoreWindowActivationState.PointerActivated : CoreWindowActivationState.Deactivated);
		Publish();
	}

	private void OnPreferredFractionalScale(object? sender, WpFractionalScaleV1.PreferredScaleEventArgs args)
	{
		_preferredScale = args.Scale;
		WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: preferred fractional scale {args.Scale}/120"));
		OnScaleChanged();
	}

	private void OnPreferredBufferScale(object? sender, WlSurface.PreferredBufferScaleEventArgs args)
	{
		_integerScale = Math.Max(args.Factor, 1);
		WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: preferred buffer scale {args.Factor}"));
		OnScaleChanged();
	}

	private void OnScaleChanged()
	{
		if (_configured && !_closed)
		{
			ApplyGeometry();
			Publish();
		}
	}

	private void OnSurfaceEnter(object? sender, WlSurface.EnterEventArgs args)
	{
		if (_connection.FindOutput(args.Output) is not { } output)
		{
			return;
		}
		if (!_enteredOutputs.Remove(output))
		{
			output.Changed += OnEnteredOutputChanged;
		}
		_enteredOutputs.Add(output);
		WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: entered output {output.Info.Name}"));
		OnOutputsChanged();
	}

	private void OnSurfaceLeave(object? sender, WlSurface.LeaveEventArgs args)
	{
		if (_connection.FindOutput(args.Output) is { } output)
		{
			LeaveOutput(output);
		}
	}

	private void OnOutputRemoved(object? sender, WaylandOutput output) => LeaveOutput(output);

	private void LeaveOutput(WaylandOutput output)
	{
		output.Changed -= OnEnteredOutputChanged;
		if (_enteredOutputs.Remove(output))
		{
			WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: left output {output.Info.Name}"));
			OnOutputsChanged();
		}
	}

	private void OnEnteredOutputChanged(object? sender, EventArgs args) => OnOutputsChanged();

	private void OnOutputsChanged()
	{
		if (_closed)
		{
			return;
		}
		// Keep the last output while the window is on none (e.g. hidden), like the integer scale below.
		var current = _enteredOutputs.Count > 0 ? _enteredOutputs[^1].Info : _outputInfo;
		if (current != _outputInfo)
		{
			_outputInfo = current;
			WaylandConnection.PostToUI(() => _displayInformation?.NotifyDpiChanged());
		}
		// Surfaces before v6 get no preferred_buffer_scale: derive it from the entered outputs instead.
		if (_surface.Version < 6)
		{
			var infos = new List<WaylandOutputInfo>(_enteredOutputs.Count);
			foreach (var output in _enteredOutputs)
			{
				infos.Add(output.Info);
			}
			var scale = WaylandOutput.GetIntegerScale(infos, _integerScale);
			if (scale != _integerScale)
			{
				_integerScale = scale;
				OnScaleChanged();
			}
		}
	}

	private void OnToplevelClose(object? sender, XdgToplevel.CloseEventArgs args)
	{
		WaylandDiagnostics.Trace($"window{_id}: close requested");
		WaylandConnection.PostToUI(RequestClose);
	}

	private double EffectiveScale => WaylandRenderScale.Compute(_viewport is not null, _preferredScale, _integerScale);

	private bool WantsTitleBar => _hasTitleBar && !_extendsContent;

	private int TitleBarHeight
		=> !_serverSideDecorations && _clientDecorations is not null && WantsTitleBar && (_states & WaylandToplevelStates.Fullscreen) == 0
			? WaylandDecorationLayout.DefaultTitleBarHeight
			: 0;

	// Ask for server-side decorations only when a title bar is wanted; otherwise the compositor's would show one.
	// Returns whether a new mode was requested; the compositor answers with a configure sequence.
	private bool RequestDecorationMode()
	{
		var mode = !_forceClientDecorations && WantsTitleBar
			? (uint)ZxdgToplevelDecorationV1.ModeValues.ServerSide
			: (uint)ZxdgToplevelDecorationV1.ModeValues.ClientSide;
		if (_decoration is not null && mode != _requestedDecorationMode)
		{
			_requestedDecorationMode = mode;
			_decoration.SetMode(mode);
			WaylandDiagnostics.Trace($"window{_id}: request {(mode == (uint)ZxdgToplevelDecorationV1.ModeValues.ServerSide ? "server" : "client")}-side decorations");
			return true;
		}
		return false;
	}

	// At xdg_surface.configure: the negotiated mode becomes effective. Without xdg-decoration it is client-side.
	private void ApplyDecorationMode()
	{
		_serverSideDecorations = _decoration is not null && _pendingDecorationMode == (uint)ZxdgToplevelDecorationV1.ModeValues.ServerSide;
		if (!_serverSideDecorations && _clientDecorations is null)
		{
			if (_connection.Subcompositor is null)
			{
				if (this.Log().IsEnabled(LogLevel.Warning))
				{
					this.Log().Warn("The compositor uses client-side decorations but provides no wl_subcompositor; the window has no title bar.");
				}
				return;
			}
			_clientDecorations = new WaylandClientDecorations(_connection, _surface, _toplevel, _id, _darkDecorations, this);
		}
		else if (_serverSideDecorations && _clientDecorations is { } decorations)
		{
			_clientDecorations = null;
			decorations.Dispose();
		}
	}

	// Dispatch thread, after a decoration preference changed.
	private void RefreshDecorations()
	{
		// A mode change is applied at its configure, avoiding an intermediate layout with the old mode.
		if (!RequestDecorationMode() && _configured)
		{
			ApplyGeometry();
			Publish();
		}
	}

	// Splits the window geometry into title bar and content, then updates geometry, decorations and constraints.
	private void ApplyGeometry()
	{
		var titleBarHeight = TitleBarHeight;
		var contentWidth = Math.Max(_geometryWidth, 1);
		var contentHeight = Math.Max(_geometryHeight - titleBarHeight, 1);
		var buttons = WaylandCaptionButtons.Close;
		if (_isMaximizable && _isResizable && (_capabilities & WaylandWindowCapabilities.Maximize) != 0)
		{
			buttons |= WaylandCaptionButtons.Maximize;
		}
		if (_isMinimizable && (_capabilities & WaylandWindowCapabilities.Minimize) != 0)
		{
			buttons |= WaylandCaptionButtons.Minimize;
		}
		var resizeMargin = _clientDecorations is not null && !_serverSideDecorations && _isResizable && (_states & ConstrainedStates) == 0
			? WaylandDecorationLayout.DefaultResizeMargin
			: 0;
		_layout = new WaylandDecorationLayout(contentWidth, contentHeight, titleBarHeight, resizeMargin, buttons);
		_xdgSurface.SetWindowGeometry(0, _layout.GeometryY, contentWidth, _layout.GeometryHeight);
		if (!_serverSideDecorations)
		{
			_clientDecorations?.Update(_layout, _dispatchTitle, (_states & WaylandToplevelStates.Activated) != 0, (_states & WaylandToplevelStates.Maximized) != 0, _preferredScale, _integerScale);
		}
		ApplySizeConstraints();
	}

	// Takes effect with the next commit. A non-resizable window is pinned to its current geometry.
	private void ApplySizeConstraints()
	{
		var scale = EffectiveScale;
		static int ToLogical(int? pixels, double scale) => pixels is > 0 and < int.MaxValue ? (int)Math.Ceiling(pixels.Value / scale) : 0;
		var min = _isResizable ? (ToLogical(_minWidthPixels, scale), ToLogical(_minHeightPixels, scale)) : (_geometryWidth, _geometryHeight);
		var max = _isResizable ? (ToLogical(_maxWidthPixels, scale), ToLogical(_maxHeightPixels, scale)) : (_geometryWidth, _geometryHeight);
		if (min == _sentMinSize && max == _sentMaxSize)
		{
			return;
		}
		_sentMinSize = min;
		_sentMaxSize = max;
		_toplevel.SetMinSize(min.Item1, min.Item2);
		_toplevel.SetMaxSize(max.Item1, max.Item2);
		WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: size constraints min={min.Item1}x{min.Item2} max={max.Item1}x{max.Item2}"));
	}

	private void Publish()
	{
		var width = _layout.ContentWidth;
		var height = _layout.ContentHeight;
		var titleBarHeight = _layout.TitleBarHeight;
		var states = _states;
		var scale = EffectiveScale;
		WaylandConnection.PostToUI(() => ApplyConfigure(width, height, titleBarHeight, scale, states));
		_host.Configure(width, height, _preferredScale, _integerScale);
	}

	// UI thread.
	private void ApplyConfigure(int width, int height, int titleBarHeight, double scale, WaylandToplevelStates states)
	{
		if (_closed)
		{
			return;
		}
		_uiStates = states;
		if ((states & WaylandToplevelStates.Activated) != 0)
		{
			_minimizeRequested = false;
		}
		var scaleChanged = RasterizationScale != (float)scale;
		RasterizationScale = (float)scale;
		_host.Scale = scale;
		var client = new SizeInt32(WaylandRenderScale.ToBufferPixels(width, scale), WaylandRenderScale.ToBufferPixels(height, scale));
		var outer = new SizeInt32(client.Width, WaylandRenderScale.ToBufferPixels(height + titleBarHeight, scale));
		SetSizes(outer, client);
		var bounds = new Rect(0, 0, width, height);
		SetBoundsAndVisibleBounds(bounds, bounds);
		if (scaleChanged)
		{
			_displayInformation?.NotifyDpiChanged();
		}
		_host.InvalidateRender();
	}

	private void RequestClose()
	{
		if (!_closed && !RaiseClosing().Cancel)
		{
			Close();
		}
	}

	protected override void CloseCore()
	{
		if (_closed)
		{
			return;
		}
		_closed = true;
		WaylandDiagnostics.Trace($"window{_id}: closing");
		if (Window is { } window)
		{
			window.AppWindow.TitleBar.ExtendsContentIntoTitleBarChanged -= ExtendContentIntoTitleBar;
		}
		CoreApplication.GetCurrentView().TitleBar.ExtendViewIntoTitleBarChanged -= OnCoreTitleBarChanged;
		_connection.RemoveWindow(this);
		_host.Dispose(() =>
		{
			_connection.OutputRemoved -= OnOutputRemoved;
			foreach (var output in _enteredOutputs)
			{
				output.Changed -= OnEnteredOutputChanged;
			}
			_enteredOutputs.Clear();
			if (_connection.ActiveSurface == _surface)
			{
				_connection.ActiveSurface = null;
			}
			_clientDecorations?.Dispose();
			_decoration?.Dispose();
			_fractionalScale?.Dispose();
			_viewport?.Dispose();
			_toplevel.Dispose();
			_xdgSurface.Dispose();
			_surface.Dispose();
			WaylandDiagnostics.Trace($"window{_id}: destroyed");
		});
	}
}
