// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Input;
using PointerDeviceType = Windows.Devices.Input.PointerDeviceType;
using PointerEventArgs = Windows.UI.Core.PointerEventArgs;

namespace Uno.UI.Runtime.X11;

[TestClass]
public sealed class Given_X11DragProtocols
{
	[TestMethod]
	[DataRow(true)]
	[DataRow(false)]
	public void RejectedGrabOrStartupTimeoutPreservesInternalDrop(bool grabReleased)
	{
		var host = new X11XamlRootHost();
		var pointer = new X11PointerInputSource(host);
		var properties = new PointerPointProperties { IsLeftButtonPressed = true };
		pointer.Press(new PointerPoint(1, 1, PointerDevice.For(PointerDeviceType.Mouse), 1, default, default, true, properties));
		var cancelled = 0;
		var captureLost = 0;
		pointer.PointerCancelled += (_, _) => cancelled++;
		pointer.PointerCaptureLost += (_, _) => captureLost++;
		var generation = pointer.BeginNativeDrag(1, default);
		pointer.RollbackNativeDrag(generation, grabReleased);
		host.Drain();
		Assert.IsFalse(pointer.IsNativeDragActive);
		Assert.AreEqual(2, pointer.PressedButtons);
		Assert.IsTrue(pointer.PreviousProperties!.IsLeftButtonPressed);
		Assert.AreEqual(grabReleased ? 1 : 0, host.RestoredGrabs);
		Assert.AreEqual(0, cancelled);
		Assert.AreEqual(0, captureLost);
		Assert.IsTrue(pointer.InternalDrop());
	}

	[TestMethod]
	[DataRow(true, 1, PointerUpdateKind.LeftButtonReleased)]
	[DataRow(false, 1, PointerUpdateKind.LeftButtonReleased)]
	[DataRow(true, 2, PointerUpdateKind.MiddleButtonReleased)]
	[DataRow(true, 3, PointerUpdateKind.RightButtonReleased)]
	public void StartupReleaseIsCompletedWithoutRestoringGrab(bool grabReleased, int button, PointerUpdateKind update)
	{
		var host = new X11XamlRootHost { ButtonState = 0 };
		var pointer = new X11PointerInputSource(host);
		var properties = new PointerPointProperties
		{
			IsLeftButtonPressed = button == 1,
			IsMiddleButtonPressed = button == 2,
			IsRightButtonPressed = button == 3
		};
		pointer.Press(new PointerPoint(1, 1, PointerDevice.For(PointerDeviceType.Mouse), 1, default, default, true, properties));
		var releases = 0;
		pointer.PointerReleased += (_, args) =>
		{
			Assert.IsFalse(pointer.IsNativeDragActive);
			Assert.IsFalse(args.CurrentPoint.IsInContact);
			Assert.AreEqual(update, args.CurrentPoint.Properties.PointerUpdateKind);
			releases++;
		};
		pointer.RollbackNativeDrag(pointer.BeginNativeDrag(1, default), grabReleased);
		host.Drain();
		Assert.AreEqual(1, releases);
		Assert.AreEqual(0, pointer.PressedButtons);
		Assert.IsFalse(pointer.PreviousProperties!.HasPressedButton);
		Assert.AreEqual(0, host.RestoredGrabs);
		Assert.IsFalse(host.HasRestoredGrab);
	}

	[TestMethod]
	public void RollbackReconcilesReleasedButtonWhileAnotherRemainsPressed()
	{
		var host = new X11XamlRootHost { ButtonState = 0x400 };
		var pointer = new X11PointerInputSource(host);
		pointer.Press(new PointerPoint(1, 1, PointerDevice.For(PointerDeviceType.Mouse), 1, default, default, true,
			new PointerPointProperties { IsLeftButtonPressed = true, IsRightButtonPressed = true }));
		var releases = 0;
		pointer.PointerReleased += (_, args) =>
		{
			Assert.IsTrue(args.CurrentPoint.Properties.IsRightButtonPressed);
			Assert.IsFalse(args.CurrentPoint.Properties.IsLeftButtonPressed);
			releases++;
		};
		pointer.RollbackNativeDrag(pointer.BeginNativeDrag(1, default), true);
		host.Drain();
		Assert.AreEqual(8, pointer.PressedButtons);
		Assert.IsTrue(host.HasRestoredGrab);
		Assert.AreEqual(1, releases);
	}

