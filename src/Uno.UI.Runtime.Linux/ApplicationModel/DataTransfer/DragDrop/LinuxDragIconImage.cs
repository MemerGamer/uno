#nullable enable

using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer.DragDrop.Core;

namespace Uno.UI.Runtime.Linux;

// BGRA8 premultiplied pixels of the drag visual and the icon's top-left corner relative to the pointer, in logical pixels.
internal sealed record LinuxDragIconImage(int Width, int Height, byte[] Pixels, (double X, double Y)? Anchor)
{
	// Uno renders the drag visual during a later render pass; the drag waits briefly for it.
	private static readonly TimeSpan _captureTimeout = TimeSpan.FromMilliseconds(150);

	/// <summary>UI thread: copies the bitmap Uno rendered for the drag visual, if its pixels become available in time.</summary>
	internal static async Task<LinuxDragIconImage?> CaptureAsync(CoreDragInfo info)
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
		var pixelsTask = bitmap.GetPixelsAsync().AsTask();
		if (await Task.WhenAny(pixelsTask, Task.Delay((int)Math.Max(1, deadline - Environment.TickCount64))) != pixelsTask)
		{
			return null;
		}
		var pixels = (await pixelsTask).ToArray();
		var (width, height) = (bitmap.PixelWidth, bitmap.PixelHeight);
		return pixels.LongLength < (long)width * height * 4 ? null : new(width, height, pixels, ui.Anchor is { } anchor ? (anchor.X, anchor.Y) : null);
	}
}
