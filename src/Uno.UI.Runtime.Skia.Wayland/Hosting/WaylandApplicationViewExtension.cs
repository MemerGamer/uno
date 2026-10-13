// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using Microsoft.UI.Windowing;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal sealed class WaylandApplicationViewExtension(object owner, WaylandConnection connection) : IApplicationViewExtension
{
	private readonly ApplicationView _owner = (ApplicationView)owner;

	// Like X11, the size is passed through as the window size; the compositor may still constrain it.
	public bool TryResizeView(Size size)
	{
		if (AppWindow.GetFromWindowId(_owner.WindowId) is { } appWindow
			&& Microsoft.UI.Xaml.Window.GetFromAppWindow(appWindow) is { } window
			&& connection.FindWindow(window) is { } wrapper)
		{
			wrapper.Resize(new SizeInt32 { Width = (int)size.Width, Height = (int)size.Height });
			return true;
		}
		return false;
	}
}
