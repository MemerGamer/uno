#nullable enable

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.WinUI.Runtime.Skia.Wayland;

namespace Wayland.Scanner.Tests;

[TestClass]
public sealed class DragSourceTests
{
	[TestMethod]
	public void FinishedReportsTheLastActionTheCompositorSelected()
	{
		var state = new WaylandDragSourceState();
		state.Target("text/uri-list");
		state.SetAction(WaylandDragSourceState.ActionCopy);
		state.SetAction(WaylandDragSourceState.ActionMove);
		state.DropPerformed();
		Assert.AreEqual(WaylandDragSourcePhase.Dropped, state.Phase);
		// The target may still change the action between the drop and finish.
		state.SetAction(WaylandDragSourceState.ActionCopy);
		state.Sent();
		Assert.IsTrue(state.Finished());
		Assert.AreEqual(WaylandDragSourcePhase.Finished, state.Phase);
		Assert.AreEqual(WaylandDragSourceState.ActionCopy, state.Result);
		Assert.IsTrue(state.IsComplete);
		// Late events after completion change nothing.
		Assert.IsFalse(state.Cancelled());
		Assert.IsFalse(state.Finished());
		state.SetAction(WaylandDragSourceState.ActionMove);
		Assert.AreEqual(WaylandDragSourceState.ActionCopy, state.Result);
		Assert.AreEqual(WaylandDragSourceState.ActionCopy, state.Action);
	}

	[TestMethod]
	public void MoveIsReportedOnlyWhenTheTargetFinishedWithIt()
	{
		var state = new WaylandDragSourceState();
		state.SetAction(WaylandDragSourceState.ActionMove);
		state.DropPerformed();
		state.Sent();
		Assert.IsTrue(state.Finished());
		Assert.AreEqual(WaylandDragSourceState.ActionMove, state.Result);
	}

	[TestMethod]
	public void CancelledAfterDropReportsNoOperation()
	{
		var state = new WaylandDragSourceState();
		state.Target("text/plain");
		state.SetAction(WaylandDragSourceState.ActionMove);
		state.DropPerformed();
		state.Sent();
		// The target destroyed its offer without wl_data_offer.finish, so the data must not be deleted.
		Assert.IsTrue(state.Cancelled());
		Assert.AreEqual(WaylandDragSourcePhase.Cancelled, state.Phase);
		Assert.AreEqual(0u, state.Result);
		Assert.IsFalse(state.Finished());
	}

	[TestMethod]
	public void EscapeBeforeAnyDropIsCancelled()
	{
		var state = new WaylandDragSourceState();
		state.Target("text/plain");
		state.SetAction(WaylandDragSourceState.ActionCopy);
		state.Target(null);
		Assert.IsFalse(state.TargetAccepts);
		Assert.IsTrue(state.Cancelled());
		Assert.AreEqual(0u, state.Result);
		Assert.IsFalse(state.DataSent);
	}

	[TestMethod]
	public void FinishWithoutNegotiatedActionCountsAsCopyOnlyWhenDataWasRead()
	{
		var read = new WaylandDragSourceState();
		read.DropPerformed();
		read.Sent();
		Assert.IsTrue(read.Finished());
		Assert.AreEqual(WaylandDragSourceState.ActionCopy, read.Result);

		var unread = new WaylandDragSourceState();
		unread.DropPerformed();
		Assert.IsTrue(unread.Finished());
		Assert.AreEqual(0u, unread.Result);
	}

	[TestMethod]
	public void UnknownActionBitsAreIgnored()
	{
		var state = new WaylandDragSourceState();
		// ask (4) is never offered, so a compositor cannot select it; stray bits are masked.
		state.SetAction(4 | WaylandDragSourceState.ActionMove | 8);
		Assert.AreEqual(WaylandDragSourceState.ActionMove, state.Action);
		state.SetAction(4);
		Assert.AreEqual(0u, state.Action);
	}

	[TestMethod]
	public void IconLayoutKeepsPixelsUnderViewporter()
	{
		var layout = WaylandDragIconLayout.Compute(150, 90, 1.5, (-30, -15), hasViewport: true);
		Assert.AreEqual((150, 90, 1), (layout.BufferWidth, layout.BufferHeight, layout.BufferScale));
		Assert.AreEqual((100, 60), (layout.LogicalWidth, layout.LogicalHeight));
		Assert.AreEqual((-30, -15), (layout.OffsetX, layout.OffsetY));
	}

	[TestMethod]
	public void IconLayoutResamplesToAnIntegerScaleWithoutViewporter()
	{
		var layout = WaylandDragIconLayout.Compute(150, 90, 1.5, null, hasViewport: false);
		Assert.AreEqual(2, layout.BufferScale);
		Assert.AreEqual((200, 120), (layout.BufferWidth, layout.BufferHeight));
		Assert.AreEqual((100, 60), (layout.LogicalWidth, layout.LogicalHeight));
		// Uno's DragView default: centred horizontally, three quarters above the pointer.
		Assert.AreEqual((-50, -45), (layout.OffsetX, layout.OffsetY));
	}

	[TestMethod]
	public void IconLayoutShrinksOversizedSnapshots()
	{
		var layout = WaylandDragIconLayout.Compute(2048, 512, 1, (-1024, -256), hasViewport: true);
		Assert.AreEqual((512, 128), (layout.LogicalWidth, layout.LogicalHeight));
		Assert.AreEqual((512, 128, 1), (layout.BufferWidth, layout.BufferHeight, layout.BufferScale));
		Assert.AreEqual((-256, -64), (layout.OffsetX, layout.OffsetY));
		var unscaled = WaylandDragIconLayout.Compute(0, 0, 0, null, hasViewport: false);
		Assert.AreEqual((1, 1, 1), (unscaled.LogicalWidth, unscaled.LogicalHeight, unscaled.BufferScale));
	}
}
