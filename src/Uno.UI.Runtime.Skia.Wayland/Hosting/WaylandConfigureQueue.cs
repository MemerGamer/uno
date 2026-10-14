// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal sealed record WaylandWindowConfigure(
	long Generation,
	WaylandDecorationLayout Layout,
	double Scale,
	uint PreferredScale,
	int IntegerScale,
	WaylandToplevelStates States,
	bool Configured,
	bool NotifyActivation,
	long SerialSequence,
	uint? Serial);

// Serialized by the display's SyncRoot. Keep applied geometry until submission, and only the newest successor.
internal sealed class WaylandConfigureQueue
{
	private WaylandWindowConfigure? _pending;
	private bool _inFlight;
	private bool _posted;
	private long _generation;
	private long _serialSequence;
	private long _ackedSequence;
	private uint? _serial;

	internal WaylandWindowConfigure? Current { get; private set; }

	internal void Receive(uint serial)
	{
		_serial = serial;
		_serialSequence++;
	}

	internal bool Enqueue(WaylandDecorationLayout layout, double scale, uint preferredScale, int integerScale, WaylandToplevelStates states, bool configured, bool notifyActivation = false)
	{
		_pending = new(++_generation, layout, scale, preferredScale, integerScale, states, configured,
			notifyActivation || _pending?.NotifyActivation == true, _serialSequence, _serial);
		return Schedule();
	}

	internal WaylandWindowConfigure? TakePending()
	{
		if (!_posted || _inFlight)
		{
			return null;
		}
		_posted = false;
		Current = _pending;
		_pending = null;
		_inFlight = Current is not null;
		return Current;
	}

	internal bool Complete(long generation)
	{
		if (!_inFlight || Current?.Generation != generation)
		{
			return false;
		}
		_inFlight = false;
		return Schedule();
	}

	internal bool CanAcknowledgeLatest(WaylandDecorationLayout layout, double scale)
		=> !_inFlight || (Current is { } current && current.Layout == layout && current.Scale == scale);

	internal void AcknowledgeLatest(Action<uint> acknowledge) => Acknowledge(_serialSequence, _serial, acknowledge);

	internal void AcknowledgeCurrent(Action<uint> acknowledge)
	{
		if (Current is { } current)
		{
			Acknowledge(current.SerialSequence, current.Serial, acknowledge);
		}
	}

	private void Acknowledge(long sequence, uint? serial, Action<uint> acknowledge)
	{
		// Receipt order, rather than numeric serial order, also handles the uint wraparound.
		if (sequence > _ackedSequence && serial is { } value)
		{
			acknowledge(value);
			_ackedSequence = sequence;
		}
	}

	private bool Schedule()
	{
		if (_inFlight || _posted || _pending is null)
		{
			return false;
		}
		_posted = true;
		return true;
	}
}
