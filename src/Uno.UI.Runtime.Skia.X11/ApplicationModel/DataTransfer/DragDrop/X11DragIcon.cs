// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer.DragDrop.Core;

namespace Uno.WinUI.Runtime.Skia.X11;

internal sealed record X11DragIconImage(int Width, int Height, byte[] Pixels, (double X, double Y)? Anchor);

// All native resources belong to the source thread and its private display.
internal sealed class X11DragIcon : IDisposable
{
	private readonly IntPtr _display;
	private readonly X11DragIconLayout _layout;
	private SKBitmap? _bitmap;
	private IntPtr _image;
	private IntPtr _gc;
	private IntPtr _colormap;

	private X11DragIcon(IntPtr display, X11DragIconLayout layout)
	{
		_display = display;
		_layout = layout;
	}

	internal IntPtr Window { get; private set; }

	internal static async Task<X11DragIconImage?> CaptureAsync(CoreDragInfo info)
	{
		if (info.DragUI is not DragUI { Content: RenderTargetBitmap bitmap } ui)
		{
			return null;
		}
		var deadline = Environment.TickCount64 + 150;
		while (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
		{
			if (Environment.TickCount64 >= deadline)
			{
				return null;
			}
			await Task.Delay(16);
		}
		var pixelsTask = bitmap.GetPixelsAsync().AsTask();
		if (await Task.WhenAny(pixelsTask, Task.Delay((int)Math.Max(1, deadline - Environment.TickCount64))) != pixelsTask)
		{
			return null;
		}
		var pixels = (await pixelsTask).ToArray();
		var (width, height) = (bitmap.PixelWidth, bitmap.PixelHeight);
		return pixels.LongLength < (long)width * height * 4 ? null : new(width, height, pixels, ui.Anchor is { } anchor ? (anchor.X, anchor.Y) : null);
	}

	internal static unsafe X11DragIcon? Create(IntPtr display, X11DragIconImage image, double scale)
	{
		using var xlock = X11Helper.XLock(display);
		if (!BitConverter.IsLittleEndian || XLib.XMatchVisualInfo(display, XLib.XDefaultScreen(display), 32, 4 /* TrueColor */, out var visual) == 0
			|| !X11Helper.XShapeQueryExtension(display, out _, out _))
		{
			return null;
		}
		var layout = X11DragIconLayout.Compute(image.Width, image.Height, scale, image.Anchor);
		var icon = new X11DragIcon(display, layout);
		try
		{
			var root = XLib.XDefaultRootWindow(display);
			icon._colormap = XLib.XCreateColormap(display, root, visual.visual, 0);
			var attributes = new XSetWindowAttributes
			{
				override_redirect = 1,
				colormap = icon._colormap,
				event_mask = (IntPtr)XEventMask.ExposureMask
			};
			// CWBackPixel | CWBorderPixel | CWOverrideRedirect | CWEventMask | CWColormap.
			icon.Window = XLib.XCreateWindow(display, root, 0, 0, layout.Width, layout.Height, 0, 32, 1, visual.visual,
				(UIntPtr)((1 << 1) | (1 << 3) | (1 << 9) | (1 << 11) | (1 << 13)), ref attributes);
			if (icon.Window == IntPtr.Zero)
			{
				icon.Dispose();
				return null;
			}
			XLib.XChangeProperty(display, icon.Window, X11Helper.GetAtom(display, "_NET_WM_WINDOW_TYPE"), X11Helper.GetAtom(display, "ATOM"),
				32, PropertyMode.Replace, new[] { X11Helper.GetAtom(display, "_NET_WM_WINDOW_TYPE_DND") }, 1);
			X11Helper.XShapeCombineRectangles(display, icon.Window, 2 /* ShapeInput */, 0, 0, null, 0, 0, 0);
			icon._bitmap = new SKBitmap(new SKImageInfo(layout.Width, layout.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
			using (var source = SKImage.FromPixelCopy(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul), image.Pixels))
			using (var canvas = new SKCanvas(icon._bitmap))
			{
				canvas.Clear(SKColors.Transparent);
				canvas.DrawImage(source, SKRect.Create(layout.Width, layout.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
			}
			icon._image = X11Helper.XCreateImage(display, visual.visual, 32, 2, 0, icon._bitmap.GetPixels(), (uint)layout.Width, (uint)layout.Height, 32, icon._bitmap.RowBytes);
			icon._gc = X11Helper.XCreateGC(display, icon.Window, 0, IntPtr.Zero);
			_ = XLib.XMapWindow(display, icon.Window);
			_ = XLib.XRaiseWindow(display, icon.Window);
			icon.Draw();
			return icon;
		}
		catch
		{
			icon.Dispose();
			throw;
		}
	}

	internal void Move(int rootX, int rootY)
	{
		using var xlock = X11Helper.XLock(_display);
		_ = X11Helper.XMoveWindow(_display, Window, rootX + _layout.OffsetX, rootY + _layout.OffsetY);
	}

	internal void Draw()
	{
		using var xlock = X11Helper.XLock(_display);
		if (_image != IntPtr.Zero && _gc != IntPtr.Zero)
		{
			_ = X11Helper.XPutImage(_display, Window, _gc, _image, 0, 0, 0, 0, (uint)_layout.Width, (uint)_layout.Height);
		}
	}

	public unsafe void Dispose()
	{
		using var xlock = X11Helper.XLock(_display);
		if (_image != IntPtr.Zero)
		{
			// The bitmap owns the pixels; XDestroyImage must only free the image structure.
			((XImage*)_image)->data = IntPtr.Zero;
			_ = XLib.XDestroyImage(_image);
			_image = IntPtr.Zero;
		}
		_bitmap?.Dispose();
		_bitmap = null;
		if (_gc != IntPtr.Zero)
		{
			_ = X11Helper.XFreeGC(_display, _gc);
			_gc = IntPtr.Zero;
		}
		if (Window != IntPtr.Zero)
		{
			_ = XLib.XDestroyWindow(_display, Window);
			Window = IntPtr.Zero;
		}
		if (_colormap != IntPtr.Zero)
		{
			_ = X11Helper.XFreeColormap(_display, _colormap);
			_colormap = IntPtr.Zero;
		}
	}
}
