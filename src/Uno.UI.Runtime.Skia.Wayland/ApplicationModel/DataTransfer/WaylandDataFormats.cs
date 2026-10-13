// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// Maps DataPackage formats to the MIME types exchanged through wl_data_source / wl_data_offer.
internal static class WaylandDataFormats
{
	// In order of preference when reading. UTF8_STRING, TEXT and STRING are the X11 targets that XWayland clients use.
	internal static readonly string[] TextMimeTypes = ["text/plain;charset=utf-8", "UTF8_STRING", "text/plain", "TEXT", "STRING"];
	internal const string HtmlMimeType = "text/html";
	internal static readonly string[] ImageMimeTypes = ["image/png", "image/jpeg"];

	// wl_data_device_manager.dnd_action
	internal const uint DndActionCopy = 1;
	internal const uint DndActionMove = 2;

	/// <summary>The MIME types under which <paramref name="view"/> can be sent, most specific first.</summary>
	internal static List<string> GetMimeTypes(DataPackageView view)
	{
		var formats = view.AvailableFormats;
		var mimeTypes = new List<string>();
		if (formats.Contains(StandardDataFormats.StorageItems))
		{
			mimeTypes.Add(WaylandFileUris.UriListMimeType);
			mimeTypes.Add(WaylandFileUris.GnomeCopiedFilesMimeType);
		}
		if (formats.Contains(StandardDataFormats.Bitmap))
		{
			mimeTypes.Add(ImageMimeTypes[0]);
		}
		if (formats.Contains(StandardDataFormats.Html))
		{
			mimeTypes.Add(HtmlMimeType);
		}
		if (formats.Contains(StandardDataFormats.Text) || formats.Contains(StandardDataFormats.Uri) || formats.Contains(StandardDataFormats.WebLink))
		{
			mimeTypes.AddRange(TextMimeTypes);
		}
		// Custom formats that already are MIME types are passed through as raw data.
		foreach (var format in formats)
		{
			if (format.Contains('/') && !mimeTypes.Contains(format))
			{
				mimeTypes.Add(format);
			}
		}
		return mimeTypes;
	}

	/// <summary>Serializes the content of <paramref name="view"/> for <paramref name="mimeType"/>; null if it has no such content.</summary>
	internal static async Task<byte[]?> GetBytesAsync(DataPackageView view, string mimeType)
	{
		var formats = view.AvailableFormats;
		if (formats.Contains(mimeType))
		{
			return await view.GetDataAsync(mimeType) switch
			{
				byte[] bytes => bytes,
				string text => await EncodeAsync(text, Encoding.UTF8),
				_ => null,
			};
		}
		if (mimeType is WaylandFileUris.UriListMimeType or WaylandFileUris.GnomeCopiedFilesMimeType && formats.Contains(StandardDataFormats.StorageItems))
		{
			var paths = (await view.GetStorageItemsAsync()).Select(item => item.Path).Where(path => !string.IsNullOrEmpty(path));
			var localPaths = paths.ToArray();
			return await Task.Run(() => Encoding.UTF8.GetBytes(mimeType == WaylandFileUris.UriListMimeType
				? WaylandFileUris.FormatUriList(localPaths)
				: WaylandFileUris.FormatGnomeCopiedFiles(localPaths)));
		}
		if (mimeType == ImageMimeTypes[0] && formats.Contains(StandardDataFormats.Bitmap))
		{
			var reference = await view.GetBitmapAsync();
			return await Task.Run(() => GetPngAsync(reference));
		}
		if (mimeType == HtmlMimeType && formats.Contains(StandardDataFormats.Html))
		{
			return await EncodeAsync(await view.GetHtmlFormatAsync(), Encoding.UTF8);
		}
		if (TextMimeTypes.Contains(mimeType))
		{
			var text = formats.Contains(StandardDataFormats.Text) ? await view.GetTextAsync()
				: formats.Contains(StandardDataFormats.Uri) ? (await view.GetUriAsync()).ToString()
				: formats.Contains(StandardDataFormats.WebLink) ? (await view.GetWebLinkAsync()).ToString()
				: null;
			return text is null ? null : await EncodeAsync(text, mimeType == "STRING" ? Encoding.Latin1 : Encoding.UTF8);
		}
		return null;
	}

