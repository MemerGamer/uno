#nullable enable

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.Interactions;
using Microsoft.UI.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.UI.Composition;
using Uno.UI.Dispatching;

namespace Uno.UI.Tests.CompositionTests;

[UITestClass]
public class Given_KeyFrameAnimation_Without_Frames
{
	[TestMethod]
	public async Task When_No_Frame_Comes_Then_Ends_And_Completes_Batch()
	{
		var compositor = Compositor.GetSharedCompositor();
		var (visual, animation) = CreateOpacityAnimation(compositor, TimeSpan.FromMilliseconds(50));
		using var visualLifetime = visual;
		var completed = new TaskCompletionSource();
		var elapsed = Stopwatch.StartNew();

		var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
		batch.Completed += (_, _) => completed.TrySetResult();
		visual.StartAnimation(nameof(Visual.Opacity), animation);
		batch.End();

		await completed.Task.WaitAsync(TimeSpan.FromMilliseconds(200));

		Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromMilliseconds(200), $"Completed after {elapsed.ElapsedMilliseconds}ms.");
		Assert.AreEqual(1.0f, visual.Opacity);
		Assert.IsFalse(compositor.IsRunning(animation));
	}

	[TestMethod]
	public async Task When_Paused_Then_Does_Not_End()
	{
		var compositor = Compositor.GetSharedCompositor();
		var (visual, animation) = CreateOpacityAnimation(compositor, TimeSpan.FromMilliseconds(50));
		using var visualLifetime = visual;
		var stopped = false;
		animation.Stopped += (_, _) => stopped = true;

		visual.StartAnimation(nameof(Visual.Opacity), animation);
		var controller = visual.TryGetAnimationController(nameof(Visual.Opacity))!;
		controller.Pause();
		await Task.Delay(500);

		Assert.IsFalse(stopped);
		Assert.AreEqual(0.0f, visual.Opacity);

		visual.StopAnimation(nameof(Visual.Opacity));
	}

	[TestMethod]
	public async Task When_Resumed_Then_Ends()
	{
		var compositor = Compositor.GetSharedCompositor();
		var (visual, animation) = CreateOpacityAnimation(compositor, TimeSpan.FromMilliseconds(50));
		using var visualLifetime = visual;
		var stopped = new TaskCompletionSource();
		animation.Stopped += (_, _) => stopped.TrySetResult();

		visual.StartAnimation(nameof(Visual.Opacity), animation);
		var controller = visual.TryGetAnimationController(nameof(Visual.Opacity))!;
		controller.Pause();
		await Task.Delay(100);
		controller.Resume();

		await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.AreEqual(1.0f, visual.Opacity);
	}

	[TestMethod]
	public async Task When_Stopped_Then_Does_Not_Apply_Final_Value()
	{
		var compositor = Compositor.GetSharedCompositor();
		var (visual, animation) = CreateOpacityAnimation(compositor, TimeSpan.FromMilliseconds(50));
		using var visualLifetime = visual;

		visual.StartAnimation(nameof(Visual.Opacity), animation);
		visual.StopAnimation(nameof(Visual.Opacity));
		await Task.Delay(500);

		Assert.AreEqual(0.0f, visual.Opacity);
	}

	[TestMethod]
	public async Task When_Repeating_Forever_Then_Waits_For_Frames()
	{
		var compositor = Compositor.GetSharedCompositor();
		var (visual, animation) = CreateOpacityAnimation(compositor, TimeSpan.FromMilliseconds(50));
		using var visualLifetime = visual;
		animation.IterationBehavior = AnimationIterationBehavior.Forever;
		var stopped = false;
		animation.Stopped += (_, _) => stopped = true;

		visual.StartAnimation(nameof(Visual.Opacity), animation);
		Assert.IsNull(GetEndTimer(animation));
		await Task.Delay(500);

		Assert.IsFalse(stopped);
		Assert.IsTrue(compositor.IsRunning(animation));

		visual.StopAnimation(nameof(Visual.Opacity));
	}

	[TestMethod]
	public async Task When_Frames_Come_Then_Animation_Starts_At_First_Frame()
	{
		var compositor = Compositor.GetSharedCompositor();
		var duration = TimeSpan.FromMilliseconds(500);
		var (visual, animation) = CreateOpacityAnimation(compositor, duration);
		using var visualLifetime = visual;
		var stopped = false;
		animation.Stopped += (_, _) => stopped = true;

		visual.StartAnimation(nameof(Visual.Opacity), animation);

		// A frame that arrives before the fallback deadline still starts playback at its initial value.
		await Task.Delay(100);
		Assert.IsFalse(stopped);

		var firstFrame = Stopwatch.StartNew();
		var previousFrameTimestamp = compositor.FrameTimestampInTicks;
		try
		{
			// Use one timestamp for the frame so evaluation itself cannot advance playback.
			compositor.FrameTimestampInTicks = compositor.TimestampInTicks;
			animation.RaiseAnimationFrame();
			Assert.AreEqual(0.0f, visual.Opacity);
		}
		finally
		{
			compositor.FrameTimestampInTicks = previousFrameTimestamp;
		}

		while (!stopped && firstFrame.Elapsed < TimeSpan.FromSeconds(5))
		{
			animation.RaiseAnimationFrame();
			await Task.Delay(10);
		}

		Assert.IsTrue(stopped);
		Assert.IsTrue(firstFrame.Elapsed >= duration, $"Ended {firstFrame.ElapsedMilliseconds}ms after the first frame.");
		Assert.AreEqual(1.0f, visual.Opacity);
	}

	[TestMethod]
	public async Task When_Frames_Stop_Near_Deadline_Then_Completes_On_Time()
	{
		var compositor = Compositor.GetSharedCompositor();
		var (visual, animation) = CreateOpacityAnimation(compositor, TimeSpan.FromMilliseconds(400));
		using var visualLifetime = visual;
		var completed = new TaskCompletionSource();
		using var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
		batch.Completed += (_, _) => completed.TrySetResult();

		visual.StartAnimation(nameof(Visual.Opacity), animation);
		batch.End();
		var elapsed = Stopwatch.StartNew();
		animation.RaiseAnimationFrame();
		await Task.Delay(325);
		animation.RaiseAnimationFrame();

		await completed.Task.WaitAsync(TimeSpan.FromMilliseconds(175));

		Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromMilliseconds(500), $"Completed after {elapsed.ElapsedMilliseconds}ms.");
		Assert.AreEqual(1.0f, visual.Opacity);
	}

	[TestMethod]
	public async Task When_Paused_After_Partial_Playback_Then_Resumes_Remaining_Time()
	{
		var compositor = Compositor.GetSharedCompositor();
		var (visual, animation) = CreateOpacityAnimation(compositor, TimeSpan.FromSeconds(1));
		using var visualLifetime = visual;
		var stopped = new TaskCompletionSource();
		animation.Stopped += (_, _) => stopped.TrySetResult();
		visual.StartAnimation(nameof(Visual.Opacity), animation);
		var controller = visual.TryGetAnimationController(nameof(Visual.Opacity))!;

		await Task.Delay(400);
		controller.Pause();
		await Task.Delay(150);
		Assert.IsFalse(stopped.Task.IsCompleted);
		controller.Resume();
		await Task.Delay(300);
		controller.Pause();
		await Task.Delay(150);
		Assert.IsFalse(stopped.Task.IsCompleted);
		var elapsed = Stopwatch.StartNew();
		controller.Resume();

		await stopped.Task.WaitAsync(TimeSpan.FromMilliseconds(500));

		Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromMilliseconds(500), $"Resumed for {elapsed.ElapsedMilliseconds}ms.");
		Assert.AreEqual(1.0f, visual.Opacity);
	}

	[TestMethod]
	public async Task When_Playback_Rate_Changes_Before_First_Frame_Then_Preserves_Playback()
	{
		var compositor = Compositor.GetSharedCompositor();
		var (visual, animation) = CreateOpacityAnimation(compositor, TimeSpan.FromSeconds(1));
		using var visualLifetime = visual;
		var stopped = new TaskCompletionSource();
		animation.Stopped += (_, _) => stopped.TrySetResult();
		visual.StartAnimation(nameof(Visual.Opacity), animation);
		var controller = visual.TryGetAnimationController(nameof(Visual.Opacity))!;

		await Task.Delay(400);
		controller.PlaybackRate = 2;
		var elapsed = Stopwatch.StartNew();

		await stopped.Task.WaitAsync(TimeSpan.FromMilliseconds(450));

		Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromMilliseconds(450), $"Ran for {elapsed.ElapsedMilliseconds}ms after changing rate.");
		Assert.AreEqual(1.0f, visual.Opacity);
	}

	[TestMethod]
	public async Task When_End_Is_Queued_Then_Zero_Playback_Rate_Invalidates_Callback()
	{
		var compositor = Compositor.GetSharedCompositor();
		var (visual, animation) = CreateOpacityAnimation(compositor, TimeSpan.FromMilliseconds(50));
		using var visualLifetime = visual;
		var stoppedCount = 0;
		var completedCount = 0;
		animation.Stopped += (_, _) => stoppedCount++;
		using var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
		batch.Completed += (_, _) => completedCount++;
		var callback = await CaptureEndCallback(() =>
		{
			visual.StartAnimation(nameof(Visual.Opacity), animation);
			batch.End();
		});
		var controller = visual.TryGetAnimationController(nameof(Visual.Opacity))!;
		controller.PlaybackRate = 0;
		callback();
		await Task.Delay(350);

		Assert.AreEqual(0, stoppedCount);
		Assert.AreEqual(0, completedCount);
		Assert.AreEqual(0.0f, visual.Opacity);
		Assert.IsTrue(compositor.IsRunning(animation));

		controller.PlaybackRate = 1;
		await Task.Delay(100);
		Assert.AreEqual(1, stoppedCount);
		Assert.AreEqual(1, completedCount);
		Assert.AreEqual(1.0f, visual.Opacity);
	}

	[TestMethod]
	public async Task When_End_Is_Queued_Then_Rearming_Invalidates_Callback()
	{
		var compositor = Compositor.GetSharedCompositor();
		var (visual, animation) = CreateOpacityAnimation(compositor, TimeSpan.FromMilliseconds(50));
		using var visualLifetime = visual;
		var stoppedCount = 0;
		var completedCount = 0;
		animation.Stopped += (_, _) => stoppedCount++;
		using var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
		batch.Completed += (_, _) => completedCount++;
		var callback = await CaptureEndCallback(() =>
		{
			visual.StartAnimation(nameof(Visual.Opacity), animation);
			batch.End();
		});
		var controller = visual.TryGetAnimationController(nameof(Visual.Opacity))!;
		controller.Pause();
		controller.Progress = 0;
		controller.Resume();
		var timer = GetEndTimer(animation);
		callback();

		Assert.AreSame(timer, GetEndTimer(animation));
		Assert.AreEqual(0, stoppedCount);
		Assert.AreEqual(0, completedCount);
		await Task.Delay(150);
		Assert.AreEqual(1, stoppedCount);
		Assert.AreEqual(1, completedCount);
	}

	[TestMethod]
	public async Task When_Repeating_Finite_Times_Then_Waits_For_Frames()
	{
		var compositor = Compositor.GetSharedCompositor();
		var (visual, animation) = CreateOpacityAnimation(compositor, TimeSpan.FromMilliseconds(50));
		using var visualLifetime = visual;
		animation.IterationCount = 2;
		var stopped = false;
		animation.Stopped += (_, _) => stopped = true;
		visual.StartAnimation(nameof(Visual.Opacity), animation);
		animation.IterationCount = 1;
		visual.TryGetAnimationController(nameof(Visual.Opacity))!.PlaybackRate = 2;

		Assert.IsNull(GetEndTimer(animation));
		await Task.Delay(300);

		Assert.IsFalse(stopped);
		Assert.IsTrue(compositor.IsRunning(animation));
		visual.StopAnimation(nameof(Visual.Opacity));
	}

	[TestMethod]
	public async Task When_Changed_To_Repeating_After_Start_Then_Uses_Captured_Iteration_Count()
	{
		var compositor = Compositor.GetSharedCompositor();
		var (visual, animation) = CreateOpacityAnimation(compositor, TimeSpan.FromMilliseconds(50));
		using var visualLifetime = visual;
		var stopped = new TaskCompletionSource();
		animation.Stopped += (_, _) => stopped.TrySetResult();
		visual.StartAnimation(nameof(Visual.Opacity), animation);
		animation.IterationCount = 2;
		animation.IterationBehavior = AnimationIterationBehavior.Forever;
		visual.TryGetAnimationController(nameof(Visual.Opacity))!.PlaybackRate = 2;

		Assert.IsNotNull(GetEndTimer(animation));
		await stopped.Task.WaitAsync(TimeSpan.FromMilliseconds(200));
		Assert.AreEqual(1.0f, visual.Opacity);
	}

	[TestMethod]
	public async Task When_Unattached_Then_Does_Not_Retain_Target()
	{
		var (visual, animation) = StartUnattachedAnimation();
		for (var i = 0; i < 5 && (visual.IsAlive || animation.IsAlive); i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			await Task.Delay(20);
		}

		Assert.IsFalse(visual.IsAlive);
		Assert.IsFalse(animation.IsAlive);
	}

	[TestMethod]
	public async Task When_Disposed_Then_Disposes_Timer_And_Invalidates_Callback()
	{
		var compositor = Compositor.GetSharedCompositor();
		var (visual, animation) = CreateOpacityAnimation(compositor, TimeSpan.FromMilliseconds(50));
		using var visualLifetime = visual;
		var stoppedCount = 0;
		animation.Stopped += (_, _) => stoppedCount++;
		var callback = await CaptureEndCallback(() => visual.StartAnimation(nameof(Visual.Opacity), animation));
		var timer = GetEndTimer(animation)!;

		animation.Dispose();
		Assert.IsNull(GetEndTimer(animation));
		Assert.IsFalse(timer.Change(Timeout.Infinite, Timeout.Infinite));
		callback();
		Assert.AreEqual(0, stoppedCount);
		Assert.AreEqual(0.0f, visual.Opacity);
		visual.StopAnimation(nameof(Visual.Opacity));
	}

	private static async Task<Action> CaptureEndCallback(Action start)
	{
		var previousDispatch = NativeDispatcher.DispatchOverride;
		var queued = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
		NativeDispatcher.DispatchOverride = (action, _) => queued.TrySetResult(action);
		try
		{
			start();
			return await queued.Task.WaitAsync(TimeSpan.FromSeconds(2));
		}
		finally
		{
			NativeDispatcher.DispatchOverride = previousDispatch;
		}
	}

	private static Timer? GetEndTimer(KeyFrameAnimation animation)
		=> (Timer?)typeof(KeyFrameAnimation).GetField("_endTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(animation);

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static (WeakReference Visual, WeakReference Animation) StartUnattachedAnimation()
	{
		var compositor = Compositor.GetSharedCompositor();
		var (visual, animation) = CreateOpacityAnimation(compositor, TimeSpan.FromHours(1));
		visual.CompositionTarget = null;
		visual.StartAnimation(nameof(Visual.Opacity), animation);
		Assert.IsFalse(compositor.IsRunning(animation));
		return (new WeakReference(visual), new WeakReference(animation));
	}

	private static (SpriteVisual Visual, ScalarKeyFrameAnimation Animation) CreateOpacityAnimation(Compositor compositor, TimeSpan duration)
	{
		var visual = compositor.CreateSpriteVisual();
		visual.CompositionTarget = new TestCompositionTarget();
		visual.Opacity = 0.0f;

		var animation = compositor.CreateScalarKeyFrameAnimation();
		animation.InsertKeyFrame(0.0f, 0.0f);
		animation.InsertKeyFrame(1.0f, 1.0f);
		animation.Duration = duration;

		return (visual, animation);
	}

	// Never records, like the target of a hidden window.
	private sealed class TestCompositionTarget : ICompositionTarget
	{
		public double RasterizationScale => 1;

		public Uno.UI.Composition.Drawing.IDrawingFactory? Renderer => null;

		public event EventHandler? RasterizationScaleChanged
		{
			add { }
			remove { }
		}

		public void AddDamage(Windows.Foundation.Rect bounds)
		{
		}

		public void AddDamage(Uno.UI.Composition.Drawing.IGeometry region)
		{
		}

		public void RequestNewFrame()
		{
		}

		public event EventHandler<long>? FrameStarting
		{
			add { }
			remove { }
		}

		public long FrameIntervalInTicks => TimeSpan.TicksPerSecond / 60;

		public void TryRedirectForManipulation(PointerPoint pointerPoint, InteractionTracker tracker)
		{
		}
	}
}