	[TestMethod]
	public void ReleaseDuringGrabRestorationReleasesTheExplicitGrab()
	{
		var host = new X11XamlRootHost();
		host.OnRestore = () => host.ButtonState = 0;
		var pointer = new X11PointerInputSource(host);
		pointer.Press(new PointerPoint(1, 1, PointerDevice.For(PointerDeviceType.Mouse), 1, default, default, true,
			new PointerPointProperties { IsLeftButtonPressed = true }));
		var releases = 0;
		pointer.PointerReleased += (_, _) => releases++;
		pointer.RollbackNativeDrag(pointer.BeginNativeDrag(1, default), true);
		host.Drain();
		Assert.AreEqual(1, host.RestoredGrabs);
		Assert.IsFalse(host.HasRestoredGrab);
		Assert.AreEqual(1, releases);
		Assert.AreEqual(0, pointer.PressedButtons);
	}

	[TestMethod]
	public void StaleRollbackCannotRestoreGrabOverNewDrag()
	{
		var host = new X11XamlRootHost();
		var pointer = new X11PointerInputSource(host);
		var first = pointer.BeginNativeDrag(1, default);
		pointer.RollbackNativeDrag(first, true);
		var second = pointer.BeginNativeDrag(1, default);
		host.Drain();
		Assert.IsTrue(pointer.IsNativeDragActive);
		Assert.AreEqual(0, host.RestoredGrabs);
		pointer.EndNativeDrag(second);
		host.Drain();
		Assert.IsFalse(pointer.IsNativeDragActive);
	}

	[TestMethod]
	public void DeferredAcceptanceAcknowledgesStationaryReleaseOnce()
	{
		var target = new X11DragTargetStatus();
		var source = new X11DragSourceState();
		var acknowledgements = 0;
		void Acknowledge(DataPackageOperation accepted)
		{
			acknowledgements++;
			source.Status(accepted != DataPackageOperation.None, accepted, DataPackageOperation.Copy);
		}
		source.PositionChanged();
		source.PositionSent(0, DataPackageOperation.Copy);
		Assert.IsTrue(target.ProcessPosition(() => { }));
		Assert.AreEqual(0, acknowledgements);
		source.Release(10);
		Assert.AreEqual(X11DragReleaseDecision.Wait, source.ReleaseDecision(20));
		target.CompletePosition(DataPackageOperation.Copy, Acknowledge);
		Assert.AreEqual(X11DragReleaseDecision.Drop, source.ReleaseDecision(30));
		target.CompletePosition(DataPackageOperation.Copy, Acknowledge);
		Assert.AreEqual(1, acknowledgements);
	}

	[TestMethod]
	public void SynchronousAcceptanceCannotAcknowledgeQueuedSecondPositionTwice()
	{
		var target = new X11DragTargetStatus();
		var source = new X11DragSourceState();
		var acknowledgements = 0;
		void Acknowledge(DataPackageOperation accepted)
		{
			acknowledgements++;
			source.Status(accepted != DataPackageOperation.None, accepted, DataPackageOperation.Copy);
			if (source.CanSendPosition(10))
			{
				source.PositionSent(10, DataPackageOperation.Copy);
			}
		}
		source.PositionChanged();
		source.PositionSent(0, DataPackageOperation.Copy);
		source.PositionChanged();
		Assert.IsTrue(target.ProcessPosition(() => target.CompletePosition(DataPackageOperation.Copy, Acknowledge)));
		Assert.AreEqual(1, acknowledgements);
		Assert.IsTrue(source.Outstanding);
		Assert.IsTrue(target.ProcessPosition(() => { }));
		source.Release(20);
		Assert.AreEqual(X11DragReleaseDecision.Wait, source.ReleaseDecision(30));
		target.CompletePosition(DataPackageOperation.None, Acknowledge);
		Assert.AreEqual(2, acknowledgements);
		Assert.AreEqual(X11DragReleaseDecision.Leave, source.ReleaseDecision(40));
	}

