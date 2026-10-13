// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Wayland.Sample;

// Clipboard and drop checks driven by environment variables, reporting on stdout:
//   UNO_SAMPLE_COPY=text|html|files|image  sets the clipboard once the window is up (files: UNO_SAMPLE_COPY_FILES, ':'-separated)
//   UNO_SAMPLE_PASTE=1                      prints the clipboard on start and on every ContentChanged
// The window content always accepts drops and prints what was dropped.
internal static class DataTransferProbe
{
	internal static void Attach(UIElement root)
	{
		root.AllowDrop = true;
		root.DragOver += (_, e) =>
		{
			e.AcceptedOperation = DataPackageOperation.Copy;
			Console.WriteLine($"probe: drag over {e.GetPosition(root)} formats=[{string.Join(", ", e.DataView.AvailableFormats)}]");
		};
		root.Drop += async (_, e) =>
		{
			var deferral = e.GetDeferral();
			Console.WriteLine($"probe: drop {await DescribeAsync(e.DataView)}");
			deferral.Complete();
		};

		if (Environment.GetEnvironmentVariable("UNO_SAMPLE_PASTE") == "1")
		{
			Clipboard.ContentChanged += async (_, _) => Console.WriteLine($"probe: clipboard changed {await DescribeAsync(Clipboard.GetContent())}");
			var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
			timer.Tick += async (_, _) =>
			{
				timer.Stop();
				Console.WriteLine($"probe: clipboard {await DescribeAsync(Clipboard.GetContent())}");
			};
			timer.Start();
		}

		if (Environment.GetEnvironmentVariable("UNO_SAMPLE_COPY") is { Length: > 0 } copy)
		{
			// Leave time for the compositor to map and focus the window: only the focused client may set the selection.
			var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
			timer.Tick += (_, _) =>
			{
				timer.Stop();
				_ = CopyAsync(copy);
			};
			timer.Start();
		}
	}

	private static async Task CopyAsync(string kind)
	{
		var package = new DataPackage();
		switch (kind)
		{
			case "text":
				package.SetText("Hello from Uno Wayland ✓");
				break;
			case "html":
				package.SetText("bold text");
				package.SetHtmlFormat("<b>bold</b> text");
				break;
			case "files":
				var items = new System.Collections.Generic.List<IStorageItem>();
				foreach (var path in (Environment.GetEnvironmentVariable("UNO_SAMPLE_COPY_FILES") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
				{
					items.Add(Directory.Exists(path) ? await StorageFolder.GetFolderFromPathAsync(path) : await StorageFile.GetFileFromPathAsync(path));
				}
				package.SetStorageItems(items);
				break;
			case "image":
				using (var bitmap = new SkiaSharp.SKBitmap(4, 3))
				{
					bitmap.Erase(SkiaSharp.SKColors.Red);
					var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
					package.SetBitmap(RandomAccessStreamReference.CreateFromStream(new MemoryStream(png).AsRandomAccessStream()));
				}
				break;
		}
		Clipboard.SetContent(package);
		Console.WriteLine($"probe: copied {kind}");
	}

	internal static async Task<string> DescribeAsync(DataPackageView? view)
	{
		if (view is null)
		{
			return "(no clipboard)";
		}
		var text = new StringBuilder($"formats=[{string.Join(", ", view.AvailableFormats)}]");
		try
		{
			if (view.Contains(StandardDataFormats.Text))
			{
				text.Append($" text=\"{await view.GetTextAsync()}\"");
			}
			if (view.Contains(StandardDataFormats.Html))
			{
				text.Append($" html=\"{await view.GetHtmlFormatAsync()}\"");
			}
			if (view.Contains(StandardDataFormats.StorageItems))
			{
				text.Append($" items=[{string.Join(", ", (await view.GetStorageItemsAsync()).Select(item => item.Path))}]");
			}
			if (view.Contains(StandardDataFormats.Bitmap))
			{
				using var stream = await (await view.GetBitmapAsync()).OpenReadAsync();
				using var bitmap = SkiaSharp.SKBitmap.Decode(stream.AsStreamForRead());
				text.Append($" bitmap={bitmap?.Width}x{bitmap?.Height}");
			}
		}
		catch (Exception error)
		{
			text.Append($" error={error}");
		}
		return text.ToString();
	}
}
