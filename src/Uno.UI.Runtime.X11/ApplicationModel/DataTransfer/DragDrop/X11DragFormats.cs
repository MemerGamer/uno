// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Uno.UI.Composition.Drawing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Uno.UI.Runtime.X11;

// Formats exported by an XDND selection owner.
internal static class X11DragFormats
{
	// In order of preference when reading. UTF8_STRING, TEXT and STRING are the X11 targets that XWayland clients use.
	internal static readonly string[] TextMimeTypes = ["UTF8_STRING", "text/plain;charset=utf-8", "text/plain", "STRING", "TEXT"];
	internal const string HtmlMimeType = "text/html";
	internal static readonly string[] ImageMimeTypes = ["image/png", "image/jpeg"];

	/// <summary>
	/// The MIME types a drag of <paramref name="view"/> offers. Unlike the clipboard it carries no cut/copy verb,
	/// and a file list is also readable as text so terminals and editors can take the paths.
	/// </summary>
	internal static List<string> GetDragMimeTypes(DataPackageView view)
	{
		var formats = view.AvailableFormats;
		var mimeTypes = new List<string>();
		var hasFiles = formats.Contains(StandardDataFormats.StorageItems) || formats.Contains(X11FileUris.UriListMimeType);
		if (hasFiles)
		{
			mimeTypes.Add(X11FileUris.UriListMimeType);
		}
		if (formats.Contains(StandardDataFormats.Bitmap))
		{
			mimeTypes.Add(ImageMimeTypes[0]);
		}
		if (formats.Contains(StandardDataFormats.Html))
		{
			mimeTypes.Add(HtmlMimeType);
		}
		if (hasFiles || formats.Contains(StandardDataFormats.Text) || formats.Contains(StandardDataFormats.Uri) || formats.Contains(StandardDataFormats.WebLink))
		{
			mimeTypes.AddRange(TextMimeTypes);
		}
		foreach (var format in formats)
		{
			if (format.Contains('/') && format != X11FileUris.GnomeCopiedFilesMimeType && !mimeTypes.Contains(format))
			{
				mimeTypes.Add(format);
			}
		}
		return mimeTypes;
	}

	internal static string ReplyType(string target) => target == "TEXT" ? "UTF8_STRING" : target;

	/// <summary>Serializes the content of <paramref name="view"/> for <paramref name="mimeType"/>; null if it has no such content.</summary>
	internal static async Task<byte[]?> GetBytesAsync(DataPackageView view, string mimeType)
	{
		var formats = view.AvailableFormats;
		if (formats.Contains(mimeType))
		{
			return await view.GetDataAsync(mimeType).AsTask().ConfigureAwait(false) switch
			{
				byte[] bytes => bytes,
				string text => await EncodeAsync(text, mimeType == "STRING" ? Encoding.Latin1 : Encoding.UTF8).ConfigureAwait(false),
				_ => null,
			};
		}
		if (mimeType == X11FileUris.UriListMimeType && formats.Contains(StandardDataFormats.StorageItems))
		{
			var localPaths = await GetLocalPathsAsync(view).ConfigureAwait(false);
			return await Task.Run(() => Encoding.UTF8.GetBytes(X11FileUris.FormatUriList(localPaths))).ConfigureAwait(false);
		}
		if (mimeType == ImageMimeTypes[0] && formats.Contains(StandardDataFormats.Bitmap))
		{
			var reference = await view.GetBitmapAsync().AsTask().ConfigureAwait(false);
			return await Task.Run(() => GetPngAsync(reference)).ConfigureAwait(false);
		}
		if (mimeType == HtmlMimeType && formats.Contains(StandardDataFormats.Html))
		{
			return await EncodeAsync(await view.GetHtmlFormatAsync().AsTask().ConfigureAwait(false), Encoding.UTF8).ConfigureAwait(false);
		}
		if (TextMimeTypes.Contains(mimeType))
		{
			var text = formats.Contains(StandardDataFormats.Text) ? await view.GetTextAsync().AsTask().ConfigureAwait(false)
				: formats.Contains(StandardDataFormats.Uri) ? (await view.GetUriAsync().AsTask().ConfigureAwait(false)).ToString()
				: formats.Contains(StandardDataFormats.WebLink) ? (await view.GetWebLinkAsync().AsTask().ConfigureAwait(false)).ToString()
				: await GetFileListTextAsync(view).ConfigureAwait(false);
			return text is null ? null : await EncodeAsync(text, mimeType == "STRING" ? Encoding.Latin1 : Encoding.UTF8).ConfigureAwait(false);
		}
		return null;
	}

	// A file list offered as text, for formats produced by GetDragMimeTypes.
	private static async Task<string?> GetFileListTextAsync(DataPackageView view)
	{
		var formats = view.AvailableFormats;
		if (formats.Contains(X11FileUris.UriListMimeType))
		{
			return await view.GetDataAsync(X11FileUris.UriListMimeType).AsTask().ConfigureAwait(false) switch
			{
				string text => X11FileUris.FormatUriText(X11FileUris.ParseUriList(text)),
				byte[] bytes => X11FileUris.FormatUriText(X11FileUris.ParseUriList(Encoding.UTF8.GetString(bytes))),
				_ => null,
			};
		}
		return formats.Contains(StandardDataFormats.StorageItems) ? X11FileUris.FormatUriText(await GetLocalPathsAsync(view).ConfigureAwait(false)) : null;
	}

	private static async Task<string[]> GetLocalPathsAsync(DataPackageView view)
		=> (await view.GetStorageItemsAsync().AsTask().ConfigureAwait(false)).Select(item => item.Path).Where(path => !string.IsNullOrEmpty(path)).ToArray();

	private static Task<byte[]> EncodeAsync(string text, Encoding encoding)
		=> Task.Run(() => encoding.GetBytes(text));

	private static async Task<byte[]?> GetPngAsync(RandomAccessStreamReference reference)
	{
		using var stream = await reference.OpenReadAsync().AsTask().ConfigureAwait(false);
		using var buffer = new MemoryStream();
		await stream.AsStreamForRead().CopyToAsync(buffer).ConfigureAwait(false);
		var bytes = buffer.ToArray();
		if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']))
		{
			return bytes;
		}
		using var input = new MemoryStream(bytes);
		var codec = ImageEncoderDecoder.Current;
		if (!codec.TryDecode(input, null, null, out var frames))
		{
			return null;
		}
		using (frames)
		{
			var image = frames.Frames[0];
			var pixels = new byte[checked(image.PixelWidth * image.PixelHeight * 4)];
			image.CopyPixels(pixels);
			using var output = new MemoryStream();
			codec.Encode(output, pixels, image.PixelWidth, image.PixelHeight, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, BitmapEncoderFormat.Png, 100);
			return output.ToArray();
		}
	}
}