	[TestMethod]
	public void DeferredLeaveDoesNotRouteReentryToTheOldSession()
	{
		var old = new X11DragDropExtension.XdndSession(5, 3, [], DataPackageOperation.Copy);
		var reentry = new X11DragDropExtension.XdndSession(5, 3, [], DataPackageOperation.Copy);
		var registered = new[] { old, reentry };
		Assert.IsTrue(old.Status.ProcessPosition(() => { }));
		Assert.AreNotEqual(old.PointerId, reentry.PointerId);
		var acknowledgements = 0;
		void Move()
		{
			var source = reentry.CreateEventSource(default);
			var operation = registered.First(session => session.PointerId == source.Id);
			if (operation == reentry)
			{
				operation.Status.CompletePosition(DataPackageOperation.Copy, _ => acknowledgements++);
			}
		}
		Assert.IsTrue(reentry.Status.ProcessPosition(Move));
		Assert.AreEqual(1, acknowledgements);
		old.Status.CompletePosition(DataPackageOperation.None, _ => { });
		Assert.IsTrue(reentry.Status.ProcessPosition(Move));
		Assert.AreEqual(2, acknowledgements);
		Assert.AreEqual(reentry.PointerId, reentry.CreateEventSource(new(1, 2)).Id);
	}

	[TestMethod]
	public void ImmediateFirstIncrementSurvivesHeaderAcknowledgement()
	{
		using var connection = new SelectionOwner();
		connection.Properties.Enqueue(new(connection.IncrementalType, 32, new byte[4]));
		connection.Properties.Enqueue(new((IntPtr)2, 8, new byte[] { 1, 2, 3 }));
		connection.Properties.Enqueue(new((IntPtr)2, 8, new byte[] { 4, 5 }));
		connection.Properties.Enqueue(new((IntPtr)2, 8, Array.Empty<byte>()));
		CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, Read(connection));
		Assert.AreEqual(4, connection.PropertyReads);
	}

	[TestMethod]
	public async Task PrivateConnectionsInternAtomsWithoutSharedCache()
	{
		using var barrier = new Barrier(2);
		var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
		{
			using var display = new XLib.SelectionDisplay { AtomBarrier = barrier };
			using var connection = new X11SelectionConnection();
			CollectionAssert.AreEqual(new[] { "_UNO_SELECTION", "INCR" }, display.Atoms);
			Assert.AreEqual((IntPtr)2, connection.IncrementalType);
			return display;
		})).ToArray();
		foreach (var display in await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)))
		{
			Assert.AreEqual(1, display.Closed);
			Assert.AreEqual(1, display.Destroyed);
		}
	}

	[TestMethod]
	[DataRow("window")]
	[DataRow("_UNO_SELECTION")]
	[DataRow("INCR")]
	[DataRow("input")]
	public void SelectionInitializationFailureClosesTheDisplay(string failure)
	{
		using var display = new XLib.SelectionDisplay { Failure = failure };
		Assert.ThrowsExactly<InvalidOperationException>(() => new X11SelectionConnection());
		Assert.AreEqual(1, display.Closed);
	}

	[TestMethod]
	public void MissingPropertyNotificationDoesNotTerminateIncrementalRead()
	{
		using var connection = new SelectionOwner();
		connection.Properties.Enqueue(new(connection.IncrementalType, 32, new byte[4]));
		connection.Properties.Enqueue(new(IntPtr.Zero, 0, Array.Empty<byte>()));
		connection.Properties.Enqueue(new((IntPtr)2, 8, new byte[] { 9 }));
		connection.Properties.Enqueue(new((IntPtr)2, 8, Array.Empty<byte>()));
		CollectionAssert.AreEqual(new byte[] { 9 }, Read(connection));
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public void CancellationInterruptsSelectionAndIncrementWaits(bool incremental)
	{
		using var cancellation = new CancellationTokenSource();
		using var connection = new SelectionOwner { Notify = incremental, OnWait = cancellation.Cancel };
		if (incremental)
		{
			connection.Properties.Enqueue(new(connection.IncrementalType, 32, new byte[4]));
		}
		Assert.ThrowsExactly<OperationCanceledException>(() => Read(connection, cancellation.Token));
		Assert.AreEqual(1, connection.Waits);
	}

	[TestMethod]
	public async Task LazyReadPassesCancellationToTransferAndDisposesConnection()
	{
		using var cancellation = new CancellationTokenSource();
		var session = new X11SelectionReadSession((IntPtr)3) { Timestamp = (IntPtr)100 };
		var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var connection = new SelectionOwner { Notify = false };
		byte[]? Retrieve(IntPtr owner, IntPtr time, CancellationToken ct)
		{
			connection.OnWait = () =>
			{
				cancellation.Cancel();
				Assert.IsTrue(ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
			};
			try
			{
				using (connection)
				{
					Assert.IsTrue(ct.CanBeCanceled);
					return X11SelectionReader.Read(connection, (IntPtr)1, (IntPtr)2, time, owner, ct);
				}
			}
			finally
			{
				stopped.SetResult(true);
			}
		}
		var read = session.GetBytesAsync((IntPtr)2, Retrieve, cancellation.Token);
		await Assert.ThrowsAsync<OperationCanceledException>(async () => await read);
		await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.IsTrue(connection.Disposed);
		Assert.AreEqual(1, connection.Waits);
	}

	[TestMethod]
	public async Task CanceledCallerDoesNotCancelAnotherCallersTransfer()
	{
		var session = new X11SelectionReadSession((IntPtr)3);
		using var cancellation = new CancellationTokenSource();
		using var finish = new ManualResetEventSlim();
		var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		var reads = 0;
		byte[]? Retrieve(IntPtr owner, IntPtr time, CancellationToken ct)
		{
			Interlocked.Increment(ref reads);
			entered.SetResult(ct);
			Assert.IsTrue(finish.Wait(TimeSpan.FromSeconds(5), ct));
			return new byte[] { 7 };
		}
		var first = session.GetBytesAsync((IntPtr)2, Retrieve, cancellation.Token);
		var transferToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		var second = session.GetBytesAsync((IntPtr)2, Retrieve, default);
		try
		{
			cancellation.Cancel();
			await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);
			Assert.IsFalse(transferToken.IsCancellationRequested);
		}
		finally
		{
			finish.Set();
		}
		CollectionAssert.AreEqual(new byte[] { 7 }, await second.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.AreEqual(1, reads);
	}

	[TestMethod]
	[DataRow("timeout")]
	[DataRow("fault")]
	[DataRow("canceled")]
	public async Task FailedReadCanRetryAtDropAndSuccessfulPayloadIsCached(string failure)
	{
		var session = new X11SelectionReadSession((IntPtr)3) { Timestamp = (IntPtr)100 };
		var reads = 0;
		byte[]? Retrieve(IntPtr owner, IntPtr time, CancellationToken ct)
		{
			if (++reads == 1)
			{
				if (failure == "fault")
				{
					throw new InvalidOperationException();
				}
				if (failure == "canceled")
				{
					throw new OperationCanceledException(new CancellationToken(true));
				}
				using var silent = new SelectionOwner { Notify = false };
				return X11SelectionReader.Read(silent, (IntPtr)1, (IntPtr)2, time, owner, ct);
			}
			Assert.AreEqual((IntPtr)200, time);
			return new byte[] { 9 };
		}
		if (failure == "fault")
		{
			await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.GetBytesAsync((IntPtr)2, Retrieve, default));
		}
		else if (failure == "canceled")
		{
			await Assert.ThrowsAsync<OperationCanceledException>(async () => await session.GetBytesAsync((IntPtr)2, Retrieve, default));
		}
		else
		{
			Assert.IsNull(await session.GetBytesAsync((IntPtr)2, Retrieve, default));
		}
		session.Timestamp = (IntPtr)200;
		CollectionAssert.AreEqual(new byte[] { 9 }, await session.GetBytesAsync((IntPtr)2, Retrieve, default));
		CollectionAssert.AreEqual(new byte[] { 9 }, await session.GetBytesAsync((IntPtr)2, Retrieve, default));
		Assert.AreEqual(2, reads);
	}

	[TestMethod]
	public async Task CanceledLastCallerCanRetryBeforePreviousWorkerStops()
	{
		var session = new X11SelectionReadSession((IntPtr)3);
		using var cancellation = new CancellationTokenSource();
		using var finish = new ManualResetEventSlim();
		var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var first = session.GetBytesAsync((IntPtr)2, (_, _, ct) =>
		{
			entered.SetResult(true);
			try
			{
				Assert.IsTrue(finish.Wait(TimeSpan.FromSeconds(5)));
				ct.ThrowIfCancellationRequested();
				return null;
			}
			finally
			{
				stopped.SetResult(true);
			}
		}, cancellation.Token);
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		try
		{
			cancellation.Cancel();
			await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);
			CollectionAssert.AreEqual(new byte[] { 8 }, await session.GetBytesAsync((IntPtr)2, (_, _, _) => new byte[] { 8 }, default));
		}
		finally
		{
			finish.Set();
		}
		await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
		CollectionAssert.AreEqual(new byte[] { 8 }, await session.GetBytesAsync((IntPtr)2, (_, _, _) => throw new AssertFailedException(), default));
	}

	[TestMethod]
	public void ContinuousIncrementalTrafficStillHasOverallDeadline()
	{
		using var connection = new SelectionOwner { EndlessChunks = true };
		connection.Properties.Enqueue(new(connection.IncrementalType, 32, new byte[4]));
		Assert.IsNull(Read(connection));
		Assert.IsTrue(connection.TickCount >= 10_000);
		Assert.IsTrue(connection.PropertyReads < 20);
	}

	[TestMethod]
	public void SilentOwnerTimesOutAndIndependentReaderStillRuns()
	{
		using var blocked = new SelectionOwner { Notify = false };
		using var other = new SelectionOwner();
		other.Properties.Enqueue(new((IntPtr)2, 8, new byte[] { 7 }));
		blocked.OnWait = () =>
		{
			if (blocked.Waits == 1)
			{
				CollectionAssert.AreEqual(new byte[] { 7 }, Read(other));
			}
		};
		Assert.IsNull(Read(blocked));
		Assert.AreEqual(2_000, blocked.TickCount);
	}

	[TestMethod]
	public async Task DelayedReadsUseDropTimestampAndCachedDataSurvivesOwnerChange()
	{
		var session = new X11SelectionReadSession((IntPtr)3) { Timestamp = (IntPtr)100 };
		session.Timestamp = (IntPtr)200;
		using var connection = new SelectionOwner();
		connection.Properties.Enqueue(new((IntPtr)2, 8, new byte[] { 1 }));
		byte[]? Retrieve(IntPtr owner, IntPtr time, CancellationToken ct)
			=> X11SelectionReader.Read(connection, (IntPtr)1, (IntPtr)2, time, owner, ct);
		CollectionAssert.AreEqual(new byte[] { 1 }, await session.GetBytesAsync((IntPtr)2, Retrieve, default));
		Assert.AreEqual((IntPtr)200, connection.Timestamp);
		connection.Owner = (IntPtr)4;
		CollectionAssert.AreEqual(new byte[] { 1 }, await session.GetBytesAsync((IntPtr)2, Retrieve, default));
		Assert.AreEqual(1, connection.Conversions);
		Assert.IsNull(await session.GetBytesAsync((IntPtr)5, Retrieve, default));
		Assert.AreEqual(1, connection.Conversions);
	}

	[TestMethod]
	public void OwnerChangeDuringTransferRejectsNewDragData()
	{
		using var connection = new SelectionOwner { Notify = false };
		connection.OnWait = () => connection.Owner = (IntPtr)4;
		Assert.IsNull(Read(connection));
		Assert.AreEqual(0, connection.PropertyReads);
	}

	[TestMethod]
	public void TypeListSurvivesLeaveAndReentry()
	{
		var published = new[] { (IntPtr)1, (IntPtr)2, (IntPtr)3, (IntPtr)4, IntPtr.Zero };
		var property = published;
		IntPtr[] ReadProperty() => property;
		var first = X11DragTypeList.Read(true, 0, 0, 0, ReadProperty);
		var reentry = X11DragTypeList.Read(true, 0, 0, 0, ReadProperty);
		Assert.AreEqual(4, first.Length);
		CollectionAssert.AreEqual(first, reentry);
		CollectionAssert.AreEqual(published, property);
	}

	[TestMethod]
	public void InlineTypesDoNotReadTheTypeListProperty()
	{
		CollectionAssert.AreEqual(new[] { (IntPtr)1, (IntPtr)3 },
			X11DragTypeList.Read(false, 1, 0, 3, () => throw new AssertFailedException()));
	}

	[TestMethod]
	public void SessionFinishAndSelectionClearRetireTheWindowOnce()
	{
		var selection = new X11DragSelection((IntPtr)3, (IntPtr)100, new()
		{
			[(IntPtr)1] = Task.FromResult<byte[]?>(new byte[] { 8 })
		});
		var destroyed = new List<IntPtr>();
		selection.RetainCompleted(100);
		selection.Retire(destroyed.Add);
		selection.Retire(destroyed.Add);
		Assert.IsTrue(selection.IsRetired);
		CollectionAssert.AreEqual(new[] { (IntPtr)3 }, destroyed);
		Assert.AreEqual(0, selection.Content.Count);
	}

	[TestMethod]
	public void RetiringPreviousSelectionDoesNotRetireItsReplacement()
	{
		var previous = new X11DragSelection((IntPtr)3, (IntPtr)100, new());
		var replacement = new X11DragSelection((IntPtr)4, (IntPtr)200, new()
		{
			[(IntPtr)1] = Task.FromResult<byte[]?>(new byte[] { 9 })
		});
		var destroyed = new List<IntPtr>();
		previous.Retire(destroyed.Add);
		previous.Retire(destroyed.Add);
		Assert.IsFalse(replacement.IsRetired);
		Assert.AreEqual(1, replacement.Content.Count);
		replacement.Retire(destroyed.Add);
		CollectionAssert.AreEqual(new[] { (IntPtr)3, (IntPtr)4 }, destroyed);
	}

	[TestMethod]
	public void CompletedSelectionReleasesProviderStateAndExpires()
	{
		var (selection, ui) = CompleteSelection();
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		Assert.IsFalse(ui.IsAlive);
		Assert.AreEqual(1, selection.Content.Count);
		CollectionAssert.AreEqual(new byte[] { 8 }, selection.Content[(IntPtr)1].Result!);
		Assert.IsFalse(selection.IsExpired(2_099));
		Assert.IsTrue(selection.IsExpired(2_100));
		GC.KeepAlive(selection);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static (X11DragSelection selection, WeakReference ui) CompleteSelection()
	{
		var ui = new object();
		var pending = new TaskCompletionSource<byte[]?>(ui);
		var completed = new TaskCompletionSource<byte[]?>(ui);
		completed.SetResult(new byte[] { 8 });
		var selection = new X11DragSelection((IntPtr)3, (IntPtr)100, new()
		{
			[(IntPtr)1] = completed.Task,
			[(IntPtr)2] = pending.Task
		});
		selection.RetainCompleted(100);
		return (selection, new WeakReference(ui));
	}

	private static byte[]? Read(SelectionOwner connection, CancellationToken ct = default)
		=> X11SelectionReader.Read(connection, (IntPtr)1, (IntPtr)2, (IntPtr)100, (IntPtr)3, ct);

	private sealed class SelectionOwner : IX11SelectionConnection
	{
		internal readonly Queue<X11SelectionProperty> Properties = new();
		internal bool Notify = true;
		internal bool EndlessChunks;
		internal Action? OnWait;
		internal IntPtr Owner = (IntPtr)3;
		internal IntPtr Timestamp;
		internal int PropertyReads;
		internal int Conversions;
		internal int Waits;
		internal bool Disposed;
		public long TickCount { get; private set; }
		public IntPtr IncrementalType => (IntPtr)99;
		public IntPtr GetOwner(IntPtr selection) => Owner;
		public void Convert(IntPtr selection, IntPtr format, IntPtr timestamp)
		{
			Timestamp = timestamp;
			Conversions++;
		}
		public bool TrySelectionNotify(IntPtr selection, IntPtr format, out bool accepted)
		{
			accepted = Notify;
			return Notify;
		}
		public bool TryPropertyNotify() => Properties.Count > 0 || EndlessChunks;
		public X11SelectionProperty ReadProperty()
		{
			PropertyReads++;
			if (EndlessChunks)
			{
				TickCount += 1_000;
			}
			return Properties.TryDequeue(out var property) ? property : new((IntPtr)2, 8, new byte[] { 1 });
		}
		public void Wait(CancellationToken cancellationToken)
		{
			TickCount += 10;
			Waits++;
			OnWait?.Invoke();
			cancellationToken.ThrowIfCancellationRequested();
		}
		public void Dispose()
		{
			Disposed = true;
		}
	}
}

