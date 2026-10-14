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
using Uno.WinUI.Runtime.Skia.Linux;
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
	private bool _hasAppliedConfigure;
	private bool _activationRequested;
	private volatile bool _minimizeRequested;
	// Written on the dispatch thread, also read by Minimize on the UI thread.
	private volatile WaylandWindowCapabilities _capabilities = WaylandWindowCapabilities.All;

	// Dispatch thread state.
	private int _pendingWidth;
	private int _pendingHeight;
	private WaylandToplevelStates _pendingStates;
	private WaylandToplevelStates _states;
	private uint _pendingDecorationMode;
	private uint _requestedDecorationMode;
	private bool _serverSideDecorations;
	private WaylandClientDecorations? _clientDecorations;
	private bool _clientDecorationsUnavailable;
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
	private readonly WaylandConfigureQueue _configures = new();
	private readonly WaylandPresentationState _presentation = new();
	private long _iconGeneration;
	// Most recently entered last; the last one is the window's current output.
	private readonly List<WaylandOutput> _enteredOutputs = new();
	private volatile WaylandOutputInfo? _outputInfo;

	internal WaylandWindowWrapper(WaylandConnection connection, Window window, XamlRoot root)
		: base(window, root)
	{
		_connection = connection;
		_id = Interlocked.Increment(ref _nextId);
		OwnerWindow = window;
		ActivationState = CoreWindowActivationState.Deactivated;
		_darkDecorations = Application.Current?.RequestedTheme == ApplicationTheme.Dark;
		var package = Windows.ApplicationModel.Package.Current;
		var appId = connection.AppId ??= package.Id.Name is { Length: > 0 } name ? name : "uno.app";
		var titleBar = window.AppWindow.TitleBar;
		_extendsContent = titleBar.ExtendsContentIntoTitleBar;
		var display = connection.Display;
		var hostConstructionStarted = false;
		try
		{
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
			hostConstructionStarted = true;
			_host = new WaylandXamlRootHost(connection, _id, window, root, _surface, _viewport, static _ => { }, PreparePresentation, OnConfigureFrameOpportunity, SetRenderAttemptActive, DestroyProtocolObjects);
			_host.LayoutRecorded = OnLayoutRecorded;
			lock (display.SyncRoot)
			{
				// Scale events may precede host construction; publish their accumulated output state now.
				UpdateTextPixelGeometry();
			}
			NonClientPointerSource = new WaylandNonClientPointerSource(connection, _toplevel, window, _id, _host, () => Scale, Post);
			connection.AddWindow(this);
			RasterizationScale = 1;
			// The deferred bounds notification must not raise AppWindow.Changed for the initial size.
			var initialSize = new SizeInt32(InitialWidth, InitialHeight);
			SetSizes(initialSize, initialSize);
			titleBar.ExtendsContentIntoTitleBarChanged += ExtendContentIntoTitleBar;
			CoreApplication.GetCurrentView().TitleBar.ExtendViewIntoTitleBarChanged += OnCoreTitleBarChanged;
			// Publish a provisional size so content can load and trigger Show; configure replaces it.
			// Posted because Uno subscribes to SizeChanged only after the factory returns this wrapper.
			_connection.PostToUI(() =>
			{
				if (!_closed && !_configured)
				{
					ApplyConfigure(InitialWidth, InitialHeight, 0, 1, WaylandToplevelStates.None, 0);
				}
			});
			UpdateWindowPropertiesFromPackage(package, window);
			if (WaylandDiagnostics.IsEnabled)
			{
				WaylandDiagnostics.Trace($"window{_id}: created (app_id={appId}, viewport={_viewport is not null}, fractional={_fractionalScale is not null}, decoration={_decoration is not null}, subcompositor={connection.Subcompositor is not null}, seat={connection.Seat is not null})");
			}
		}
		catch
		{
			_closed = true;
			try
			{
				WaylandCleanup.Run(
					() => titleBar.ExtendsContentIntoTitleBarChanged -= ExtendContentIntoTitleBar,
					() => CoreApplication.GetCurrentView().TitleBar.ExtendViewIntoTitleBarChanged -= OnCoreTitleBarChanged,
					() => NonClientPointerSource?.Dispose());
			}
			catch (Exception cleanupError)
			{
				connection.TrackWindowTeardown(Task.FromException(cleanupError));
			}
			if (_host is not null)
			{
				connection.RemoveWindow(this, _host.Completion);
				_host.Dispose(DestroyProtocolObjects);
			}
			else if (!hostConstructionStarted)
			{
				try
				{
					lock (display.SyncRoot)
					{
						DestroyProtocolObjects();
					}
				}
				catch (Exception cleanupError)
				{
					connection.TrackWindowTeardown(Task.FromException(cleanupError));
				}
			}
			throw;
		}
	}

	internal Window OwnerWindow { get; }
	internal WaylandXamlRootHost Host => _host;
	internal WaylandNonClientPointerSource NonClientPointerSource { get; }

	public override object NativeWindow => _surface;

	public override bool IsReadyForActivation => _hasAppliedConfigure;

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
		var generation = Interlocked.Increment(ref _iconGeneration);
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
				_ = _connection.Display.TryPost(() =>
				{
					using (bitmap)
					{
						if (!_closed && generation == Interlocked.Read(ref _iconGeneration))
						{
							WaylandWindowIcon.Apply(_connection, _toplevel, bitmap, _id);
						}
					}
				}, bitmap.Dispose);
			}
		});
	}

	protected override void ShowCore()
	{
		Post(() =>
		{
			// The initial commit without a buffer requests the first configure; rendering waits for its ack.
			_surface.Commit();
			if (WaylandDiagnostics.IsEnabled)
			{
				WaylandDiagnostics.Trace($"window{_id}: initial commit");
			}
		});
	}

	internal protected override void Activate()
	{
		_activationRequested = true;
		Post(() =>
		{
			if (_minimizeRequested || (_states & WaylandToplevelStates.Activated) == 0)
			{
				if (WaylandDiagnostics.IsEnabled)
				{
					WaylandDiagnostics.Trace($"window{_id}: activation requested");
				}
				_connection.Activate(_surface, () => !_closed);
			}
		});
	}

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
			if ((_states & ConstrainedStates) == 0)
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
		NonClientPointerSource.ExtendsContent = extend;
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

	// Returns whether the presenter state changed; OverlappedPresenter notifies AppWindow for its own calls.
	internal bool Minimize()
	{
		// The compositor ignores the request when it does not support minimizing.
		if ((_capabilities & WaylandWindowCapabilities.Minimize) == 0)
		{
			return false;
		}
		var stateChanged = !_minimizeRequested;
		_minimizeRequested = true;
		Post(() => _toplevel.SetMinimized());
		return stateChanged;
	}

	internal void Restore(bool activateWindow)
	{
		Post(() =>
		{
			_toplevel.UnsetMaximized();
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
		_host.PresentationReady();
	});

	// Decorations owner, dispatch thread.
	void IWaylandDecorationsOwner.RequestParentCommit() => _host.PresentationReady();

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

	void IWaylandDecorationsOwner.Minimize() => _connection.PostToUI(() =>
	{
		if (Minimize())
		{
			Window?.AppWindow.OnAppWindowChanged(AppWindowChangedEventArgs.PresenterChangedEventArgs);
		}
	});

	void IWaylandDecorationsOwner.RequestClose() => _connection.PostToUI(RequestClose);

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

	// Dispatch thread handlers stage state; geometry changes are acknowledged with their presentation.
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
		_configures.Receive(args.Serial);
		var acknowledged = _presentation.Generation != 0 && _layout == _presentation.Layout && EffectiveScale == _presentation.Scale
			&& _configures.CanAcknowledgeLatest(_layout, EffectiveScale);
		if (acknowledged)
		{
			// State-only changes need no new buffer; hidden surfaces may receive no frame callbacks.
			_configures.AcknowledgeLatest(AcknowledgeConfigure);
		}
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
		if (WaylandDiagnostics.IsEnabled)
		{
			WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: configure serial={args.Serial} suggested={_pendingWidth}x{_pendingHeight} geometry={_geometryWidth}x{_geometryHeight} content={_layout.ContentWidth}x{_layout.ContentHeight} states={_states} decorations={(_serverSideDecorations ? "server" : "client")} -> {(acknowledged ? "acknowledged" : "pending presentation")}"));
		}
		Publish(notifyActivation: true);
	}

	private void OnPreferredFractionalScale(object? sender, WpFractionalScaleV1.PreferredScaleEventArgs args)
	{
		_preferredScale = args.Scale;
		if (WaylandDiagnostics.IsEnabled)
		{
			WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: preferred fractional scale {args.Scale}/120"));
		}
		OnScaleChanged();
	}

	private void OnPreferredBufferScale(object? sender, WlSurface.PreferredBufferScaleEventArgs args)
	{
		_integerScale = Math.Max(args.Factor, 1);
		if (WaylandDiagnostics.IsEnabled)
		{
			WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: preferred buffer scale {args.Factor}"));
		}
		OnScaleChanged();
	}

	private void OnScaleChanged()
	{
		UpdateTextPixelGeometry();
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
		if (WaylandDiagnostics.IsEnabled)
		{
			WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: entered output {output.Info.Name}"));
		}
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
			if (WaylandDiagnostics.IsEnabled)
			{
				WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: left output {output.Info.Name}"));
			}
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
			_connection.PostToUI(() => _displayInformation?.NotifyDpiChanged());
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
		UpdateTextPixelGeometry();
	}

	private void UpdateTextPixelGeometry()
	{
		if (_closed || _host is not { } host)
		{
			return;
		}
		var infos = new List<WaylandOutputInfo>(_enteredOutputs.Count);
		foreach (var output in _enteredOutputs)
		{
			infos.Add(output.Info);
		}
		var scale = EffectiveScale;
		var preference = LinuxTextRenderingExtension.Instance.Settings.GetSurfaceGeometry(scale);
		host.SetOutputGeometry(GetTextPixelGeometry(infos, preference, scale), scale);
	}

	internal static SKPixelGeometry GetTextPixelGeometry(IReadOnlyCollection<WaylandOutputInfo> entered, SKPixelGeometry preference, double scale)
	{
		if (entered.Count == 0 || preference == SKPixelGeometry.Unknown || !double.IsFinite(scale) || scale < 1 || scale != Math.Truncate(scale))
		{
			return SKPixelGeometry.Unknown;
		}
		foreach (var output in entered)
		{
			var geometry = output.Subpixel switch
			{
				2 => SKPixelGeometry.RgbHorizontal,
				3 => SKPixelGeometry.BgrHorizontal,
				4 => SKPixelGeometry.RgbVertical,
				5 => SKPixelGeometry.BgrVertical,
				_ => SKPixelGeometry.Unknown,
			};
			// No LCD if the compositor may rotate/resample, or entered outputs disagree with the preference.
			if (output.Transform != 0 || output.Scale != scale || geometry != preference)
			{
				return SKPixelGeometry.Unknown;
			}
		}
		return preference;
	}

	private void OnToplevelClose(object? sender, XdgToplevel.CloseEventArgs args)
	{
		if (WaylandDiagnostics.IsEnabled)
		{
			WaylandDiagnostics.Trace($"window{_id}: close requested");
		}
		_connection.PostToUI(RequestClose);
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
		var mode = !_forceClientDecorations && WantsTitleBar && _isMaximizable && _isMinimizable
			? (uint)ZxdgToplevelDecorationV1.ModeValues.ServerSide
			: (uint)ZxdgToplevelDecorationV1.ModeValues.ClientSide;
		if (_decoration is not null && mode != _requestedDecorationMode)
		{
			_requestedDecorationMode = mode;
			_decoration.SetMode(mode);
			if (WaylandDiagnostics.IsEnabled)
			{
				WaylandDiagnostics.Trace($"window{_id}: request {(mode == (uint)ZxdgToplevelDecorationV1.ModeValues.ServerSide ? "server" : "client")}-side decorations");
			}
			return true;
		}
		return false;
	}

	// At xdg_surface.configure: the negotiated mode becomes effective. Without xdg-decoration it is client-side.
	private void ApplyDecorationMode()
	{
		_serverSideDecorations = _decoration is not null && _pendingDecorationMode == (uint)ZxdgToplevelDecorationV1.ModeValues.ServerSide;
		// Compositors may drop decorations of fullscreen windows; elsewhere, server-side ones expose all window controls.
		if (_serverSideDecorations
			&& _requestedDecorationMode == (uint)ZxdgToplevelDecorationV1.ModeValues.ClientSide
			&& (_states & WaylandToplevelStates.Fullscreen) == 0
			&& this.Log().IsEnabled(LogLevel.Warning))
		{
			this.Log().Warn("The compositor refused client-side decorations; title bar and presenter restrictions cannot be enforced by xdg-decoration.");
		}
		if (!_serverSideDecorations && _clientDecorations is null && !_clientDecorationsUnavailable)
		{
			if (_connection.Subcompositor is null)
			{
				_clientDecorationsUnavailable = true;
				if (this.Log().IsEnabled(LogLevel.Warning))
				{
					this.Log().Warn("The compositor uses client-side decorations but provides no wl_subcompositor; the window has no title bar.");
				}
				return;
			}
			_clientDecorations = new WaylandClientDecorations(_connection, _surface, _toplevel, _id, _darkDecorations, this);
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

	// Stage geometry and decorations for the next matching parent-buffer commit.
	private void ApplyGeometry()
	{
		var titleBarHeight = WaylandDecorationLayout.FitTitleBar(_geometryHeight, TitleBarHeight);
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

		ApplySizeConstraints();
	}

	// Render thread, immediately before committing a buffer rendered for the generation.
	// Acknowledges the configure and applies its geometry and decorations with that commit.
	private bool PreparePresentation(long generation, Func<bool> submit)
	{
		WaylandWindowConfigure configure;
		lock (_connection.Display.SyncRoot)
		{
			if (_closed || _configures.Current is not { } current || current.Generation != generation
				|| !PreparePresentationCore(current))
			{
				return false;
			}
			configure = current;
		}
		var committed = _presentation.Submit(generation, configure.Layout, configure.Scale, () =>
		{
			var submitted = submit();
			if (submitted)
			{
				_clientDecorations?.AdvancePendingState();
			}
			return submitted;
		});
		if (committed && WaylandDiagnostics.IsEnabled)
		{
			WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: presented generation {generation} serial={configure.Serial} buffer geometry {configure.Layout.ContentWidth}x{configure.Layout.ContentHeight}"));
		}
		return committed;
	}

	private bool PreparePresentationCore(WaylandWindowConfigure configure)
	{
		if (_clientDecorations is { } decorations)
		{
			if (!decorations.IsReady)
			{
				if (decorations.HasPendingState)
				{
					// Apply the cached subsurface buffer so its predecessor can be released.
					_surface.Commit();
					decorations.AdvancePendingState();
				}
				return false;
			}
		}
		if (_presentation.Generation == configure.Generation)
		{
			return true;
		}
		_configures.AcknowledgeCurrent(AcknowledgeConfigure);
		var layout = configure.Layout;
		_xdgSurface.SetWindowGeometry(0, layout.GeometryY, layout.ContentWidth, layout.GeometryHeight);
		return true;
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
		if (WaylandDiagnostics.IsEnabled)
		{
			WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: size constraints min={min.Item1}x{min.Item2} max={max.Item1}x{max.Item2}"));
		}
	}

	private void Publish(bool notifyActivation = false)
	{
		if (_configures.Enqueue(_layout, EffectiveScale, _preferredScale, _integerScale, _states, _configured, notifyActivation))
		{
			_connection.PostToUI(ApplyPendingConfigure);
		}
	}

	private void AcknowledgeConfigure(uint serial)
	{
		_xdgSurface.AckConfigure(serial);
		if (WaylandDiagnostics.IsEnabled)
		{
			WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: ack configure serial={serial}"));
		}
	}

	// Render thread: a compositor callback opens the next configure slot.
	private bool OnConfigureFrameOpportunity(long generation)
	{
		lock (_connection.Display.SyncRoot)
		{
			if (_closed || !_configures.FrameOpportunity(generation))
			{
				return false;
			}
			_connection.PostToUI(ApplyPendingConfigure);
			return true;
		}
	}

	// Render thread: the attempt keeps its configure until it ends, even if the fallback timer fires.
	private void SetRenderAttemptActive(bool active)
	{
		lock (_connection.Display.SyncRoot)
		{
			if (!_closed && _configures.SetAttemptActive(active))
			{
				_connection.PostToUI(ApplyPendingConfigure);
			}
		}
	}

	private void AdvanceConfigure(long generation)
	{
		lock (_connection.Display.SyncRoot)
		{
			if (!_closed && _configures.FrameOpportunity(generation))
			{
				_connection.PostToUI(ApplyPendingConfigure);
			}
		}
	}

	// UI thread, after the host recorded the current configure's layout.
	private void OnLayoutRecorded()
	{
		lock (_connection.Display.SyncRoot)
		{
			if (_closed || !_configures.ResumeDeferredApply())
			{
				return;
			}
		}
		_connection.PostToUI(ApplyPendingConfigure);
	}

	// UI thread. Coalesced configures advance at frame opportunities: the frame callback of the presented
	// predecessor, or the render scheduler's stall interval when nothing can be presented. A successor
	// waits until the UI has recorded the current layout, so a slow layout is never restarted mid-way.
	private void ApplyPendingConfigure()
	{
		WaylandWindowConfigure configure;
		lock (_connection.Display.SyncRoot)
		{
			if (_closed)
			{
				return;
			}
			if (_host.IsLayoutPending && _configures.DeferApply())
			{
				if (WaylandDiagnostics.IsEnabled)
				{
					WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: configure apply deferred until layout is recorded"));
				}
				return;
			}
			if (_configures.TakePending() is not { } pending)
			{
				return;
			}
			configure = pending;
		}
		var layout = configure.Layout;
		var states = configure.States;
		ApplyConfigure(layout.ContentWidth, layout.ContentHeight, layout.TitleBarHeight, configure.Scale, states, configure.Generation);
		if (configure.NotifyActivation && !_closed)
		{
			// Even an unchanged size must be applied before activation observers inspect the window.
			var initialActivation = !_hasAppliedConfigure && _activationRequested;
			_hasAppliedConfigure = true;
			ActivationState = (states & WaylandToplevelStates.Activated) != 0
				? CoreWindowActivationState.PointerActivated
				: initialActivation ? CoreWindowActivationState.CodeActivated : CoreWindowActivationState.Deactivated;
		}
		Post(() =>
		{
			if (_configures.Current?.Generation != configure.Generation)
			{
				return;
			}
			_clientDecorations?.Update(layout, _dispatchTitle, (states & WaylandToplevelStates.Activated) != 0,
				(states & WaylandToplevelStates.Maximized) != 0, configure.PreferredScale, configure.IntegerScale);
			// Pre-activation resizes update managed bounds, but buffers wait for the first configure.
			if (!configure.Configured)
			{
				// Nothing is presented before the first configure, so release the slot right away.
				AdvanceConfigure(configure.Generation);
				return;
			}
			_host.Configure(layout.ContentWidth, layout.ContentHeight, configure.PreferredScale, configure.IntegerScale, configure.Generation);
			if (_presentation.Generation == 0 || layout != _presentation.Layout || configure.Scale != _presentation.Scale)
			{
				return;
			}
			// Already presented geometry needs no frame callback, including on hidden surfaces: acknowledge
			// it and let a successor apply without waiting for a presentation that never comes.
			_configures.AcknowledgeCurrent(AcknowledgeConfigure);
			AdvanceConfigure(configure.Generation);
		});
	}

	// UI thread.
	private void ApplyConfigure(int width, int height, int titleBarHeight, double scale, WaylandToplevelStates states, long generation)
	{
		if (_closed)
		{
			return;
		}
		_host.BeginConfigureUI(generation);
		var previousStates = _uiStates;
		var wasMinimized = _minimizeRequested;
		_uiStates = states;
		if ((states & WaylandToplevelStates.Activated) != 0)
		{
			_minimizeRequested = false;
		}
		if (WaylandToplevelStateParser.HasPresenterChanged(previousStates, states, wasMinimized, _minimizeRequested))
		{
			Window?.AppWindow.OnAppWindowChanged(AppWindowChangedEventArgs.PresenterChangedEventArgs);
		}
		var scaleChanged = RasterizationScale != (float)scale;
		RasterizationScale = (float)scale;
		_host.Scale = scale;
		var bounds = new Rect(0, 0, width, height);
		var boundsChanged = Bounds != bounds;
		var client = new SizeInt32(WaylandRenderScale.ToBufferPixels(width, scale), WaylandRenderScale.ToBufferPixels(height, scale));
		var outer = new SizeInt32(client.Width, WaylandRenderScale.ToBufferPixels(height + titleBarHeight, scale));
		SetSizes(outer, client);
		SetBoundsAndVisibleBounds(bounds, bounds);
		if (scaleChanged)
		{
			_displayInformation?.NotifyDpiChanged();
		}
		_host.ConfigureUI(generation, boundsChanged || scaleChanged);
		_host.PresentationReady();
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
		Interlocked.Increment(ref _iconGeneration);
		if (WaylandDiagnostics.IsEnabled)
		{
			WaylandDiagnostics.Trace($"window{_id}: closing");
		}
		if (Window is { } window)
		{
			window.AppWindow.TitleBar.ExtendsContentIntoTitleBarChanged -= ExtendContentIntoTitleBar;
		}
		CoreApplication.GetCurrentView().TitleBar.ExtendViewIntoTitleBarChanged -= OnCoreTitleBarChanged;
		NonClientPointerSource.Dispose();
		_connection.RemoveWindow(this, _host.Completion);
		_host.Dispose(DestroyProtocolObjects);
	}

	private void DestroyProtocolObjects()
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
		WaylandCleanup.Run(
			() => _clientDecorations?.Dispose(),
			() => _decoration?.Dispose(),
			() => _fractionalScale?.Dispose(),
			() => _viewport?.Dispose(),
			() => _toplevel?.Dispose(),
			() => _xdgSurface?.Dispose(),
			() => _surface?.Dispose());
		if (WaylandDiagnostics.IsEnabled)
		{
			WaylandDiagnostics.Trace($"window{_id}: destroyed");
		}
	}
}
