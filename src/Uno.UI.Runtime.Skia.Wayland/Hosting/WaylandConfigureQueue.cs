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

// Serialized by the display's SyncRoot. Apply only the newest successor at each frame opportunity,
// never while a draw/commit attempt is using the current configure.
internal sealed class WaylandConfigureQueue
{
	private WaylandWindowConfigure? _pending;
	private bool _awaitingFrame;
	private bool _attemptActive;
	private bool _opportunityDeferred;
	private bool _posted;
	private bool _applyDeferred;
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
		if (!_posted || _awaitingFrame)
		{
			return null;
		}
		_posted = false;
		_opportunityDeferred = false;
		_applyDeferred = false;
		Current = _pending;
		_pending = null;
		_awaitingFrame = Current is not null;
		return Current;
	}

	// UI thread: keep the posted successor until the UI has recorded the current configure's layout.
	// Applying it mid-layout would restart the layout at yet another size.
	internal bool DeferApply()
	{
		if (!_posted || _awaitingFrame)
		{
			return false;
		}
		_applyDeferred = true;
		return true;
	}

	// UI thread, once the layout was recorded. Returns whether the deferred successor should now be applied.
	internal bool ResumeDeferredApply()
	{
		if (!_applyDeferred)
		{
			return false;
		}
		_applyDeferred = false;
		return _posted && !_awaitingFrame;
	}

	internal bool FrameOpportunity(long generation)
	{
		if (!_awaitingFrame || Current?.Generation != generation)
		{
			return false;
		}
		if (_attemptActive)
		{
			// Superseding Current mid-draw would reject the frame; the attempt's end releases the slot.
			_opportunityDeferred = true;
			return false;
		}
		_awaitingFrame = false;
		return Schedule();
	}

	// Brackets a draw/commit attempt, which keeps its configure until it finishes. Returns whether
	// a deferred opportunity now releases a pending successor.
	internal bool SetAttemptActive(bool active)
	{
		_attemptActive = active;
		if (active || !_opportunityDeferred)
		{
			return false;
		}
		_opportunityDeferred = false;
		_awaitingFrame = false;
		return Schedule();
	}

	internal bool CanAcknowledgeLatest(WaylandDecorationLayout layout, double scale)
		=> Current is null || (Current.Layout == layout && Current.Scale == scale);

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
		if (_awaitingFrame || _posted || _pending is null)
		{
			return false;
		}
		_posted = true;
		return true;
	}
}
