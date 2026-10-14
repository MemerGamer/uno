// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Uno.WinUI.Runtime.Skia.Wayland.Tests;

[TestClass]
public sealed class ConfigureQueueTests
{
	private static bool Enqueue(WaylandConfigureQueue queue, int width = 1280, bool activation = false)
		=> queue.Enqueue(new WaylandDecorationLayout(width, 799, 0, 8, WaylandCaptionButtons.Close),
			1, 120, 1, WaylandToplevelStates.Resizing, configured: true, notifyActivation: activation);

	[TestMethod]
	public void ConfigureFloodAppliesNewestSizeEveryDisplayFrameWithoutPresentation()
	{
		var queue = new WaylandConfigureQueue();
		var received = 0;
		var applied = 0;
		long previous = 0;
		// One second of the reported 525/s configure flood on a 165 Hz output. Neither
		// layout readiness, successful submission nor acknowledgement opens the next slot.
		for (var frame = 1; frame <= 165; frame++)
		{
			var newest = frame * 525 / 165;
			while (received < newest)
			{
				queue.Receive((uint)++received);
				Enqueue(queue, 1280 + received, activation: true);
			}
			if (previous != 0)
			{
				Assert.IsNull(queue.TakePending(), "The flood cannot apply twice within a frame.");
				Assert.IsTrue(queue.FrameOpportunity(previous));
			}
			var current = queue.TakePending();
			Assert.IsNotNull(current);
			Assert.AreEqual(1280 + received, current.Layout.ContentWidth);
			Assert.AreEqual((uint)received, current.Serial);
			Assert.IsTrue(current.NotifyActivation);
			Assert.IsFalse(queue.FrameOpportunity(previous), "An old callback/timer must not release a new size.");
			previous = current.Generation;
			applied++;
		}
		Assert.AreEqual(525, received);
		Assert.AreEqual(165, applied);
	}

	[TestMethod]
	public void FrameFallbackAllowsLateConfigureWithoutAnotherCommit()
	{
		var queue = new WaylandConfigureQueue();
		Assert.IsTrue(Enqueue(queue));
		var first = queue.TakePending()!;
		Assert.IsFalse(queue.FrameOpportunity(first.Generation), "No pending successor means no UI post.");
		Assert.IsTrue(Enqueue(queue, 900));
		Assert.AreEqual(900, queue.TakePending()!.Layout.ContentWidth);
	}

	[TestMethod]
	public void SlowDrawsKeepTheirConfigureWhileArrivalsAndTimerOpportunitiesContinue()
	{
		const int refreshMs = 6;
		const int drawMs = 10;
		var queue = new WaylandConfigureQueue();
		var received = 0;
		var completed = 0;
		var superseded = 0;
		var acks = new List<uint>();
		queue.Receive((uint)++received);
		Enqueue(queue, 1280 + received);
		var current = queue.TakePending()!;
		// A configure every 2 ms and a timer opportunity every refresh interval, while each
		// draw lasts longer than the interval.
		for (var attempt = 0; attempt < 50; attempt++)
		{
			queue.SetAttemptActive(true);
			var width = current.Layout.ContentWidth;
			for (var ms = 1; ms <= drawMs; ms++)
			{
				if (ms % 2 == 0)
				{
					queue.Receive((uint)++received);
					Enqueue(queue, 1280 + received);
				}
				if (ms % refreshMs == 0)
				{
					Assert.IsFalse(queue.FrameOpportunity(current.Generation), "The timer must not release a slot during a draw.");
				}
				superseded += queue.Current!.Generation == current.Generation && queue.Current.Layout.ContentWidth == width ? 0 : 1;
			}
			queue.AcknowledgeCurrent(acks.Add);
			completed++;
			Assert.IsTrue(queue.SetAttemptActive(false), "The deferred opportunity must advance when the draw ends.");
			current = queue.TakePending()!;
			Assert.AreEqual(1280 + received, current.Layout.ContentWidth, "Successors coalesce to the newest size.");
		}
		Assert.AreEqual(0, superseded);
		Assert.AreEqual(50, completed);
		Assert.AreEqual(50, acks.Count, "Every drawn configure is acknowledged, in receipt order.");
		CollectionAssert.AreEqual(acks.Order().ToArray(), acks);
	}

	[TestMethod]
	public void OpportunityWithoutActiveAttemptAdvancesImmediately()
	{
		var queue = new WaylandConfigureQueue();
		Enqueue(queue);
		var first = queue.TakePending()!;
		Enqueue(queue, 900);
		Assert.IsFalse(queue.SetAttemptActive(true));
		Assert.IsFalse(queue.SetAttemptActive(false), "No opportunity was deferred.");
		Assert.IsTrue(queue.FrameOpportunity(first.Generation));
	}

