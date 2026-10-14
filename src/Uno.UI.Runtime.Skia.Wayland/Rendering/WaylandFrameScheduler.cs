// Licensed under the MIT License.
#nullable enable
using System;
using System.Threading;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland.Rendering;

internal enum WaylandRenderResult
{
	BufferUnavailable,
	ContentRejected,
	Presented,
	ContextRecovered,
}

// All calls run on the render owner thread; frame callbacks arrive on the dispatch thread and are posted back to it.
internal sealed class WaylandFrameScheduler : IDisposable
{
	// Compositors withhold frame callbacks (and may hold buffers) while a surface is hidden. When a pending frame
	// makes no progress for this long, the stall callback lets the UI keep producing frames without presenting them.
	internal static readonly TimeSpan StallInterval = TimeSpan.FromMilliseconds(100);

	private readonly WlSurface _surface;
	private readonly Func<WaylandRenderResult> _render;
	private readonly Action _stalled;
	private readonly Func<long> _getGeneration;
	private readonly Func<long, bool> _frameOpportunity;
	private readonly Action<bool> _attemptActive;
	private long _callbackGeneration;
	private readonly Action<Action> _postToOwner;
	private readonly Timer _stallTimer;
	private WlCallback? _callback;
	private bool _dirty;
	// Requests alone cannot resolve rejection; wait for availability, recorded content, or the fallback timer.
	private bool _waitingForAvailability;
	private bool _waitingForContentRetry;
	private bool _stallArmed;
	private int _presented;
	private int _presentedWhenArmed;
	private bool _disposed;

	public WaylandFrameScheduler(WlSurface surface, Func<WaylandRenderResult> render, Action stalled, Action<Action> postToOwner, Func<long> getGeneration, Func<long, bool> frameOpportunity, Action<bool> attemptActive)
	{
		_surface = surface;
		_render = render;
		_stalled = stalled;
		_getGeneration = getGeneration;
		_frameOpportunity = frameOpportunity;
		_attemptActive = attemptActive;
		_postToOwner = postToOwner;
		_stallTimer = new Timer(_ => _postToOwner(OnStallTimer));
	}

	public void Invalidate()
	{
		if (_disposed)
		{
			return;
		}
		_dirty = true;
		TryRender();
		if (_dirty)
		{
			ArmStallTimer();
		}
	}

	public void BufferAvailable()
	{
		_waitingForAvailability = false;
		_waitingForContentRetry = false;
		TryRender();
	}

	public void ContentReady()
	{
		_waitingForContentRetry = false;
		TryRender();
	}

	public void PresentationReady()
	{
		_waitingForAvailability = false;
		_waitingForContentRetry = false;
		Invalidate();
	}

	private void TryRender()
	{
		if (_disposed || !_dirty || _callback is not null || _waitingForAvailability || _waitingForContentRetry)
		{
			return;
		}
		_dirty = false;
		_callbackGeneration = _getGeneration();
		_callback = _surface.Frame();
		_callback.Done += OnDone;
		// The attempt keeps its configure; a timer-driven opportunity waits for the attempt to end.
		_attemptActive(true);
		try
		{
			var result = _render();
			if (result == WaylandRenderResult.Presented)
			{
				_presented++;
			}
			else
			{
				CancelCallback();
				_dirty = true;
				_waitingForAvailability = result == WaylandRenderResult.BufferUnavailable;
				_waitingForContentRetry = result == WaylandRenderResult.ContentRejected;
				if (_waitingForContentRetry)
				{
					// No frame callback can arrive; retry if neither a configure nor recorded content wakes us.
					ArmStallTimer();
				}
				if (result == WaylandRenderResult.ContextRecovered)
				{
					// A replaced context needs a fresh frame immediately.
					_postToOwner(Invalidate);
				}
			}
		}
		catch
		{
			CancelCallback();
			_dirty = true;
			throw;
		}
		finally
		{
			_attemptActive(false);
		}
	}

	private void ArmStallTimer()
	{
		if (!_stallArmed)
		{
			_stallArmed = true;
			_presentedWhenArmed = _presented;
			_stallTimer.Change(StallInterval, Timeout.InfiniteTimeSpan);
		}
	}

	private void OnStallTimer()
	{
		_stallArmed = false;
		if (_disposed || !_dirty)
		{
			return;
		}
		if (_presented != _presentedWhenArmed && !_waitingForContentRetry)
		{
			// Frames are still being presented; keep watching the pending one.
			ArmStallTimer();
			return;
		}
		// Nothing was presented for a whole stall interval, so no frame callback will release the next
		// configure: offer the opportunity here. The UI applies it only once its current layout is recorded.
		_frameOpportunity(_callbackGeneration);
		if (_waitingForContentRetry)
		{
			_waitingForContentRetry = false;
			TryRender();
			if (_dirty)
			{
				ArmStallTimer();
			}
			return;
		}
		// Not re-armed here: the UI's next invalidation does that, so an idle UI stops producing frames.
		_stalled();
	}

	private void OnDone(object? sender, WlCallback.DoneEventArgs args)
	{
		var callback = (WlCallback)sender!;
		_postToOwner(() =>
		{
			if (_callback != callback)
			{
				return;
			}
			CancelCallback();
			// Let the UI apply a pending configure before drawing again at the predecessor's size.
			if (!_frameOpportunity(_callbackGeneration))
			{
				TryRender();
			}
		});
	}

	private void CancelCallback()
	{
		if (_callback is { } callback)
		{
			_callback = null;
			callback.Done -= OnDone;
			callback.Dispose();
		}
	}

	public void Dispose()
	{
		_disposed = true;
		_stallTimer.Dispose();
		CancelCallback();
	}
}