// The pointer handoff partial runs against a queued host without opening an X display.
internal sealed class X11XamlRootHost
{
	internal readonly System.Collections.Generic.Queue<Action> Actions = new();
	internal int RestoredGrabs;
	internal uint ButtonState = 0x100;
	internal bool HasRestoredGrab;
	internal Action? OnRestore;
	internal uint GetPointerButtonState() => ButtonState;
	internal static void QueueAction(X11XamlRootHost host, Action action) => host.Actions.Enqueue(action);
	internal void RestorePointerGrabAfterDrag()
	{
		RestoredGrabs++;
		HasRestoredGrab = true;
		OnRestore?.Invoke();
	}
	internal void ReleaseRestoredPointerGrab() => HasRestoredGrab = false;
	internal void Drain()
	{
		while (Actions.TryDequeue(out var action))
		{
			action();
		}
	}
}

internal partial class X11PointerInputSource
{
	private readonly X11XamlRootHost _host;
	private int _pressedButtons;
	private PointerPointProperties? _previousPointerPointProperties;
	internal event EventHandler<PointerEventArgs>? PointerCancelled;
	internal event EventHandler<PointerEventArgs>? PointerCaptureLost;
	internal event EventHandler<PointerEventArgs>? PointerReleased;
	private void RaisePointerReleased(PointerEventArgs args) => PointerReleased?.Invoke(this, args);

