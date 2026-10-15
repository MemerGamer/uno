// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using Uno.ApplicationModel.DataTransfer;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;
using Windows.ApplicationModel.DataTransfer;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// The CLIPBOARD selection through wl_data_device. Wayland only lets the client with keyboard focus set the
// selection, and only announces selection changes to it, so ContentChanged is raised while a window is focused.
internal sealed class WaylandClipboardExtension : IClipboardExtension
{
	private readonly WaylandDataDevice _device;
	private readonly object _gate = new();
	// Guarded by _gate: the content we offer, valid while our source is the selection.
	private DataPackageView? _content;
	private int _generation;
	// Dispatch thread.
	private WlDataSource? _source;
	private WaylandDataSourceContent? _sourceContent;
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
			_sourceContent?.Dispose();
			_sourceContent = null;
			_source = null;
			if (view is not null)
			{
				var source = _device.CreateSource();
				foreach (var mimeType in mimeTypes)
				{
					source.Offer(mimeType);
				}
				var content = new WaylandDataSourceContent(_device, view, "clipboard");
				_sourceContent = content;
				source.Send += (_, e) => content.Send(e.MimeType, e.Fd);
				source.Cancelled += (_, _) =>
				{
					content.Dispose();
					// Another client (or a newer SetContent) took the selection.
					if (_source == source)
					{
						_source = null;
						_sourceContent = null;
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
