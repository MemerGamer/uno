#nullable enable

using System;

namespace Uno.UI.Runtime.Wayland;

internal sealed record WaylandWindowConfiguration(
	long Generation, WaylandDecorationLayout Layout, double Scale, uint PreferredScale, int IntegerScale,
	WaylandToplevelStates States, string Title, bool NotifyActivation, uint? Serial, long SerialSequence);

// Keep the applied configure stable until its frame is consumed; later arrivals occupy a single pending slot.
internal sealed class WaylandConfigureScheduler(Action<Action> postToUI, Action<WaylandWindowConfiguration> apply)
{
	private readonly object _gate = new();
	private WaylandWindowConfiguration? _pending;
	private WaylandWindowConfiguration? _current;
	private bool _posted;
	private bool _inFlight;
	private long _acknowledgedSequence;

	internal WaylandWindowConfiguration? Current
	{
		get
		{
			lock (_gate)
			{
				return _current;
			}
		}
	}

	internal void Enqueue(WaylandWindowConfiguration configuration)
	{
		bool post;
		lock (_gate)
		{
			_pending = configuration with { NotifyActivation = configuration.NotifyActivation || _pending?.NotifyActivation == true };
			post = Schedule();
		}
		if (post)
		{
			postToUI(ApplyPending);
		}
	}

	private bool Schedule()
	{
		if (!_posted && !_inFlight && _pending is not null)
		{
			_posted = true;
			return true;
		}
		return false;
	}

	private void ApplyPending()
	{
		WaylandWindowConfiguration configuration;
		lock (_gate)
		{
			_posted = false;
			configuration = _pending!;
			_pending = null;
			_current = configuration;
			_inFlight = true;
		}
		apply(configuration);
	}

	internal void Complete(long generation)
	{
		bool post;
		lock (_gate)
		{
			if (!_inFlight || _current?.Generation != generation)
			{
				return;
			}
			_inFlight = false;
			post = Schedule();
		}
		if (post)
		{
			postToUI(ApplyPending);
		}
	}

	// Sequence numbers follow receipt order, independent of uint serial wraparound and local scale changes.
	internal void Acknowledge(WaylandWindowConfiguration configuration, Action<uint> acknowledge)
	{
		lock (_gate)
		{
			if (configuration.Serial is { } serial && configuration.SerialSequence > _acknowledgedSequence)
			{
				acknowledge(serial);
				_acknowledgedSequence = configuration.SerialSequence;
			}
		}
	}
}
