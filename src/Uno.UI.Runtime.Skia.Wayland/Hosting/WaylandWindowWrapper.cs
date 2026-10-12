// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using Microsoft.UI.Xaml;
using Uno.UI.Xaml.Controls;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;
using Windows.Foundation;
using Windows.Graphics;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal sealed class WaylandWindowWrapper : NativeWindowWrapperBase
{
	private readonly WaylandXamlRootHost _host;
	private readonly XdgSurface _xdgSurface;
	private readonly XdgToplevel _toplevel;
	private readonly Action<Action> _postToProtocol;
	private string _title = string.Empty;
	private volatile bool _closed;

	internal WaylandWindowWrapper(Window window, XamlRoot root, WlSurface surface, XdgSurface xdgSurface, XdgToplevel toplevel, Action<Action> postToProtocol)
		: base(window, root)
	{
		_host = new WaylandXamlRootHost(window, root, surface);
		_xdgSurface = xdgSurface;
		_toplevel = toplevel;
		_postToProtocol = postToProtocol;
		RasterizationScale = 1;
	}

	public override object NativeWindow => _host.Surface;

	public override string Title
	{
		get => _title;
		set
		{
			_title = value;
			_postToProtocol(() =>
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
		_postToProtocol(() =>
		{
			if (!_closed)
			{
				// Initial empty commit requests configure; render must wait for its acknowledgement.
				_host.Surface.Commit();
			}
		});
	}

	// Called on the Uno UI thread after the dispatch thread has acknowledged configure.
	internal void ApplyConfigure(int width, int height, double scale)
	{
		if (_closed)
		{
			return;
		}
		if (!double.IsFinite(scale) || scale <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(scale));
		}
		var logicalWidth = width > 0 ? width : Bounds.Width > 0 ? Bounds.Width : InitialWidth;
		var logicalHeight = height > 0 ? height : Bounds.Height > 0 ? Bounds.Height : InitialHeight;
		RasterizationScale = (float)scale;
		var pixels = new SizeInt32(checked((int)Math.Floor(logicalWidth * scale + 0.5)), checked((int)Math.Floor(logicalHeight * scale + 0.5)));
		SetSizes(pixels, pixels);
		var bounds = new Rect(0, 0, logicalWidth, logicalHeight);
		SetBoundsAndVisibleBounds(bounds, bounds);
		_host.InvalidateRender();
	}

	internal void RequestClose()
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
		_host.Dispose();
		_postToProtocol(() =>
		{
			_toplevel.Dispose();
			_xdgSurface.Dispose();
			_host.Surface.Dispose();
		});
	}

	// TODO(map): xdg activation tokens, decoration negotiation and native presenters.
	// TODO(proto): wire configure/close events with UI snapshots; ack on the protocol thread.
}