	[TestMethod]
	public void StateOnlyAcknowledgementIsIndependentOfAnActiveAttempt()
	{
		var queue = new WaylandConfigureQueue();
		var acks = new List<uint>();
		queue.Receive(5);
		Enqueue(queue);
		var current = queue.TakePending()!;
		queue.SetAttemptActive(true);
		queue.Receive(6);
		Assert.IsTrue(queue.CanAcknowledgeLatest(current.Layout, current.Scale));
		queue.AcknowledgeLatest(acks.Add);
		CollectionAssert.AreEqual(new uint[] { 6 }, acks);
	}

	[TestMethod]
	public void PresentedSerialsAreAcknowledgedInReceiptOrderAcrossWraparound()
	{
		var queue = new WaylandConfigureQueue();
		var acks = new List<uint>();
		queue.Receive(uint.MaxValue - 1);
		Enqueue(queue);
		var first = queue.TakePending()!;
		queue.AcknowledgeCurrent(acks.Add);
		queue.AcknowledgeCurrent(acks.Add);
		queue.Receive(uint.MaxValue);
		Enqueue(queue, 900);
		queue.Receive(0);
		Enqueue(queue, 901);
		Assert.IsTrue(queue.FrameOpportunity(first.Generation));
		var second = queue.TakePending()!;
		queue.AcknowledgeCurrent(acks.Add);
		queue.Receive(1);
		Assert.IsFalse(queue.CanAcknowledgeLatest(first.Layout, first.Scale));
		Enqueue(queue, 902);
		Assert.IsTrue(queue.FrameOpportunity(second.Generation));
		queue.TakePending();
		queue.AcknowledgeCurrent(acks.Add);
		CollectionAssert.AreEqual(new uint[] { uint.MaxValue - 1, 0, 1 }, acks);
	}

	[TestMethod]
	public void FrameOpportunityDoesNotPermitAckForDifferentGeometry()
	{
		var queue = new WaylandConfigureQueue();
		queue.Receive(10);
		Enqueue(queue);
		var current = queue.TakePending()!;
		Assert.IsFalse(queue.FrameOpportunity(current.Generation));
		queue.Receive(11);
		var otherLayout = current.Layout with { ContentWidth = 900 };
		Assert.IsFalse(queue.CanAcknowledgeLatest(otherLayout, 1));
		Assert.IsFalse(queue.CanAcknowledgeLatest(current.Layout, 2));
		Assert.IsTrue(queue.CanAcknowledgeLatest(current.Layout, 1));
	}

	[TestMethod]
	public void StateOnlyAckCannotMakeAnOlderSerialAckAgain()
	{
		var queue = new WaylandConfigureQueue();
		var acks = new List<uint>();
		queue.Receive(10);
		Enqueue(queue);
		var first = queue.TakePending()!;
		queue.Receive(11);
		Assert.IsTrue(queue.CanAcknowledgeLatest(first.Layout, first.Scale));
		queue.AcknowledgeLatest(acks.Add);
		queue.AcknowledgeCurrent(acks.Add);
		CollectionAssert.AreEqual(new uint[] { 11 }, acks);
	}

	[TestMethod]
	public void DeferredApplyWaitsForTheRecordedLayout()
	{
		var queue = new WaylandConfigureQueue();
		Assert.IsFalse(queue.DeferApply(), "Nothing is posted yet.");
		Assert.IsTrue(Enqueue(queue));
		var first = queue.TakePending()!;
		Assert.IsFalse(queue.DeferApply(), "The current configure still awaits its frame.");
		Assert.IsFalse(Enqueue(queue, 900), "A successor waits for the frame opportunity.");
		Assert.IsFalse(queue.ResumeDeferredApply());

		// The opportunity posts the successor, but the UI is still laying out the first configure.
		Assert.IsTrue(queue.FrameOpportunity(first.Generation));
		Assert.IsTrue(queue.DeferApply());
		Assert.IsFalse(Enqueue(queue, 1000), "Newer configures coalesce into the posted slot.");
		Assert.IsFalse(queue.FrameOpportunity(first.Generation), "The slot is already posted.");

		// Once the layout is recorded, the newest size applies.
		Assert.IsTrue(queue.ResumeDeferredApply());
		Assert.IsFalse(queue.ResumeDeferredApply(), "Resuming is one-shot.");
		var second = queue.TakePending()!;
		Assert.AreEqual(1000, second.Layout.ContentWidth);
		Assert.IsNull(queue.TakePending());
		Assert.IsFalse(queue.DeferApply(), "Taking the configure clears the deferral.");
	}
}
