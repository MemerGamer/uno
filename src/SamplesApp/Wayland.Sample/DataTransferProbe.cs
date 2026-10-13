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
//   UNO_SAMPLE_COPY_DELAYS_MS=N,...         copies at each delay instead of after 2 s; text copies after the first are numbered
//   UNO_SAMPLE_PASTE=1                      prints the clipboard on start and on every ContentChanged
//   UNO_SAMPLE_DROP_ACCEPT=copy|move|both   operation accepted on DragOver (default: copy)
//   UNO_SAMPLE_DROP_DEFER_MS=N              DragOver decides only after an N ms deferral
// The window content always accepts drops and prints what was dropped.
internal static class DataTransferProbe
{
	internal static void Attach(UIElement root)
	{
		root.AllowDrop = true;
		var accept = Environment.GetEnvironmentVariable("UNO_SAMPLE_DROP_ACCEPT") switch
		{
			"move" => DataPackageOperation.Move,
			"both" => DataPackageOperation.Copy | DataPackageOperation.Move,
			_ => DataPackageOperation.Copy,
		};
		var deferMilliseconds = int.TryParse(Environment.GetEnvironmentVariable("UNO_SAMPLE_DROP_DEFER_MS"), out var ms) ? ms : 0;
		root.DragOver += async (_, e) =>
		{
			Console.WriteLine($"probe: drag over {e.GetPosition(root)} allowed={e.AllowedOperations} formats=[{string.Join(", ", e.DataView.AvailableFormats)}]");
			if (deferMilliseconds > 0)
			{
				var deferral = e.GetDeferral();
				await Task.Delay(deferMilliseconds);
				e.AcceptedOperation = accept;
				deferral.Complete();
			}
			else
			{
				e.AcceptedOperation = accept;
			}
		};
		root.Drop += async (_, e) =>
		{
			var deferral = e.GetDeferral();
			Console.WriteLine($"probe: drop accepted={e.AcceptedOperation} allowed={e.AllowedOperations} {await DescribeAsync(e.DataView)}");
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
			var delays = (Environment.GetEnvironmentVariable("UNO_SAMPLE_COPY_DELAYS_MS") ?? "2000").Split(',', StringSplitOptions.RemoveEmptyEntries);
			for (var i = 0; i < delays.Length; i++)
			{
				var index = i;
				var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(int.Parse(delays[i], System.Globalization.CultureInfo.InvariantCulture)) };
				timer.Tick += (_, _) =>
				{
					timer.Stop();
					_ = CopyAsync(copy, index);
				};
				timer.Start();
			}
		}
	}

	private static async Task CopyAsync(string kind, int index)
	{
		var package = new DataPackage();
		switch (kind)
		{
			case "text":
				package.SetText(index == 0 ? "Hello from Uno Wayland ✓" : $"Uno copy #{index + 1}");
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
		Console.WriteLine($"probe: copied {kind} #{index + 1}, readback {await DescribeAsync(Clipboard.GetContent())}");
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
