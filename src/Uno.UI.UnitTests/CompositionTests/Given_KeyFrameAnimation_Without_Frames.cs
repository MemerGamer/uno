#nullable enable

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.Interactions;
using Microsoft.UI.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.UI.Composition;

namespace Uno.UI.Tests.CompositionTests;

[UITestClass]
public class Given_KeyFrameAnimation_Without_Frames
{
	[TestMethod]
	public async Task When_No_Frame_Comes_Then_Ends_And_Completes_Batch()
	{
		var compositor = Compositor.GetSharedCompositor();
		var (visual, animation) = CreateOpacityAnimation(compositor, TimeSpan.FromMilliseconds(50));
		var completed = new TaskCompletionSource();

		var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
		batch.Completed += (_, _) => completed.TrySetResult();
		visual.StartAnimation(nameof(Visual.Opacity), animation);
		batch.End();

		await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.AreEqual(1.0f, visual.Opacity);
		Assert.IsFalse(compositor.IsRunning(animation));
	}

	[TestMethod]
	public async Task When_Paused_Then_Does_Not_End()
	{
		var compositor = Compositor.GetSharedCompositor();
		var (visual, animation) = CreateOpacityAnimation(compositor, TimeSpan.FromMilliseconds(50));
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
		animation.IterationBehavior = AnimationIterationBehavior.Forever;
		var stopped = false;
		animation.Stopped += (_, _) => stopped = true;

		visual.StartAnimation(nameof(Visual.Opacity), animation);
		await Task.Delay(500);

		Assert.IsFalse(stopped);
		Assert.IsTrue(compositor.IsRunning(animation));

		visual.StopAnimation(nameof(Visual.Opacity));
	}

	[TestMethod]
	public async Task When_Frames_Come_Then_Animation_Starts_At_First_Frame()
	{
		var compositor = Compositor.GetSharedCompositor();
		var duration = TimeSpan.FromMilliseconds(50);
		var (visual, animation) = CreateOpacityAnimation(compositor, duration);
		var stopped = false;
		animation.Stopped += (_, _) => stopped = true;

		visual.StartAnimation(nameof(Visual.Opacity), animation);

		// The first frame lands after the animation's duration but within the time left for frames to arrive.
		await Task.Delay(100);
		Assert.IsFalse(stopped);

		var firstFrame = Stopwatch.StartNew();
		while (!stopped && firstFrame.Elapsed < TimeSpan.FromSeconds(5))
		{
			animation.RaiseAnimationFrame();
			await Task.Delay(10);
		}

		Assert.IsTrue(stopped);
		Assert.IsTrue(firstFrame.Elapsed >= duration, $"Ended {firstFrame.ElapsedMilliseconds}ms after the first frame.");
		Assert.AreEqual(1.0f, visual.Opacity);
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