	internal X11PointerInputSource(X11XamlRootHost host) => _host = host;
	internal int PressedButtons => _pressedButtons;
	internal PointerPointProperties? PreviousProperties => _previousPointerPointProperties;
	internal void Press(PointerPoint point)
	{
		_lastPoint = point;
		_pressedButtons = (point.Properties.IsLeftButtonPressed ? 2 : 0)
			| (point.Properties.IsMiddleButtonPressed ? 4 : 0)
			| (point.Properties.IsRightButtonPressed ? 8 : 0);
		_previousPointerPointProperties = point.Properties;
	}

	internal bool InternalDrop()
	{
		if (IsNativeDragActive || _pressedButtons != 2 || _previousPointerPointProperties?.IsLeftButtonPressed != true)
		{
			return false;
		}
		_pressedButtons = 0;
		return true;
	}
}

// The connection constructor uses these bindings without opening a real Display.
internal static class XLib
{
	private static readonly AsyncLocal<SelectionDisplay?> _current = new();
	internal sealed class SelectionDisplay : IDisposable
	{
		private readonly SelectionDisplay? _previous = _current.Value;
		internal readonly List<string> Atoms = new();
		internal Barrier? AtomBarrier;
		internal string? Failure;
		internal int Closed;
		internal int Destroyed;
		internal SelectionDisplay() => _current.Value = this;
		internal void Check(string step)
		{
			if (Failure == step)
			{
				throw new InvalidOperationException();
			}
		}
		public void Dispose() => _current.Value = _previous;
	}

