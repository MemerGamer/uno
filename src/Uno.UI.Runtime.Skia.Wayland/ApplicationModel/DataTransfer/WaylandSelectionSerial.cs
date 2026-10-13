// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// Dispatch thread. Tracks keyboard focus and the latest input serial that wl_data_device.set_selection
// requires, and holds the newest selection request until both are available. Any serial value, including
// zero after wrapping, is valid once focus was entered.
internal sealed class WaylandSelectionSerial
{
	private Action? _pendingSelection;

	internal bool HasFocus { get; private set; }
	internal uint Serial { get; private set; }

	internal void Enter(uint serial)
	{
		HasFocus = true;
		Serial = serial;
		PublishPendingSelection();
	}

	internal void Update(uint serial)
	{
		if (HasFocus)
		{
			Serial = serial;
			PublishPendingSelection();
		}
	}

	internal void Request(Action publish)
	{
		_pendingSelection = publish;
		PublishPendingSelection();
	}

	private void PublishPendingSelection()
	{
		if (HasFocus && _pendingSelection is { } publish)
		{
			_pendingSelection = null;
			publish();
		}
	}

	internal void Leave() => HasFocus = false;
}
