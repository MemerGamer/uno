// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using Microsoft.UI.Xaml;
using Uno.Foundation.Extensibility;
using Uno.UI.Xaml.Controls;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal static class WaylandRegistrations
{
	internal static void Register(Func<Window, XamlRoot, WaylandWindowWrapper> createWindow)
	{
		ApiExtensibility.Register(typeof(INativeWindowFactoryExtension), _ => new WaylandNativeWindowFactoryExtension(createWindow));
		// TODO(input): register IUnoCorePointerInputSource and IUnoKeyboardInputSource scoped to IXamlRootHost.
		// TODO(render): register IDisplayInformationExtension and INativeOpenGLWrapper.
		// TODO(map): core application, application view, portal services, clipboard, DnD and text input.
	}
}

internal sealed class WaylandNativeWindowFactoryExtension : INativeWindowFactoryExtension
{
	private readonly Func<Window, XamlRoot, WaylandWindowWrapper> _createWindow;

	internal WaylandNativeWindowFactoryExtension(Func<Window, XamlRoot, WaylandWindowWrapper> createWindow)
		=> _createWindow = createWindow;

	public bool SupportsClosingCancellation => true;
	public bool SupportsMultipleWindows => true;
	public INativeWindowWrapper CreateWindow(Window window, XamlRoot xamlRoot) => _createWindow(window, xamlRoot);
}
