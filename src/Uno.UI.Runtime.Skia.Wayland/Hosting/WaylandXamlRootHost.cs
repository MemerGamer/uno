// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using Microsoft.UI.Xaml;
using Uno.UI.Hosting;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal sealed class WaylandXamlRootHost : IXamlRootHost, IDisposable
{
	private readonly Window _window;
	private readonly XamlRoot _xamlRoot;
	private bool _disposed;

	internal WaylandXamlRootHost(Window window, XamlRoot xamlRoot, WlSurface surface)
	{
		_window = window;
		_xamlRoot = xamlRoot;
		Surface = surface;
		XamlRootMap.Register(xamlRoot, this);
	}

	internal WlSurface Surface { get; }
	public UIElement? RootElement => _window.RootElement;
	internal event EventHandler? RenderRequested;

	public void InvalidateRender()
	{
		if (!_disposed)
		{
			// TODO(render): subscribe a coalescing frame scheduler; marshal commits to the dispatch thread.
			RenderRequested?.Invoke(this, EventArgs.Empty);
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		XamlRootMap.Unregister(_xamlRoot);
		RenderRequested = null;
		// TODO(render/input): detach listeners and retire released buffers before surface destruction.
	}
}
