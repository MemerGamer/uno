// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Uno.Foundation.Logging;
using Uno.WinUI.Runtime.Skia.Wayland.Native;
using Windows.ApplicationModel.DataTransfer;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// Serves wl_data_source.send for one DataPackageView, for the selection and for drags. Each format is
// produced once on the UI thread however many clients read it; pipe ownership stays with the writer.
// Send runs on the dispatch thread.
internal sealed class WaylandDataSourceContent : IDisposable
{
	// Data production and writing each have a bounded deadline.
	private static readonly TimeSpan _writeTimeout = TimeSpan.FromSeconds(5);

	private readonly WaylandDataDevice _device;
	private readonly DataPackageView _view;
	private readonly string _purpose;
	private readonly Dictionary<string, Task<byte[]?>> _cache = new();
	private readonly CancellationTokenSource _cancellation = new();

	internal WaylandDataSourceContent(WaylandDataDevice device, DataPackageView view, string purpose)
	{
		_device = device;
		_view = view;
		_purpose = purpose;
	}

	internal void Send(string mimeType, int fd)
	{
		var token = _cancellation.Token;
		if (!_cache.TryGetValue(mimeType, out var data))
		{
			var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
			_cache[mimeType] = data = completion.Task;
			try
			{
				_device.PostToUI(async () =>
				{
					if (token.IsCancellationRequested)
					{
						completion.TrySetCanceled(token);
						return;
					}
					completion.TrySetResult(await GetBytesAsync(mimeType));
				});
			}
			catch (Exception error)
			{
				completion.TrySetException(error);
			}
		}
		_ = SendAsync(fd, data, mimeType, token);
	}

	private async Task SendAsync(int fd, Task<byte[]?> data, string mimeType, CancellationToken cancellationToken)
	{
		try
		{
			if (!await WaylandDataTransfer.SendAsync(fd, data, _writeTimeout, cancellationToken)
				&& this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"The {_purpose} reader stopped receiving {mimeType}.");
			}
		}
		catch (Exception error)
		{
			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"{_purpose} transfer failed for {mimeType}: {error.Message}");
			}
		}
	}

	private async Task<byte[]?> GetBytesAsync(string mimeType)
	{
		try
		{
			return await WaylandDataFormats.GetBytesAsync(_view, mimeType);
		}
		catch (Exception error)
		{
			if (this.Log().IsEnabled(LogLevel.Error))
			{
				this.Log().Error($"Failed to produce {_purpose} data for {mimeType}.", error);
			}
			return null;
		}
	}

	// Stops producing and writing; readers of an abandoned source get a closed pipe.
	public void Dispose()
	{
		_cancellation.Cancel();
		_cancellation.Dispose();
	}
}