	internal static IntPtr XOpenDisplay(IntPtr name) => (IntPtr)1;
	internal static IntPtr XDefaultRootWindow(IntPtr display) => (IntPtr)2;
	internal static IntPtr XCreateSimpleWindow(IntPtr display, IntPtr parent, int x, int y, uint width, uint height,
		uint borderWidth, IntPtr border, IntPtr background)
	{
		_current.Value!.Check("window");
		return (IntPtr)3;
	}
	internal static IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists)
	{
		var state = _current.Value!;
		state.Check(name);
		Assert.IsFalse(onlyIfExists);
		if (name == "_UNO_SELECTION" && state.AtomBarrier is { } barrier)
		{
			Assert.IsTrue(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
		}
		state.Atoms.Add(name);
		return (IntPtr)state.Atoms.Count;
	}
	internal static void XSelectInput(IntPtr display, IntPtr window, IntPtr mask) => _current.Value!.Check("input");
	internal static void XDestroyWindow(IntPtr display, IntPtr window) => _current.Value!.Destroyed++;
	internal static void XCloseDisplay(IntPtr display) => _current.Value!.Closed++;
	internal static IntPtr XGetSelectionOwner(IntPtr display, IntPtr selection) => throw new NotSupportedException();
	internal static void XConvertSelection(IntPtr display, IntPtr selection, IntPtr format, IntPtr property, IntPtr window, IntPtr timestamp)
		=> throw new NotSupportedException();
	internal static void XFlush(IntPtr display) => throw new NotSupportedException();
	internal static void XFree(IntPtr data) => throw new NotSupportedException();
	internal static void XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr offset, IntPtr length,
		bool delete, IntPtr requestedType, out IntPtr type, out int format, out IntPtr count, out IntPtr remaining, out IntPtr data)
		=> throw new NotSupportedException();
}

internal static class X11Helper
{
	internal const string INCR = "INCR";
	internal static readonly IntPtr LONG_LENGTH = (IntPtr)0x1fffffff;
	internal static readonly IntPtr AnyPropertyType = IntPtr.Zero;
	internal const int PropertyNewValue = 0;
	internal static IntPtr GetAtom(IntPtr display, string name) => throw new AssertFailedException("Private readers must not use the shared atom cache.");
	internal static bool XCheckTypedWindowEvent(IntPtr display, IntPtr window, int type, out XEvent ev) => throw new NotSupportedException();
}

internal enum XEventMask
{
	PropertyChangeMask = 1 << 22
}

internal enum XEventName
{
	SelectionNotify,
	PropertyNotify
}

internal readonly struct XEvent
{
	internal XSelectionEvent SelectionEvent => default;
	internal XPropertyEvent PropertyEvent => default;
}
internal readonly struct XSelectionEvent
{
	internal IntPtr selection => default;
	internal IntPtr target => default;
	internal IntPtr property => default;
}
internal readonly struct XPropertyEvent
{
	internal IntPtr atom => default;
	internal int state => default;
}
