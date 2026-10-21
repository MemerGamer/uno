using System;

namespace Microsoft.UI.Composition;

internal interface IKeyFrameEvaluator
{
	(object Value, bool ShouldStop) Evaluate();
	object Evaluate(float progress);
	void Pause();
	void Resume();
	void Seek(float progress);

	/// <summary>Moves playback to its end, so the next <see cref="Evaluate()"/> returns the final value and stops.</summary>
	void Finish();

	void AdvanceBeforeFirstFrame(long timestamp);

	bool IsRepeating { get; }

	float PlaybackRate { get; set; }

	/// <summary>
	/// Re-anchors playback to <paramref name="progress"/> without pausing. Clock-driven playback (at
	/// the current rate) continues from the new position.
	/// </summary>
	void SeekTo(float progress);

	float Progress { get; }

	bool IsPaused { get; }

	/// <summary>
	/// The time remaining until the animation completes.
	/// </summary>
	TimeSpan Remaining { get; }

	/// <summary>
	/// The fallback time remaining at <paramref name="timestamp"/>, including playback before the first frame.
	/// </summary>
	TimeSpan RemainingAt(long timestamp);
}