	private static Task<byte[]> EncodeAsync(string text, Encoding encoding)
		=> Task.Run(() => encoding.GetBytes(text));

	private static async Task<byte[]?> GetPngAsync(RandomAccessStreamReference reference)
	{
		// References created from a stream hand out that same stream on every open, so it is rewound and left
		// open for the package owner.
		var stream = await reference.OpenReadAsync();
		using var ownedStream = reference.OwnsOpenedStream ? stream : null;
		stream.Seek(0);
		using var buffer = new MemoryStream();
		await stream.AsStreamForRead().CopyToAsync(buffer);
		var bytes = buffer.ToArray();
		if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']))
		{
			return bytes;
		}
		using var bitmap = SKBitmap.Decode(bytes);
		using var encoded = bitmap?.Encode(SKEncodedImageFormat.Png, 100);
		return encoded?.ToArray();
	}

	/// <summary>Adds the offered content to <paramref name="package"/>; data is received only when requested.</summary>
	internal static void Fill(DataPackage package, WaylandDataOffer offer)
	{
		var mimeTypes = offer.MimeTypes;
		foreach (var mimeType in mimeTypes)
		{
			package.SetDataProvider(mimeType, async _ => (await offer.ReadAsync(mimeType))!);
		}
		if (TextMimeTypes.FirstOrDefault(mimeTypes.Contains) is { } textMimeType)
		{
			var encoding = textMimeType == "STRING" ? Encoding.Latin1 : Encoding.UTF8;
			package.SetDataProvider(StandardDataFormats.Text, async _ => (await offer.ReadAsync(textMimeType) is { } bytes ? encoding.GetString(bytes).TrimEnd('\0') : null)!);
		}
		if (mimeTypes.Contains(HtmlMimeType))
		{
			package.SetDataProvider(StandardDataFormats.Html, async _ => (await offer.ReadAsync(HtmlMimeType) is { } bytes ? Encoding.UTF8.GetString(bytes) : null)!);
		}
		if (mimeTypes.Contains(WaylandFileUris.UriListMimeType) || mimeTypes.Contains(WaylandFileUris.GnomeCopiedFilesMimeType))
		{
			package.SetDataProvider(StandardDataFormats.StorageItems, async _ => await ReadStorageItemsAsync(offer, mimeTypes));
		}
		if (ImageMimeTypes.FirstOrDefault(mimeTypes.Contains) is { } imageMimeType)
		{
			package.SetDataProvider(StandardDataFormats.Bitmap, async _ => (await offer.ReadAsync(imageMimeType) is { } bytes
				? RandomAccessStreamReference.CreateFromStream(new MemoryStream(bytes).AsRandomAccessStream())
				: null)!);
		}
	}

	private static async Task<object> ReadStorageItemsAsync(WaylandDataOffer offer, IReadOnlyList<string> mimeTypes)
	{
		var paths = mimeTypes.Contains(WaylandFileUris.UriListMimeType)
			? await offer.ReadAsync(WaylandFileUris.UriListMimeType) is { } uriList ? WaylandFileUris.ParseUriList(Encoding.UTF8.GetString(uriList)) : []
			: await offer.ReadAsync(WaylandFileUris.GnomeCopiedFilesMimeType) is { } copied ? WaylandFileUris.ParseGnomeCopiedFiles(Encoding.UTF8.GetString(copied)) : [];
		var items = new List<IStorageItem>(paths.Count);
		foreach (var path in paths)
		{
			if (Directory.Exists(path))
			{
				items.Add(new StorageFolder(path));
			}
			else if (File.Exists(path))
			{
				items.Add(StorageFile.GetFileFromPath(path));
			}
		}
		return items;
	}

	internal static DataPackageOperation ToOperations(uint dndActions)
		=> ((dndActions & DndActionCopy) != 0 ? DataPackageOperation.Copy : DataPackageOperation.None)
			| ((dndActions & DndActionMove) != 0 ? DataPackageOperation.Move : DataPackageOperation.None);

	internal static uint ToDndActions(DataPackageOperation operations)
		=> ((operations & DataPackageOperation.Copy) != 0 ? DndActionCopy : 0)
			| ((operations & DataPackageOperation.Move) != 0 ? DndActionMove : 0);
}
