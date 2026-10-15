// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;
using Uno.WinUI.Runtime.Skia.Wayland.Rendering;
using Windows.ApplicationModel.DataTransfer.DragDrop.Core;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// BGRA8 premultiplied pixels of the drag visual and the icon's top-left corner relative to the pointer, in logical pixels.
internal sealed record WaylandDragIconImage(int Width, int Height, byte[] Pixels, (double X, double Y)? Anchor);

// The wl_surface shown under the pointer during an outgoing drag: the snapshot Uno's DragView would show,
// or a generic icon when none was rendered yet. Created and disposed on the dispatch thread.
internal sealed class WaylandDragIcon : IDisposable
{
	private const int FallbackLogicalSize = 32;

	private readonly WlSurface _surface;
	private readonly WpViewport? _viewport;
	private readonly WaylandSoftwareRenderer.Buffer _buffer;
	private readonly WaylandDragIconLayout _layout;
	private bool _disposed;

	private WaylandDragIcon(WlSurface surface, WpViewport? viewport, WaylandSoftwareRenderer.Buffer buffer, WaylandDragIconLayout layout)
	{
		_surface = surface;
		_viewport = viewport;
		_buffer = buffer;
		_layout = layout;
	}

	internal WlSurface Surface => _surface;

	// Uno renders the drag visual during a later render pass; the drag waits briefly for it.
	private static readonly TimeSpan _captureTimeout = TimeSpan.FromMilliseconds(150);

	/// <summary>UI thread: copies the bitmap Uno rendered for the drag visual, if its pixels become available in time.</summary>
	internal static async Task<WaylandDragIconImage?> CaptureAsync(CoreDragInfo info)
	{
		if (info.DragUI is not DragUI { Content: RenderTargetBitmap bitmap } ui)
		{
			return null;
		}
		var deadline = Environment.TickCount64 + (long)_captureTimeout.TotalMilliseconds;
		while (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
		{
			if (Environment.TickCount64 >= deadline)
			{
				return null;
			}
			await Task.Delay(16);
		}
		var pixels = (await bitmap.GetPixelsAsync()).ToArray();
		var (width, height) = (bitmap.PixelWidth, bitmap.PixelHeight);
		return pixels.Length < width * height * 4 ? null : new(width, height, pixels, ui.Anchor is { } anchor ? (anchor.X, anchor.Y) : null);
	}

	/// <summary>Dispatch thread: creates the surface without a buffer; it is attached by <see cref="Present"/> once it has the icon role.</summary>
	internal static WaylandDragIcon Create(WaylandConnection connection, WaylandDragIconImage? image, double scale)
	{
		var hasViewport = connection.Viewporter is not null;
		var layout = image is null
			? WaylandDragIconLayout.Compute((int)Math.Round(FallbackLogicalSize * scale), (int)Math.Round(FallbackLogicalSize * scale), scale, (-8, -8), hasViewport)
			: WaylandDragIconLayout.Compute(image.Width, image.Height, scale, image.Anchor, hasViewport);
		var surface = connection.Compositor.CreateSurface();
		WpViewport? viewport = null;
		WaylandSoftwareRenderer.Buffer? buffer = null;
		try
		{
			viewport = connection.Viewporter?.GetViewport(surface);
			buffer = new WaylandSoftwareRenderer.Buffer(connection.Shm, layout.BufferWidth, layout.BufferHeight, _ => { });
			Draw(buffer.Surface.Canvas, image, layout);
			buffer.Surface.Flush();
			return new WaylandDragIcon(surface, viewport, buffer, layout);
		}
		catch
		{
			buffer?.Dispose();
			viewport?.Dispose();
			surface.Dispose();
			throw;
		}
	}

	private static void Draw(SKCanvas canvas, WaylandDragIconImage? image, WaylandDragIconLayout layout)
	{
		canvas.Clear(SKColors.Transparent);
		var bounds = SKRect.Create(0, 0, layout.BufferWidth, layout.BufferHeight);
		if (image is not null)
		{
			var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
			using var source = SKImage.FromPixelCopy(info, image.Pixels, info.RowBytes);
			if (source is not null)
			{
				canvas.DrawImage(source, bounds, new SKSamplingOptions(SKCubicResampler.Mitchell));
				return;
			}
		}
		// A page outline: the drag has started but nothing was rendered for it.
		var unit = layout.BufferWidth / (float)FallbackLogicalSize;
		using var fill = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0xE6), IsAntialias = true, Style = SKPaintStyle.Fill };
		using var stroke = new SKPaint { Color = new SKColor(0x40, 0x40, 0x40, 0xFF), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * unit };
		var page = SKRect.Create(6 * unit, 3 * unit, 20 * unit, 26 * unit);
		var fold = 7 * unit;
		using var path = new SKPath();
		path.MoveTo(page.Left, page.Top);
		path.LineTo(page.Right - fold, page.Top);
		path.LineTo(page.Right, page.Top + fold);
		path.LineTo(page.Right, page.Bottom);
		path.LineTo(page.Left, page.Bottom);
		path.Close();
		canvas.DrawPath(path, fill);
		canvas.DrawPath(path, stroke);
		canvas.DrawLine(page.Right - fold, page.Top, page.Right - fold, page.Top + fold, stroke);
		canvas.DrawLine(page.Right - fold, page.Top + fold, page.Right, page.Top + fold, stroke);
		for (var line = 0; line < 3; line++)
		{
			var y = page.Top + 12 * unit + line * 4 * unit;
			canvas.DrawLine(page.Left + 4 * unit, y, page.Right - 4 * unit, y, stroke);
		}
	}

	/// <summary>Dispatch thread, after wl_data_device.start_drag gave the surface its role.</summary>
	internal void Present()
	{
		if (_disposed)
		{
			return;
		}
		if (_surface.Version >= 3)
		{
			_surface.SetBufferScale(_layout.BufferScale);
		}
		_viewport?.SetDestination(_layout.LogicalWidth, _layout.LogicalHeight);
		// The offset places the icon relative to the hotspot; attach carried it before wl_surface v5.
		if (_surface.Version >= 5)
		{
			_surface.Offset(_layout.OffsetX, _layout.OffsetY);
			_surface.Attach(_buffer.Proxy, 0, 0);
		}
		else
		{
			_surface.Attach(_buffer.Proxy, _layout.OffsetX, _layout.OffsetY);
		}
		_surface.Damage(0, 0, int.MaxValue, int.MaxValue);
		_surface.Commit();
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_viewport?.Dispose();
		_surface.Dispose();
		_buffer.Dispose();
	}
}
