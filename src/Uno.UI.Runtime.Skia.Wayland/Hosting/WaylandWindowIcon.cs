// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.IO;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;
using Uno.WinUI.Runtime.Skia.Wayland.Rendering;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// Window icons through xdg-toplevel-icon-v1, from the package logo like the X11 host's _NET_WM_ICON.
internal static class WaylandWindowIcon
{
	private const int MaximumSize = 256;

	// UI thread: BitmapImage.GetScaledPath reads DisplayInformation.
	internal static string? ResolvePackageLogo()
	{
		if (Windows.ApplicationModel.Package.Current.Logo is not { } uri)
		{
			return null;
		}
		var basePath = uri.OriginalString.Replace('\\', Path.DirectorySeparatorChar);
		var iconPath = Path.Combine(Windows.ApplicationModel.Package.Current.InstalledPath, basePath);
		if (File.Exists(iconPath))
		{
			return iconPath;
		}
		if (Microsoft.UI.Xaml.Media.Imaging.BitmapImage.GetScaledPath(basePath) is { } scaledPath && File.Exists(scaledPath))
		{
			return scaledPath;
		}
		if (typeof(WaylandWindowIcon).Log().IsEnabled(LogLevel.Warning))
		{
			typeof(WaylandWindowIcon).Log().Warn($"Unable to find icon file [{iconPath}] specified in the Package.appxmanifest file.");
		}
		return null;
	}

	// Any thread. Icon buffers must be square: the image is centered and scaled down to fit.
	internal static SKBitmap? Decode(string path)
	{
		using var source = SKBitmap.Decode(path);
		if (source is null)
		{
			if (typeof(WaylandWindowIcon).Log().IsEnabled(LogLevel.Error))
			{
				typeof(WaylandWindowIcon).Log().Error($"Unable to decode icon file [{path}].");
			}
			return null;
		}
		var size = Math.Min(Math.Max(source.Width, source.Height), MaximumSize);
		var scale = (float)size / Math.Max(source.Width, source.Height);
		var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
		using var canvas = new SKCanvas(bitmap);
		canvas.Clear(SKColors.Transparent);
		var width = source.Width * scale;
		var height = source.Height * scale;
		using var image = SKImage.FromBitmap(source);
		canvas.DrawImage(image, SKRect.Create((size - width) / 2, (size - height) / 2, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
		return bitmap;
	}

	// Dispatch thread. The compositor copies the pixels on set_icon; the buffer may go once the icon is destroyed.
	internal static void Apply(WaylandConnection connection, XdgToplevel toplevel, SKBitmap bitmap, int windowId)
	{
		if (connection.IconManager is not { } manager)
		{
			return;
		}
		using var buffer = new WaylandSoftwareRenderer.Buffer(connection.Shm, bitmap.Width, bitmap.Height, static _ => { });
		buffer.Surface.Canvas.DrawBitmap(bitmap, 0, 0);
		buffer.Surface.Canvas.Flush();
		var icon = manager.CreateIcon();
		try
		{
			icon.AddBuffer(buffer.Proxy, 1);
			manager.SetIcon(toplevel, icon);
			if (WaylandDiagnostics.IsEnabled)
			{
				WaylandDiagnostics.Trace(FormattableString.Invariant($"window{windowId}: icon {bitmap.Width}x{bitmap.Height}"));
			}
		}
		finally
		{
			icon.Dispose();
		}
	}
}
