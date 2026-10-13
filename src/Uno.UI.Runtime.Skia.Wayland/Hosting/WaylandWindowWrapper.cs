// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Threading;
using Microsoft.UI.Xaml;
using Uno.UI.Xaml.Controls;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;
using Uno.WinUI.Runtime.Skia.Wayland.Rendering;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.Core;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// One wl_surface + xdg_surface + xdg_toplevel per Uno Window, sharing the host connection.
internal sealed class WaylandWindowWrapper : NativeWindowWrapperBase
{
	private const uint XdgToplevelStateActivated = 4;
	private const uint DecorationModeServerSide = 2;
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
	private string _title = string.Empty;
	private volatile bool _closed;
	private WaylandDisplayInformationExtension? _displayInformation;

	// Dispatch thread state.
	private int _pendingWidth;
	private int _pendingHeight;
	private bool _pendingActivated;
	private int _logicalWidth;
	private int _logicalHeight;
	private uint _preferredScale;
	private int _integerScale = 1;
	private int _requestedWidth = InitialWidth;
	private int _requestedHeight = InitialHeight;
	private bool _configured;

	internal WaylandWindowWrapper(WaylandConnection connection, Window window, XamlRoot root)
		: base(window, root)
	{
		_connection = connection;
		_id = Interlocked.Increment(ref _nextId);
		OwnerWindow = window;
		var display = connection.Display;
		// Create and subscribe inside one SyncRoot section so no event is dispatched before its handler exists.
		lock (display.SyncRoot)
		{
			_surface = connection.Compositor.CreateSurface();
			_surface.PreferredBufferScale += OnPreferredBufferScale;
			_xdgSurface = connection.WmBase.GetXdgSurface(_surface);
			_xdgSurface.Configure += OnXdgSurfaceConfigure;
			_toplevel = _xdgSurface.GetToplevel();
			_toplevel.Configure += OnToplevelConfigure;
			_toplevel.Close += OnToplevelClose;
			_toplevel.SetAppId(connection.AppId);
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
				// TODO: client-side decorations when the compositor picks CSD or lacks xdg-decoration.
				_decoration = decorations.GetToplevelDecoration(_toplevel);
				_decoration.SetMode(DecorationModeServerSide);
			}
		}
		_host = new WaylandXamlRootHost(connection, _id, window, root, _surface, _viewport, OnKeyboardFocus);
		connection.AddWindow(this);
		RasterizationScale = 1;
		// Publish a provisional size so content can load and trigger Show; configure replaces it.
		// Posted because Uno subscribes to SizeChanged only after the factory returns this wrapper.
		WaylandConnection.PostToUI(() =>
		{
			if (!_configured)
			{
				ApplyConfigure(_requestedWidth, _requestedHeight, 1);
			}
		});
		WaylandDiagnostics.Trace($"window{_id}: created (viewport={_viewport is not null}, fractional={_fractionalScale is not null}, decoration={_decoration is not null}, seat={connection.Seat is not null})");
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
			_connection.Display.Post(() =>
			{
				if (!_closed)
				{
					_toplevel.SetTitle(value);
				}
			});
		}
	}

	protected override void ShowCore()
	{
		_connection.Display.Post(() =>
		{
			if (!_closed)
			{
				// The initial commit without a buffer requests the first configure; rendering waits for its ack.
				_surface.Commit();
				WaylandDiagnostics.Trace($"window{_id}: initial commit");
			}
		});
	}

	public override void Resize(SizeInt32 size)
	{
		if (size.Width <= 0 || size.Height <= 0)
		{
			return;
		}
		var scale = RasterizationScale > 0 ? RasterizationScale : 1;
		var width = (int)Math.Round(size.Width / scale);
		var height = (int)Math.Round(size.Height / scale);
		_connection.Display.Post(() =>
		{
			_requestedWidth = width;
			_requestedHeight = height;
			// A compositor-chosen size wins; otherwise the client picks its own size.
			if (_configured && _pendingWidth == 0 && _pendingHeight == 0)
			{
				Publish(width, height);
			}
		});
	}

	internal void SetDisplayInformationExtension(WaylandDisplayInformationExtension extension) => _displayInformation = extension;

	// Dispatch thread handlers: copy state, acknowledge, then post to the UI and render owners.
	private void OnToplevelConfigure(object? sender, XdgToplevel.ConfigureEventArgs args)
	{
		_pendingWidth = args.Width;
		_pendingHeight = args.Height;
		_pendingActivated = false;
		var states = args.States;
		for (var i = 0; i + 4 <= states.Length; i += 4)
		{
			if (BitConverter.ToUInt32(states, i) == XdgToplevelStateActivated)
			{
				_pendingActivated = true;
			}
		}
	}

	private void OnXdgSurfaceConfigure(object? sender, XdgSurface.ConfigureEventArgs args)
	{
		if (_closed)
		{
			return;
		}
		var width = _pendingWidth > 0 ? _pendingWidth : _logicalWidth > 0 ? _logicalWidth : _requestedWidth;
		var height = _pendingHeight > 0 ? _pendingHeight : _logicalHeight > 0 ? _logicalHeight : _requestedHeight;
		_xdgSurface.SetWindowGeometry(0, 0, width, height);
		_xdgSurface.AckConfigure(args.Serial);
		_configured = true;
		WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: configure serial={args.Serial} suggested={_pendingWidth}x{_pendingHeight} applied={width}x{height} activated={_pendingActivated} -> ack"));
		var activated = _pendingActivated;
		WaylandConnection.PostToUI(() => ActivationState = activated ? CoreWindowActivationState.CodeActivated : CoreWindowActivationState.Deactivated);
		Publish(width, height);
	}

	private void OnPreferredFractionalScale(object? sender, WpFractionalScaleV1.PreferredScaleEventArgs args)
	{
		_preferredScale = args.Scale;
		WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: preferred fractional scale {args.Scale}/120"));
		if (_configured)
		{
			Publish(_logicalWidth, _logicalHeight);
		}
	}

	private void OnPreferredBufferScale(object? sender, WlSurface.PreferredBufferScaleEventArgs args)
	{
		_integerScale = Math.Max(args.Factor, 1);
		WaylandDiagnostics.Trace(FormattableString.Invariant($"window{_id}: preferred buffer scale {args.Factor}"));
		if (_configured)
		{
			Publish(_logicalWidth, _logicalHeight);
		}
	}

	private void OnToplevelClose(object? sender, XdgToplevel.CloseEventArgs args)
	{
		WaylandDiagnostics.Trace($"window{_id}: close requested");
		WaylandConnection.PostToUI(RequestClose);
	}

	private void Publish(int width, int height)
	{
		_logicalWidth = width;
		_logicalHeight = height;
		var scale = WaylandRenderScale.Compute(_viewport is not null, _preferredScale, _integerScale);
		WaylandConnection.PostToUI(() => ApplyConfigure(width, height, scale));
		_host.Configure(width, height, _preferredScale, _integerScale);
	}

	// UI thread.
	private void ApplyConfigure(int width, int height, double scale)
	{
		if (_closed)
		{
			return;
		}
		var scaleChanged = RasterizationScale != (float)scale;
		RasterizationScale = (float)scale;
		_host.Scale = scale;
		var pixels = new SizeInt32(WaylandRenderScale.ToBufferPixels(width, scale), WaylandRenderScale.ToBufferPixels(height, scale));
		SetSizes(pixels, pixels);
		var bounds = new Rect(0, 0, width, height);
		SetBoundsAndVisibleBounds(bounds, bounds);
		if (scaleChanged)
		{
			_displayInformation?.NotifyDpiChanged();
		}
		_host.InvalidateRender();
	}

	internal double Scale => RasterizationScale > 0 ? RasterizationScale : 1;

	private void OnKeyboardFocus(bool focused)
		=> ActivationState = focused ? CoreWindowActivationState.PointerActivated : CoreWindowActivationState.Deactivated;

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
		_connection.RemoveWindow(this);
		_host.Dispose(() =>
		{
			_decoration?.Dispose();
			_fractionalScale?.Dispose();
			_viewport?.Dispose();
			_toplevel.Dispose();
			_xdgSurface.Dispose();
			_surface.Dispose();
			WaylandDiagnostics.Trace($"window{_id}: destroyed");
		});
	}

	// TODO: xdg-activation tokens, CSD title bar, fullscreen/maximize presenters and min/max size constraints.
}
