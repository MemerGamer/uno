#nullable enable

using System;
using System.Threading;
using Windows.Foundation;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.UI.Helpers;
using Uno.UI.Hosting;
using Uno.WinUI.Runtime.Skia.Linux;

namespace Uno.WinUI.Runtime.Skia.X11;

internal abstract class X11Renderer : IDisposable
{
	private int _renderCount;
	private SKColor _background = SKColors.White;
	private SKSurface? _surface;
	private SKSizeI _surfaceSize;
	private int _outputGeometryDirty = 1;
	private SKPixelGeometry _outputGeometry;
	private SKPixelGeometry _preferredGeometry;
	private readonly RetainedLayer _retainedLayer = new();
	private X11AirspaceRenderHelper? _airspaceHelper;
	private readonly IXamlRootHost _host;
	protected readonly X11Window _x11Window;

	protected X11Renderer(IXamlRootHost host, X11Window x11Window)
	{
		_host = host;
		_x11Window = x11Window;
	}

	public void SetBackgroundColor(SKColor color) => _background = color;

	protected virtual bool UsesRetainedLayer => false;

	protected virtual GRContext? GpuContext => null;

	protected SKPixelGeometry PixelGeometry { get; private set; }

	protected void DisposeRetainedLayer() => _retainedLayer.Dispose();

	internal void InvalidateOutputGeometry() => Interlocked.Exchange(ref _outputGeometryDirty, 1);

	public void Render()
	{
		if (this.Log().IsEnabled(LogLevel.Trace))
		{
			this.Log().Trace($"Render {_renderCount++}");
		}

		var display = _x11Window.Display;
		var window = _x11Window.Window;

		if (_host is X11XamlRootHost { Closed.IsCompleted: true })
		{
			return;
		}

		using (X11Helper.XLock(display))
		{
			MakeCurrent();
		}

		var useLayer = UsesRetainedLayer;
		var renderCanvas = useLayer ? _retainedLayer.Surface?.Canvas : _surface?.Canvas;

		var extension = LinuxTextRenderingExtension.Instance;
		var preference = extension.PreferredGeometry;
		if (Interlocked.Exchange(ref _outputGeometryDirty, 0) != 0 || _preferredGeometry != preference)
		{
			_preferredGeometry = preference;
			_outputGeometry = _host is X11XamlRootHost { TracksOutputGeometry: true }
				? X11TextPixelGeometry.Read(_x11Window, preference) : SKPixelGeometry.Unknown;
		}
		var scale = _host.RootElement!.XamlRoot!.RasterizationScale;
		var requestedGeometry = extension.Settings.Antialias && double.IsFinite(scale) && scale >= 1 && scale == Math.Truncate(scale)
			? _outputGeometry : SKPixelGeometry.Unknown;
		var nativeElementClipPath = ((CompositionTarget)_host.RootElement.Visual.CompositionTarget!).OnNativePlatformFrameRequested(renderCanvas, (size, pixelGeometry) =>
		{
			PixelGeometry = pixelGeometry;
			var surfaceSize = new SKSizeI((int)size.Width, (int)size.Height);
			SKSurface renderSurface;
			using (X11Helper.XLock(display))
			{
				if (_surface is null || _surfaceSize != surfaceSize)
				{
					_surface?.Dispose();
					_surface = UpdateSize(surfaceSize.Width, surfaceSize.Height);
					_surfaceSize = surfaceSize;
					_airspaceHelper?.Dispose();
					_airspaceHelper = new X11AirspaceRenderHelper(display, window, surfaceSize.Width, surfaceSize.Height);
				}
				else if (!useLayer)
				{
					var updatedSurface = UpdateSurfaceGeometry(_surface);
					if (!ReferenceEquals(updatedSurface, _surface))
					{
						_surface.Dispose();
						_surface = updatedSurface;
					}
				}
				renderSurface = useLayer
					? _retainedLayer.EnsureSurface(GpuContext!, surfaceSize.Width, surfaceSize.Height, _background, pixelGeometry)
					: _surface;
			}
			if (!useLayer)
			{
				renderSurface.Canvas.Clear(_background);
			}
			return renderSurface.Canvas;
		}, requestedGeometry);

		if (useLayer && _surface is { } surface)
		{
			_retainedLayer.Present(surface);
		}

		_airspaceHelper?.XShapeClip(nativeElementClipPath);

		using (X11Helper.XLock(display))
		{
			Flush();
			_ = XLib.XFlush(display);
		}
	}

	protected virtual SKSurface UpdateSurfaceGeometry(SKSurface surface) => surface;

	protected abstract SKSurface UpdateSize(int width, int height);
	protected virtual void MakeCurrent() { }
	protected abstract void Flush();
	public abstract void Dispose();
}
