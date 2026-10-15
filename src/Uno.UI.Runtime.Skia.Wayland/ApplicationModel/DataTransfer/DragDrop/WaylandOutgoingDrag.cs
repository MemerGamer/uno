// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.UI.Xaml;
using Uno.Foundation.Logging;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop.Core;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// One drag handed to the compositor through wl_data_device.start_drag, from the moment Uno's drag left the
// window until the data source reports its outcome. Started on the UI thread; the source lives on the dispatch thread.
internal sealed class WaylandOutgoingDrag
{
	private readonly WaylandDataDevice _device;
	private readonly WaylandXamlRootHost _host;
	private readonly Action<DataPackageOperation> _onCompleted;
	private readonly WaylandDragSourceState _state = new();
	// Dispatch thread.
	private WlDataSource? _source;
	private WaylandDataSourceContent? _content;
	private WaylandDragIcon? _icon;
	private bool _cancelled;
	private int _completed;

	internal WaylandOutgoingDrag(WaylandDataDevice device, WaylandXamlRootHost host, XamlRoot xamlRoot, CoreDragInfo info, Action<DataPackageOperation> onCompleted)
	{
		_device = device;
		_host = host;
		XamlRoot = xamlRoot;
		Info = info;
		_onCompleted = onCompleted;
	}

	internal XamlRoot XamlRoot { get; }
	internal CoreDragInfo Info { get; }
	internal bool IsCompleted => Volatile.Read(ref _completed) != 0;

	// UI thread. The data is read while the view is current; the compositor handshake follows on the dispatch thread.
	internal async void Start()
	{
		try
		{
			var view = Info.Data;
			var mimeTypes = WaylandDataFormats.GetDragMimeTypes(view);
			// Link has no Wayland action; a drag with nothing else to offer is still a copy for the target.
			var actions = WaylandDataFormats.ToDndActions(Info.AllowedOperations);
			if (actions == 0)
			{
				actions = WaylandDataFormats.DndActionCopy;
			}
			var scale = XamlRoot.RasterizationScale;
			var image = await WaylandDragIcon.CaptureAsync(Info);
			_device.Display.Post(() => StartOnDispatch(view, mimeTypes, actions, image, scale));
		}
		catch (Exception error)
		{
			if (this.Log().IsEnabled(LogLevel.Error))
			{
				this.Log().Error("Failed to start the native drag.", error);
			}
			_device.Display.Post(() =>
			{
				if (_state.Cancelled())
				{
					Complete();
				}
			});
		}
	}

	private void StartOnDispatch(DataPackageView view, List<string> mimeTypes, uint actions, WaylandDragIconImage? image, double scale)
	{
		if (_cancelled)
		{
			return;
		}
		try
		{
			if (_host.Input?.GrabSerial is not { } serial)
			{
				// Without a press to convert the compositor would ignore the request; Uno's internal drag goes on.
				if (this.Log().IsEnabled(LogLevel.Debug))
				{
					this.Log().Debug("No implicit grab serial; the drag stays internal.");
				}
				Abandon();
				return;
			}
			var source = _device.CreateSource();
			_source = source;
			foreach (var mimeType in mimeTypes)
			{
				source.Offer(mimeType);
			}
			if (source.Version >= 3)
			{
				source.SetActions(actions);
			}
			var content = new WaylandDataSourceContent(_device, view, "drag");
			_content = content;
			source.Target += (_, e) => _state.Target(e.MimeType);
			source.Action += (_, e) => _state.SetAction(e.DndAction);
			source.Send += (_, e) =>
			{
				_state.Sent();
				content.Send(e.MimeType, e.Fd);
			};
			source.DndDropPerformed += (_, _) => _state.DropPerformed();
			source.DndFinished += (_, _) =>
			{
				if (_state.Finished())
				{
					Complete();
				}
			};
			source.Cancelled += (_, _) =>
			{
				if (_state.Cancelled())
				{
					Complete();
				}
			};
			try
			{
				_icon = WaylandDragIcon.Create(_device.Connection, image, scale);
			}
			catch (Exception error)
			{
				if (this.Log().IsEnabled(LogLevel.Warning))
				{
					this.Log().Warn("The drag icon could not be created; dragging without one.", error);
				}
			}
			_host.PointerSource.BeginNativeDrag();
			_device.StartDrag(source, _host.Surface, _icon?.Surface, serial);
			_icon?.Present();
			if (this.Log().IsEnabled(LogLevel.Trace))
			{
				this.Log().Trace($"Native drag started with actions {actions} and formats {string.Join(", ", mimeTypes)}.");
			}
		}
		catch (Exception error)
		{
			if (this.Log().IsEnabled(LogLevel.Error))
			{
				this.Log().Error("Failed to start the native drag.", error);
			}
			if (_state.Cancelled())
			{
				Complete();
			}
		}
	}

	// UI thread: a newer drag or the closing of the originating window ends this one as cancelled.
	internal void Cancel() => _device.Display.Post(() =>
	{
		_cancelled = true;
		if (_state.Cancelled())
		{
			Complete();
		}
	});

	// Dispatch thread. The internal drag keeps running; only the registration is dropped.
	private void Abandon()
	{
		if (Interlocked.Exchange(ref _completed, 1) != 0)
		{
			return;
		}
		_device.PostToUI(() =>
		{
			if (_device.OutgoingDrag == this)
			{
				_device.OutgoingDrag = null;
			}
		});
	}

	// Dispatch thread, once per drag.
	private void Complete()
	{
		if (Interlocked.Exchange(ref _completed, 1) != 0)
		{
			return;
		}
		var result = WaylandDataFormats.ToOperations(_state.Result);
		if (this.Log().IsEnabled(LogLevel.Trace))
		{
			this.Log().Trace($"Native drag {_state.Phase} with action {_state.Result} ({result}).");
		}
		_icon?.Dispose();
		_icon = null;
		_content?.Dispose();
		_content = null;
		_source?.Dispose();
		_source = null;
		_device.PostToUI(() =>
		{
			if (_device.OutgoingDrag == this)
			{
				_device.OutgoingDrag = null;
			}
			_onCompleted(result);
		});
		// Queued after the completion so the drop result precedes the capture loss.
		_host.PointerSource.EndNativeDrag();
	}
}
