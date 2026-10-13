// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using Microsoft.UI.Windowing;
using Windows.Graphics.Display;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// Per-window scale comes from the surface (fractional-scale-v1 or preferred_buffer_scale), never output millimetres.
internal sealed class WaylandDisplayInformationExtension : IDisplayInformationExtension
{
	private readonly DisplayInformation _owner;
	private readonly WaylandConnection _connection;
	private WaylandWindowWrapper? _window;

	internal WaylandDisplayInformationExtension(object owner, WaylandConnection connection)
	{
		_owner = (DisplayInformation)owner;
		_connection = connection;
	}

	// Resolved lazily: DisplayInformation can be created before the window wrapper is registered.
	private WaylandWindowWrapper? Window
	{
		get
		{
			if (_window is null
				&& AppWindow.GetFromWindowId(_owner.WindowId) is { } appWindow
				&& Microsoft.UI.Xaml.Window.GetFromAppWindow(appWindow) is { } window
				&& _connection.FindWindow(window) is { } wrapper)
			{
				_window = wrapper;
				wrapper.SetDisplayInformationExtension(this);
			}
			return _window;
		}
	}

	private double Scale => Window?.Scale ?? 1;

	public DisplayOrientations CurrentOrientation => DisplayOrientations.Landscape;

	public uint ScreenWidthInRawPixels => (uint)Math.Max(_connection.OutputPixelSize.Width, 0);

	public uint ScreenHeightInRawPixels => (uint)Math.Max(_connection.OutputPixelSize.Height, 0);

	public float LogicalDpi => (float)(DisplayInformation.BaseDpi * Scale);

	public double RawPixelsPerViewPixel => Scale;

	public ResolutionScale ResolutionScale => (ResolutionScale)(int)Math.Round(Scale * 100);

	public double? DiagonalSizeInInches
	{
		get
		{
			var (width, height) = _connection.OutputPhysicalSizeMm;
			return width > 0 && height > 0 ? Math.Sqrt(width * width + height * height) / 25.4 : null;
		}
	}

	internal void NotifyDpiChanged() => _owner.NotifyDpiChanged();
}
