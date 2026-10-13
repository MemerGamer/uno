// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Uno.ApplicationModel.DataTransfer;
using Uno.Foundation.Logging;
using Uno.WinUI.Runtime.Skia.Wayland.Native;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;
using Windows.ApplicationModel.DataTransfer;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// The CLIPBOARD selection through wl_data_device. Wayland only lets the client with keyboard focus set the
// selection, and only announces selection changes to it, so ContentChanged is raised while a window is focused.
internal sealed class WaylandClipboardExtension : IClipboardExtension
{
	// A reader that stops consuming data for this long is abandoned.
	private static readonly TimeSpan _writeTimeout = TimeSpan.FromSeconds(5);

	private readonly WaylandDataDevice _device;
	private readonly object _gate = new();
	// Guarded by _gate: the content we offer, valid while our source is the selection.
	private DataPackageView? _content;
	private int _generation;
	// Dispatch thread.
	private WlDataSource? _source;
	private volatile bool _isMonitoringEnabled;

	internal WaylandClipboardExtension(WaylandDataDevice device)
	{
		_device = device;
		device.SelectionChanged += () =>
		{
			if (_isMonitoringEnabled)
			{
				WaylandConnection.PostToUI(() => ContentChanged?.Invoke(this, EventArgs.Empty));
			}
		};
	}

	public event EventHandler<object>? ContentChanged;

	public void StartContentChanged() => _isMonitoringEnabled = true;

	public void StopContentChanged() => _isMonitoringEnabled = false;

	// Nothing to persist: the compositor keeps no copy, and clipboard managers fetch the data themselves.
	public void Flush() { }

	public void Clear() => SetSelection(null);

	public void SetContent(DataPackage content) => SetSelection(content.GetView());

	private void SetSelection(DataPackageView? view)
	{
		int generation;
		lock (_gate)
		{
			_content = view;
			generation = ++_generation;
		}
		var mimeTypes = view is null ? [] : WaylandDataFormats.GetMimeTypes(view);
		_device.Display.Post(() =>
		{
			var previous = _source;
			_source = null;
			if (view is not null)
			{
				var source = _device.CreateSource();
				foreach (var mimeType in mimeTypes)
				{
					source.Offer(mimeType);
				}
				// Each format is produced once per content, however many clients paste it.
				var data = new Dictionary<string, Task<byte[]?>>();
				source.Send += (_, e) => Send(view, data, e.MimeType, e.Fd);
				source.Cancelled += (_, _) =>
				{
					// Another client (or a newer SetContent) took the selection.
					if (_source == source)
					{
						_source = null;
						lock (_gate)
						{
							if (_generation == generation)
							{
								_content = null;
							}
						}
					}
					source.Destroy();
				};
				_source = source;
			}
			_device.SetSelection(_source);
			previous?.Destroy();
		});
	}

	// Dispatch thread. The data is produced and written on a worker; the descriptor is ours to close.
	private void Send(DataPackageView view, Dictionary<string, Task<byte[]?>> cache, string mimeType, int fd)
	{
		if (!cache.TryGetValue(mimeType, out var data))
		{
			cache[mimeType] = data = Task.Run(() => GetBytesAsync(view, mimeType));
		}
		_ = data.ContinueWith(
			task =>
			{
				if (!WaylandPipe.WriteAll(fd, task.Result ?? [], _writeTimeout) && this.Log().IsEnabled(LogLevel.Debug))
				{
					this.Log().Debug($"The clipboard reader stopped receiving {mimeType}.");
				}
			},
			CancellationToken.None,
			TaskContinuationOptions.None,
			TaskScheduler.Default);
	}

	private async Task<byte[]?> GetBytesAsync(DataPackageView view, string mimeType)
	{
		try
		{
			return await WaylandDataFormats.GetBytesAsync(view, mimeType);
		}
		catch (Exception error)
		{
			if (this.Log().IsEnabled(LogLevel.Error))
			{
				this.Log().Error($"Failed to produce clipboard data for {mimeType}.", error);
			}
			return null;
		}
	}

	public DataPackageView GetContent()
	{
		lock (_gate)
		{
			if (_content is { } content)
			{
				return content;
			}
		}

		var package = new DataPackage();
		if (_device.Selection is { } offer)
		{
			WaylandDataFormats.Fill(package, offer);
		}
		return package.GetView();
	}
}
