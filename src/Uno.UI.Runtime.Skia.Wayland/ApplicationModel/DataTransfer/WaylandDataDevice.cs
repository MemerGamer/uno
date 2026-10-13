// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
using Windows.Foundation;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// The seat's wl_data_device, shared by the clipboard and every window's drop target.
// Protocol events arrive on the dispatch thread; drag-and-drop events are forwarded to the UI thread in order.
internal sealed class WaylandDataDevice
{
	private readonly WaylandConnection _connection;
	private readonly WlDataDeviceManager _manager;
	private readonly WlDataDevice _device;

	// Dispatch thread: offers announced by data_offer and not yet claimed by selection or enter.
	private readonly Dictionary<WlDataOffer, WaylandDataOffer> _newOffers = new();
	// Written on the dispatch thread, read under SyncRoot.
	private WaylandDataOffer? _selection;

	// UI thread.
	private readonly List<WaylandDragDropExtension> _dropTargets = new();
	private WaylandDragDropExtension? _dropTarget;

	private WaylandDataDevice(WaylandConnection connection, WlDataDeviceManager manager, WlSeat seat)
	{
		_connection = connection;
		_manager = manager;
		_device = manager.GetDataDevice(seat);
		_device.DataOffer += (_, e) => _newOffers[e.Id] = new WaylandDataOffer(e.Id);
		_device.Selection += OnSelection;
		_device.Enter += OnEnter;
		_device.Motion += (_, e) =>
		{
			var position = new Point(e.X.ToDouble(), e.Y.ToDouble());
			WaylandConnection.PostToUI(() => _dropTarget?.Motion(position));
		};
		_device.Leave += (_, _) => WaylandConnection.PostToUI(() =>
		{
			_dropTarget?.Leave();
			_dropTarget = null;
		});
		_device.Drop += (_, _) => WaylandConnection.PostToUI(() =>
		{
			_dropTarget?.Drop();
			_dropTarget = null;
		});
	}

	/// <summary>Creates the data device when the compositor has a seat and wl_data_device_manager.</summary>
	internal static WaylandDataDevice? Create(WaylandConnection connection)
	{
		var display = connection.Display;
		lock (display.SyncRoot)
		{
			if (connection.Seat is not { } seat || display.GetGlobal(WlDataDeviceManager.ProtocolName) is null)
			{
				return null;
			}
			// v3 adds drag-and-drop actions and wl_data_offer.finish.
			var manager = (WlDataDeviceManager)display.Bind(WlDataDeviceManager.ProtocolName, 3);
			return new WaylandDataDevice(connection, manager, seat);
		}
	}

	internal WaylandDisplay Display => _connection.Display;

	/// <summary>Raised on the dispatch thread when the selection changes while one of our surfaces has keyboard focus.</summary>
	internal event Action? SelectionChanged;

	internal WaylandDataOffer? Selection
	{
		get
		{
			lock (Display.SyncRoot)
			{
				return _selection;
			}
		}
	}

	// Dispatch thread.
	internal WlDataSource CreateSource() => _manager.CreateDataSource();

	// Dispatch thread. A null source clears the selection.
	internal void SetSelection(WlDataSource? source) => _device.SetSelection(source, _connection.InputSerial);

	// UI thread.
	internal void AddDropTarget(WaylandDragDropExtension target) => _dropTargets.Add(target);

	// UI thread.
	internal void RemoveDropTarget(WaylandDragDropExtension target) => _dropTargets.Remove(target);

	private void OnSelection(object? sender, WlDataDevice.SelectionEventArgs e)
	{
		_selection?.Destroy();
		_selection = e.Id is { } id ? Claim(id) : null;
		SelectionChanged?.Invoke();
	}

	private void OnEnter(object? sender, WlDataDevice.EnterEventArgs e)
	{
		// A drag without a source (a client-local drag of another client) carries no offer and no data.
		var offer = e.Id is { } id ? Claim(id) : null;
		var surface = e.Surface;
		var serial = e.Serial;
		var position = new Point(e.X.ToDouble(), e.Y.ToDouble());
		WaylandConnection.PostToUI(() =>
		{
			_dropTarget = offer is null ? null : _dropTargets.Find(target => target.Surface == surface);
			if (_dropTarget is { } target)
			{
				target.Enter(offer!, serial, position);
			}
			else
			{
				offer?.Destroy();
			}
		});
	}

	private WaylandDataOffer? Claim(WlDataOffer proxy)
		=> _newOffers.Remove(proxy, out var offer) ? offer : null;
}
