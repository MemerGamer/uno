#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Uno;
using Uno.Foundation.Logging;
using Uno.UI.Dispatching;

namespace Microsoft.UI.Composition
{
	public partial class KeyFrameAnimation : CompositionAnimation
	{
		private IKeyFrameEvaluator? _keyframeEvaluatorField;
		private float _playbackRate = 1.0f;

		// Concrete keyframe animations assign this when they Start(); routing through the property lets
		// a PlaybackRate set before Start() take effect the moment the evaluator is created.
		private protected IKeyFrameEvaluator? _keyframeEvaluator
		{
			get => _keyframeEvaluatorField;
			set
			{
				_keyframeEvaluatorField = value;
				if (value is not null)
				{
					value.PlaybackRate = _playbackRate;
					ArmEndTimer();
				}
			}
		}

		internal KeyFrameAnimation() => throw new NotSupportedException();

		internal KeyFrameAnimation(Compositor compositor) : base(compositor)
		{
		}

		internal override bool IsTrackedByCompositor => true;

		[NotImplemented]
		public AnimationStopBehavior StopBehavior { get; set; }

		public int IterationCount { get; set; } = 1;

		public AnimationIterationBehavior IterationBehavior { get; set; }

		public TimeSpan Duration { get; set; }

		[NotImplemented]
		public TimeSpan DelayTime { get; set; }

		public int KeyFrameCount => KeyFrameCountCore;

		private protected virtual int KeyFrameCountCore { get; }

		[NotImplemented]
		public AnimationDirection Direction { get; set; }

		internal event EventHandler? Stopped;

		private bool _stopOnceValueApplied;

		// Frames stop while nothing is on screen (a hidden window), so a finite animation also ends on a timer.
		private Timer? _endTimer;
		private long _endTimerGeneration;

		internal override object Evaluate()
		{
			var (value, shouldStop) = _keyframeEvaluator!.Evaluate();
			if (shouldStop)
			{
				// Stopped handlers read the animated property (ScrollContentPresenter's final offset), so stop only once the
				// caller has applied this final value; otherwise an animation that ends before its first frame reports its start.
				_stopOnceValueApplied = true;
			}

			return value;
		}

		internal override void OnEvaluatedValueApplied()
		{
			if (_stopOnceValueApplied)
			{
				_stopOnceValueApplied = false;
				Stop();
			}
		}

		internal object Evaluate(float progress)
		{
			return _keyframeEvaluator!.Evaluate(progress);
		}

		internal override void Stop()
		{
			base.Stop();
			if (StartedObjectCount == 0)
			{
				DisarmEndTimer();
			}

			Stopped?.Invoke(this, EventArgs.Empty);
		}

		internal void Pause()
		{
			AdvanceFallbackPlayback();
			_keyframeEvaluator!.Pause();
			DisarmEndTimer();
		}

		internal void Seek(float progress)
		{
			_keyframeEvaluator!.Seek(progress);
			ArmEndTimer();
		}

		// These match WinUI's non-virtual KeyFrameAnimation API surface; per-type behaviour is provided
		// by InsertExpressionKeyFrameCore. A virtual modifier here would diverge from WinUI and make the
		// sync generator emit a conflicting NotImplemented stub.
		public void InsertExpressionKeyFrame(float normalizedProgressKey, string value)
			=> InsertExpressionKeyFrame(normalizedProgressKey, value, Compositor.GetDefaultEasingFunction());

		public void InsertExpressionKeyFrame(float normalizedProgressKey, string value, CompositionEasingFunction easingFunction)
			=> InsertExpressionKeyFrameCore(normalizedProgressKey, value, easingFunction);

		// Concrete keyframe animations override this to parse and evaluate the expression. This base
		// fallback warns rather than silently ignoring the keyframe for any type that doesn't.
		private protected virtual void InsertExpressionKeyFrameCore(float normalizedProgressKey, string value, CompositionEasingFunction easingFunction)
			=> WarnExpressionKeyFrameNotSupported();

		private void WarnExpressionKeyFrameNotSupported()
		{
			if (this.Log().IsEnabled(LogLevel.Warning))
			{
				this.Log().Warn($"Expression keyframes are not supported for '{GetType().Name}'; the keyframe will be ignored.");
			}
		}

		internal void Resume()
		{
			_keyframeEvaluator!.Resume();
			ArmEndTimer();
		}

		internal bool IsPaused => _keyframeEvaluator?.IsPaused == true;

		// Driven by AnimationController.PlaybackRate. Stored so a rate set before Start() (before the
		// evaluator exists) is applied on creation via the _keyframeEvaluator setter.
		internal void SetPlaybackRate(float value)
		{
			_playbackRate = value;
			if (_keyframeEvaluator is { } evaluator)
			{
				AdvanceFallbackPlayback();
				evaluator.PlaybackRate = value;
				ArmEndTimer();
			}
		}

		internal void SeekTo(float progress)
		{
			if (_keyframeEvaluator is { } evaluator)
			{
				evaluator.SeekTo(progress);
				ArmEndTimer();
			}
		}

		internal float Progress => _keyframeEvaluator!.Progress;

		internal TimeSpan Remaining => _keyframeEvaluator!.Remaining;

		private void AdvanceFallbackPlayback()
		{
			if (Compositor.IsRunning(this) && _keyframeEvaluator is { IsRepeating: false } evaluator)
			{
				evaluator.AdvanceBeforeFirstFrame(Compositor.TimestampInTicks);
			}
		}

		private void ArmEndTimer()
		{
			DisarmEndTimer();
			if (StartedObjectCount == 0 || !Compositor.IsRunning(this) || _keyframeEvaluator is not { IsPaused: false, IsRepeating: false } evaluator)
			{
				return;
			}

			var now = Compositor.TimestampInTicks;
			var remaining = evaluator.RemainingAt(now);
			if (remaining == TimeSpan.MaxValue)
			{
				return;
			}

			ScheduleEndTimer(remaining.Ticks);
		}

		private void ScheduleEndTimer(long ticks)
		{
			DisarmEndTimer();
			var generation = Volatile.Read(ref _endTimerGeneration);
			var delay = (long)Math.Min(Math.Ceiling(ticks / (double)TimeSpan.TicksPerMillisecond), int.MaxValue);
			_endTimer = new Timer(_ => NativeDispatcher.Main.Enqueue(() => OnEndTimer(generation)), null, delay, Timeout.Infinite);
		}

		private void DisarmEndTimer()
		{
			Interlocked.Increment(ref _endTimerGeneration);
			Interlocked.Exchange(ref _endTimer, null)?.Dispose();
		}

		private protected override void DisposeInternal()
		{
			DisarmEndTimer();
			base.DisposeInternal();
		}

		private void OnEndTimer(long generation)
		{
			if (generation != Volatile.Read(ref _endTimerGeneration) || StartedObjectCount == 0 ||
				_keyframeEvaluator is not { IsPaused: false, IsRepeating: false } evaluator || !Compositor.IsRunning(this))
			{
				return;
			}

			var remaining = evaluator.RemainingAt(Compositor.TimestampInTicks);
			if (remaining == TimeSpan.MaxValue)
			{
				DisarmEndTimer();
				return;
			}

			if (remaining > TimeSpan.Zero)
			{
				ScheduleEndTimer(remaining.Ticks);
				return;
			}

			evaluator.Finish();
			try
			{
				RaiseAnimationFrame();
			}
			catch (Exception e)
			{
				if (this.Log().IsEnabled(LogLevel.Error))
				{
					this.Log().Error("Stopping animation after an unhandled evaluation error.", e);
				}

				Stop();
			}
		}
	}
}
