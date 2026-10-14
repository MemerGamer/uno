#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Uno.ApplicationModel.DataTransfer;
using Uno.Foundation.Logging;
using Uno.UI.Runtime.Wayland.Native;
using Uno.UI.Runtime.Wayland.Protocols;
using Windows.ApplicationModel.DataTransfer;

namespace Uno.UI.Runtime.Wayland;

// The CLIPBOARD selection through wl_data_device. Wayland only lets the client with keyboard focus set the
// selection, and only announces selection changes to it, so ContentChanged is raised while a window is focused.
internal sealed class WaylandClipboardExtension : IClipboardExtension
{
	// Data production and writing each have a bounded deadline.
	private static readonly TimeSpan _writeTimeout = TimeSpan.FromSeconds(5);

	private readonly WaylandDataDevice _device;
	private readonly object _gate = new();
	// Guarded by _gate: the content we offer, valid while our source is the selection.
	private DataPackageView? _content;
	private int _generation;
	// Dispatch thread.
	private WlDataSource? _source;
	private CancellationTokenSource? _sourceCancellation;
	private volatile bool _isMonitoringEnabled;

	internal WaylandClipboardExtension(WaylandDataDevice device)
	{
		_device = device;
		device.SelectionChanged += (_, _) =>
		{
			if (_isMonitoringEnabled)
			{
				_device.PostToUI(() => ContentChanged?.Invoke(this, EventArgs.Empty));
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
			// Readable locally right away, as on WinUI, even while publishing waits for keyboard focus.
			_content = view;
			generation = ++_generation;
		}
		var mimeTypes = view is null ? [] : WaylandDataFormats.GetMimeTypes(view);
		// Wayland only accepts a selection from the focused client with a recent input serial.
		_device.Display.Post(() => _device.RequestSelection(() =>
		{
			var previous = _source;
			_sourceCancellation?.Cancel();
			_sourceCancellation?.Dispose();
			_sourceCancellation = null;
			_source = null;
			if (view is not null)
			{
				var source = _device.CreateSource();
				foreach (var mimeType in mimeTypes)
				{
					source.Offer(mimeType);
				}
				// Each format is produced once per content, however many clients paste it.
				var cancellation = new CancellationTokenSource();
				_sourceCancellation = cancellation;
				var token = cancellation.Token;
				var data = new Dictionary<string, Task<byte[]?>>();
				source.Send += (_, e) => Send(view, data, e.MimeType, e.Fd, token);
				source.Cancelled += (_, _) =>
				{
					cancellation.Cancel();
					// Another client (or a newer SetContent) took the selection.
					if (_source == source)
					{
						_source = null;
						// Unfocused clients get no selection events, but this one is newer than a still deferred copy.
						_device.CancelPendingSelection();
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
		}, () =>
		{
			lock (_gate)
			{
				if (_generation == generation)
				{
					_content = null;
				}
			}
		}));
	}

	// Providers run on the UI dispatcher; pipe ownership stays with the worker.
	private void Send(DataPackageView view, Dictionary<string, Task<byte[]?>> cache, string mimeType, int fd, CancellationToken cancellationToken)
	{
		if (!cache.TryGetValue(mimeType, out var data))
		{
			var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
			cache[mimeType] = data = completion.Task;
			try
			{
				// Not an async lambda: a failure must reach the completion rather than the UI dispatcher.
				_device.PostToUI(() => _ = ProduceAsync(view, mimeType, completion, cancellationToken));
			}
			catch (Exception error)
			{
				completion.TrySetException(error);
			}
		}
		_ = SendAsync(fd, data, mimeType, cancellationToken);
	}

	private async Task ProduceAsync(DataPackageView view, string mimeType, TaskCompletionSource<byte[]?> completion, CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			completion.TrySetCanceled(cancellationToken);
			return;
		}
		try
		{
			completion.TrySetResult(await GetBytesAsync(view, mimeType));
		}
		catch (Exception error)
		{
			completion.TrySetException(error);
		}
	}

	private async Task SendAsync(int fd, Task<byte[]?> data, string mimeType, CancellationToken cancellationToken)
	{
		try
		{
			if (!await WaylandDataTransfer.SendAsync(fd, data, _writeTimeout, cancellationToken)
				&& this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"The clipboard reader stopped receiving {mimeType}.");
			}
		}
		catch (OperationCanceledException)
		{
			// A newer selection replaced this one.
		}
		catch (Exception error)
		{
			if (this.Log().IsEnabled(LogLevel.Warning))
			{
				this.Log().Warn($"Clipboard transfer failed for {mimeType}.", error);
			}
		}
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
