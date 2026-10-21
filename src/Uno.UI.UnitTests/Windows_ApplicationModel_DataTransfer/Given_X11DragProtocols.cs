// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
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
		Assert.AreSame(properties, pointer.PreviousProperties);
		Assert.AreEqual(grabReleased ? 1 : 0, host.RestoredGrabs);
		Assert.AreEqual(0, cancelled);
		Assert.AreEqual(0, captureLost);
		Assert.IsTrue(pointer.InternalDrop());
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
		var connection = new SelectionOwner { Notify = false, OnWait = cancellation.Cancel };
		byte[]? Retrieve(IntPtr owner, IntPtr time, CancellationToken ct)
		{
			try
			{
				using (connection)
				{
					Assert.AreEqual(cancellation.Token, ct);
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
		IntPtr[] ReadProperty(bool delete)
		{
			var result = property;
			if (delete)
			{
				property = Array.Empty<IntPtr>();
			}
			return result;
		}
		var first = X11DragTypeList.Read(true, 0, 0, 0, ReadProperty);
		var reentry = X11DragTypeList.Read(true, 0, 0, 0, ReadProperty);
		Assert.AreEqual(4, first.Length);
		CollectionAssert.AreEqual(first, reentry);
		CollectionAssert.AreEqual(published, property);
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
	internal static void QueueAction(X11XamlRootHost host, Action action) => host.Actions.Enqueue(action);
	internal void RestorePointerGrabAfterDrag() => RestoredGrabs++;
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

	internal X11PointerInputSource(X11XamlRootHost host) => _host = host;
	internal int PressedButtons => _pressedButtons;
	internal PointerPointProperties? PreviousProperties => _previousPointerPointProperties;
	internal void Press(PointerPoint point)
	{
		_lastPoint = point;
		_pressedButtons = 2;
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
