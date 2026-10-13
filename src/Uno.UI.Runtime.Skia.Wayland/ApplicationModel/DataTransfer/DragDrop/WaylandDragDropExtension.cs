// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Uno.Extensions;
using Uno.Foundation.Logging;
using Uno.UI.Hosting;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.ApplicationModel.DataTransfer.DragDrop.Core;
using Windows.Foundation;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// Drops from other clients onto one window. All members run on the UI thread.
internal sealed class WaylandDragDropExtension : IDragDropExtension
{
	private static readonly long _fakePointerId = Pointer.CreateUniqueIdForUnknownPointer();
	private static readonly TimeSpan _acceptancePollInterval = TimeSpan.FromMilliseconds(100);

	private readonly DragDropManager _manager;
	private readonly WaylandDataDevice _device;
	private Session? _session;
	// Wayland sends motion only while the pointer moves, so a DragOver deferral that completes later is polled.
	private Timer? _acceptancePoll;

	public WaylandDragDropExtension(DragDropManager manager, WaylandDataDevice device)
	{
		_manager = manager;
		_device = device;
		device.AddDropTarget(this);
		XamlRootMap.Unregistered += OnXamlRootUnregistered;
	}

	private XamlRoot XamlRoot => _manager.ContentRoot.GetOrCreateXamlRoot();

	internal WlSurface? Surface => (XamlRootMap.GetHostForRoot(XamlRoot) as WaylandXamlRootHost)?.Surface;

	private CoreDragDropManager CoreDragDropManager => XamlRoot.GetCoreDragDropManager(XamlRoot);

	internal void Enter(WaylandDataOffer offer, uint serial, Point position)
	{
		var package = new DataPackage();
		WaylandDataFormats.Fill(package, offer);
		// Offers before wl_data_offer v3 cannot negotiate actions and always copy.
		var operations = offer.Version >= 3 ? WaylandDataFormats.ToOperations(offer.SourceActions) : DataPackageOperation.Copy;
		var session = new Session(offer, serial);
		_session = session;

		if (this.Log().IsEnabled(LogLevel.Trace))
		{
			this.Log().Trace($"Drag entered with operations {operations} and formats {string.Join(", ", offer.MimeTypes)}.");
		}

		var info = new CoreDragInfo(new DragEventSource(position), package.GetView(), operations);
		info.RegisterCompletedCallback(result => Complete(session, result));
		CoreDragDropManager.DragStarted(info);
		Motion(position);
		StopAcceptancePoll();
		_acceptancePoll = new Timer(_ => WaylandConnection.PostToUI(PollAcceptance), null, _acceptancePollInterval, _acceptancePollInterval);
	}

	internal void Motion(Point position)
	{
		if (_session is not { } session)
		{
			return;
		}
		session.Position = position;
		UpdateAcceptance(session, CoreDragDropManager.ProcessMoved(new DragEventSource(position)));
	}

	private void PollAcceptance()
	{
		if (_session is { } session)
		{
			// An already processed frame id only reads the operation accepted by the latest DragOver.
			UpdateAcceptance(session, CoreDragDropManager.ProcessMoved(new DragEventSource(session.Position, frameId: 0)));
		}
	}

	private void UpdateAcceptance(Session session, DataPackageOperation accepted)
	{
		if (accepted != session.Accepted)
		{
			session.Accepted = accepted;
			// A single action is offered, so the action the compositor selects is the one the target accepted.
			var sourceActions = session.Offer.Version >= 3 ? session.Offer.SourceActions : WaylandDataFormats.DndActionCopy;
			var action = WaylandDropNegotiation.ChooseAction(WaylandDataFormats.ToDndActions(accepted), sourceActions);
			session.Offer.Accept(session.Serial, action != 0 && session.Offer.MimeTypes is [var mime, ..] ? mime : null, action, action);
		}
	}

	private void StopAcceptancePoll()
	{
		_acceptancePoll?.Dispose();
		_acceptancePoll = null;
	}

	internal void Leave()
	{
		if (_session is { } session)
		{
			_session = null;
			StopAcceptancePoll();
			CoreDragDropManager.ProcessAborted(_fakePointerId);
			Complete(session, DataPackageOperation.None);
		}
	}

	internal void Drop()
	{
		if (_session is not { } session)
		{
			return;
		}
		_session = null;
		StopAcceptancePoll();
		session.Dropped = true;
		session.DropAction = session.Offer.MarkDropped();
		if (session.DropAction == 0)
		{
			CoreDragDropManager.ProcessAborted(_fakePointerId);
			return;
		}
		var accepted = CoreDragDropManager.ProcessDropped(new DragEventSource(session.Position));

		if (this.Log().IsEnabled(LogLevel.Trace))
		{
			this.Log().Trace($"Drop processed, last accepted operation {accepted}.");
		}
	}

	// Called once the drop handlers completed (or the drag was aborted). The offer stays alive until
	// the data they requested has been received.
	private static void Complete(Session session, DataPackageOperation result)
	{
		if (session.Completed)
		{
			return;
		}
		session.Completed = true;
		// wl_data_offer.finish is only valid once an action was negotiated, and lets the source delete moved data,
		// so it is sent only when the drop handlers performed the selected action.
		var succeeded = session.Dropped && session.DropAction != 0 && (WaylandDataFormats.ToDndActions(result) & session.DropAction) != 0;
		_ = session.Offer.WhenReadsCompleted().ContinueWith(reads =>
		{
			if (succeeded && reads.Status == TaskStatus.RanToCompletion && reads.Result)
			{
				session.Offer.FinishAndDestroy(session.DropAction);
			}
			else
			{
				session.Offer.Destroy();
			}
		}, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
	}

	// Drags leaving the window keep the implicit pointer grab on Wayland, so an internal drag simply
	// continues and can come back. Like X11, dragging to other applications is not supported.
	public void StartNativeDrag(CoreDragInfo info, Action<DataPackageOperation> onCompleted) { }

	private void OnXamlRootUnregistered(object? sender, XamlRoot root)
	{
		if (root == XamlRoot)
		{
			XamlRootMap.Unregistered -= OnXamlRootUnregistered;
			StopAcceptancePoll();
			_device.RemoveDropTarget(this);
		}
	}

	private sealed class Session(WaylandDataOffer offer, uint serial)
	{
		public WaylandDataOffer Offer { get; } = offer;
		public uint Serial { get; } = serial;
		public Point Position { get; set; }
		// Null until the first motion was answered, so the first answer is always sent.
		public DataPackageOperation? Accepted { get; set; }
		public bool Completed { get; set; }
		public bool Dropped { get; set; }
		public uint DropAction { get; set; }
	}

	private readonly struct DragEventSource(Point location, uint? frameId = null) : IDragEventSource
	{
		private static long _nextFrameId;

		public long Id => _fakePointerId;

		public uint FrameId { get; } = frameId ?? (uint)Interlocked.Increment(ref _nextFrameId);

		public (Point location, DragDropModifiers modifier) GetState() => (location, DragDropModifiers.None);

		public Point GetPosition(object? relativeTo)
		{
			if (relativeTo is null)
			{
				return location;
			}

			if (relativeTo is UIElement element)
			{
				return UIElement.GetTransform(element, null).Inverse().Transform(location);
			}

			throw new InvalidOperationException("The relative to must be a UIElement.");
		}
	}
}
