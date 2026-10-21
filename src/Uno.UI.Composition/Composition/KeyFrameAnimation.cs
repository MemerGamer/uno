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
					_lastFrameTimestamp = null;
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
		private long? _lastFrameTimestamp;
		private long _armTimestamp;
		private long _remainingAtArm;

		// How long the timer leaves for a frame to land before ending the animation itself. Longer than any frame
		// interval, so it never ends an animation a frame is about to evaluate.
		private static readonly long _frameGraceInTicks = TimeSpan.FromMilliseconds(250).Ticks;

		internal override object Evaluate()
		{
			_lastFrameTimestamp = Compositor.TimestampInTicks;
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
			_lastFrameTimestamp = null;
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

		// Not used for animations that repeat forever or are halted (Remaining is then unbounded).
		private void ArmEndTimer()
		{
			if (StartedObjectCount == 0 || _keyframeEvaluator is not { IsPaused: false } evaluator)
			{
				DisarmEndTimer();
				return;
			}

			var now = Compositor.TimestampInTicks;
			var remaining = evaluator.RemainingAt(now);
			if (remaining == TimeSpan.MaxValue)
			{
				DisarmEndTimer();
				return;
			}

			// Until a frame evaluates it, the evaluator does not advance (the animation starts at its first frame), so
			// time without frames is counted from here.
			if (_lastFrameTimestamp is null)
			{
				_armTimestamp = now;
				_remainingAtArm = remaining.Ticks;
			}

			_endTimer ??= new Timer(_ => NativeDispatcher.Main.Enqueue(OnEndTimer));
			ScheduleEndTimer(remaining.Ticks);
		}

		private void ScheduleEndTimer(long ticks)
			=> _endTimer?.Change((long)Math.Min(Math.Ceiling(ticks / (double)TimeSpan.TicksPerMillisecond), int.MaxValue), Timeout.Infinite);

		private void DisarmEndTimer() => _endTimer?.Change(Timeout.Infinite, Timeout.Infinite);

		// Runs the normal end, as a frame would, unless a frame got there first or may still do so.
		private void OnEndTimer()
		{
			if (StartedObjectCount == 0 || _keyframeEvaluator is not { IsPaused: false } evaluator || !Compositor.IsRunning(this))
			{
				return;
			}

			var now = Compositor.TimestampInTicks;
			long remaining, sinceLastFrame;
			if (_lastFrameTimestamp is { } lastFrame)
			{
				remaining = evaluator.RemainingAt(now).Ticks;
				sinceLastFrame = now - lastFrame;
			}
			else
			{
				sinceLastFrame = now - _armTimestamp;
				remaining = _remainingAtArm - sinceLastFrame;
			}

			var wait = Math.Max(remaining, _frameGraceInTicks - sinceLastFrame);
			if (wait > 0)
			{
				ScheduleEndTimer(wait);
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
