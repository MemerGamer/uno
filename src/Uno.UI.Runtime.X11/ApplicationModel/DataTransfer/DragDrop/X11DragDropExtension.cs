// Copyright Edward Rosten 2006--2013.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions
// are met:
// 1. Redistributions of source code must retain the above copyright
//    notice, this list of conditions and the following disclaimer.
// 2. Redistributions in binary form must reproduce the above copyright
//    notice, this list of conditions and the following disclaimer in the
//    documentation and/or other materials provided with the distribution.
//
// THIS SOFTWARE IS PROVIDED BY THE AUTHOR AND OTHER CONTRIBUTORS ``AS IS''
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED.  IN NO EVENT SHALL THE AUTHOR OR OTHER CONTRIBUTORS BE
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.

// https://github.com/edrosten/x_clipboard/blob/164edd5d530cca12f65f99e22741d9b652e5bc43/paste.cc

using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.ApplicationModel.DataTransfer.DragDrop.Core;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Uno.Disposables;
using Uno.Extensions;
using Uno.Foundation.Logging;
using Uno.UI.Hosting;
namespace Uno.UI.Runtime.X11;

internal class X11DragDropExtension : IDragDropExtension
{
	private static readonly long _fakePointerId = Pointer.CreateUniqueIdForUnknownPointer();

	private readonly X11XamlRootHost _host;
	private readonly CoreDragDropManager _coreDragDropManager;
	private static readonly TimeSpan _sourceWatchInterval = TimeSpan.FromMilliseconds(250);
	private XdndSession? _currentSession;
	private Timer? _sourceWatch;

	private IntPtr XdndSelection => X11Helper.GetAtom(_host.RootX11Window.Display, X11Helper.XdndSelection);

	public X11DragDropExtension(DragDropManager manager)
	{
		if (manager.ContentRoot.GetOrCreateXamlRoot().HostWindow is not { } window)
		{
			throw new InvalidOperationException($"Couldn't find a window associated with the {nameof(X11DragDropExtension)}");
		}
		_host = X11XamlRootHost.GetHostFromWindow(window) ?? throw new InvalidOperationException($"Couldn't find an {nameof(X11XamlRootHost)} associated with the {nameof(X11DragDropExtension)}");
		_coreDragDropManager = XamlRoot.GetCoreDragDropManager(((IXamlRootHost)_host).RootElement!.XamlRoot);

		var display = _host.RootX11Window.Display;
		using var lockDiposable = X11Helper.XLock(display);
		_ = XLib.XChangeProperty(
			display,
			_host.RootX11Window.Window,
			X11Helper.GetAtom(display, X11Helper.XdndAware),
			X11Helper.GetAtom(display, X11Helper.XA_ATOM),
			32,
			PropertyMode.Replace,
			new IntPtr[] { 5 }, // version 5
			1);
		_ = XLib.XFlush(display);

		_host.SetDragDropExtension(this);
		// Once the window closed, queued UI work may no longer run: the watch timer is stopped from the closing thread.
		_ = _host.Closed.ContinueWith(_ => StopSourceWatch(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
	}

	public void ProcessXdndMessage(XClientMessageEvent ev)
	{
		try
		{
			ProcessXdndMessageCore(ev);
		}
		catch (Exception error)
		{
			if (X11DragDiagnostics.IsEnabled)
			{
				X11DragDiagnostics.Trace($"XDnd: handling the message failed: {error}");
			}
			throw;
		}
	}

	private void ProcessXdndMessageCore(XClientMessageEvent ev)
	{
		using var lockDiposable = X11Helper.XLock(_host.RootX11Window.Display);

		if (X11DragDiagnostics.IsEnabled)
		{
			X11DragDiagnostics.Trace($"XDnd EVENT for window {ev.window}: message_type={XLib.GetAtomName(_host.RootX11Window.Display, ev.message_type)} data: {ev.ptr1.ToString("X", CultureInfo.InvariantCulture)}, {ev.ptr2.ToString("X", CultureInfo.InvariantCulture)}, {ev.ptr3.ToString("X", CultureInfo.InvariantCulture)}, {ev.ptr4.ToString("X", CultureInfo.InvariantCulture)}, {ev.ptr5.ToString("X", CultureInfo.InvariantCulture)}");
		}

		if (ev.message_type == X11Helper.GetAtom(_host.RootX11Window.Display, X11Helper.XdndEnter))
		{
			ProcessXdndEnter(ev);
		}
		else if (ev.message_type == X11Helper.GetAtom(_host.RootX11Window.Display, X11Helper.XdndPosition))
		{
			ProcessXdndPosition(ev);
		}
		else if (ev.message_type == X11Helper.GetAtom(_host.RootX11Window.Display, X11Helper.XdndLeave))
		{
			ProcessXdndLeave(ev);
		}
		else if (ev.message_type == X11Helper.GetAtom(_host.RootX11Window.Display, X11Helper.XdndDrop))
		{
			ProcessXdndDrop(ev);
		}
		else
		{
			throw new ArgumentException($"{nameof(ProcessXdndMessage)} only accepts XDnD messages.");
		}
	}

	private unsafe void ProcessXdndEnter(XClientMessageEvent ev)
	{
		var sourceWindow = ev.ptr1;
		var version = ev.ptr2 >> 24;

		if (X11DragDiagnostics.IsEnabled)
		{
			X11DragDiagnostics.Trace($"XDndEnter: version={version}, sourceWindow={sourceWindow}, types in message: [{GetAtomNameSafe(ev.ptr3)}, {GetAtomNameSafe(ev.ptr4)}, {GetAtomNameSafe(ev.ptr5)}]");
		}

		IntPtr[] formats;
		if ((ev.ptr2 & 1) == 0)
		{
			formats = new[] { ev.ptr3, ev.ptr4, ev.ptr5 };
		}
		else
		{
			_ = XLib.XGetWindowProperty(
				_host.RootX11Window.Display,
				sourceWindow,
				X11Helper.GetAtom(_host.RootX11Window.Display, X11Helper.XdndTypeList),
				IntPtr.Zero,
				X11Helper.LONG_LENGTH,
				true,
				X11Helper.AnyPropertyType,
				out IntPtr actualType,
				out var actualFormat,
				out var nitems,
				out var _,
				out var prop);
			using var propDisposable = new DisposableStruct<IntPtr>(static p => { _ = XLib.XFree(p); }, prop);

			if (nitems == 0 || actualFormat != 32 || actualType != X11Helper.GetAtom(_host.RootX11Window.Display, X11Helper.XA_ATOM))
			{
				formats = Array.Empty<IntPtr>();
			}
			else
			{
				formats = new Span<IntPtr>((void*)prop, (int)nitems).ToArray();
			}
		}

		formats = formats.Where(t => t != IntPtr.Zero).ToArray();

		if (X11DragDiagnostics.IsEnabled)
		{
			X11DragDiagnostics.Trace($"XDndEnter: total types received {string.Join(", ", formats.Select(GetAtomNameSafe))}");
		}

		StopSourceWatch();
		_currentSession = new XdndSession(version, sourceWindow, formats, ReadSourceActions(sourceWindow));
	}

	// The XdndActionList of the source, when it publishes one, lists every action it can perform.
	private unsafe DataPackageOperation ReadSourceActions(IntPtr sourceWindow)
	{
		var display = _host.RootX11Window.Display;
		_ = XLib.XGetWindowProperty(display, sourceWindow, X11Helper.GetAtom(display, "XdndActionList"), IntPtr.Zero, X11Helper.LONG_LENGTH, false,
			X11Helper.GetAtom(display, X11Helper.XA_ATOM), out var actualType, out var actualFormat, out var nitems, out _, out var prop);
		using var propDisposable = new DisposableStruct<IntPtr>(static p => { _ = XLib.XFree(p); }, prop);
		var operations = DataPackageOperation.None;
		if (prop != IntPtr.Zero && actualFormat == 32 && actualType == X11Helper.GetAtom(display, X11Helper.XA_ATOM))
		{
			foreach (var action in new Span<IntPtr>((void*)prop, (int)nitems))
			{
				operations |= X11DragActions.FromTargetActionName(GetAtomNameSafe(action));
			}
		}
		return operations;
	}

	private void ProcessXdndPosition(XClientMessageEvent ev)
	{
		if (_currentSession is not { } session)
		{
			if (this.Log().IsEnabled(LogLevel.Error))
			{
				this.Log().Error($"Received a XdndPosition message without a XdndEnter preceding it. Ignoring.");
			}

			return;
		}

		var display = _host.RootX11Window.Display;

		var rootX = (int)(ev.ptr3 >> 16);
		var rootY = (int)(ev.ptr3 & 0xffff);
		XLib.XTranslateCoordinates(display, XLib.XDefaultRootWindow(display), _host.RootX11Window.Window, rootX, rootY, out var x, out var y, out _);
		var scale = ((IXamlRootHost)_host).RootElement?.XamlRoot?.RasterizationScale ?? 1;
		var position = new Point(x / scale, y / scale);
		// Versions before 2 carry no action; the source then copies.
		session.Requested = session.Version >= 2 ? X11DragActions.FromTargetActionName(GetAtomNameSafe(ev.ptr5)) : DataPackageOperation.Copy;
		session.Position = position;

		if (session.Info is null)
		{
			// The data is read only when a handler asks for it, off the UI thread and before XdndFinished, so answering
			// XdndPosition never waits for a selection transfer.
			var package = new DataPackage();
			var formats = session.AvailableFormats;
			X11ClipboardExtension.FillDataPackage(_host.RootX11Window, XdndSelection, package, formats, eager: false);

			var operations = X11DragActions.TargetAllowed(session.SourceActions, session.Requested);
			var info = new CoreDragInfo(new DragEventSource(position), package.GetView(), operations);
			info.RegisterCompletedCallback(result => Complete(session, result));
			session.Info = info;

			if (X11DragDiagnostics.IsEnabled)
			{
				X11DragDiagnostics.Trace($"XDndPosition: first position event, firing DragStarted with available operations {operations} (source actions {session.SourceActions}, requested {session.Requested}). Found available formats {string.Join(", ", formats.Select(GetAtomNameSafe))}.");
			}

			_coreDragDropManager.DragStarted(info);
			_sourceWatch = new Timer(_ => X11XamlRootHost.QueueAction(_host, WatchSource), null, _sourceWatchInterval, _sourceWatchInterval);
		}

		UpdateStatus(session, _coreDragDropManager.ProcessMoved(new DragEventSource(position)), force: true);
	}

	// The Enter/Over handlers may settle after the XdndPosition that raised them, and a source sends the next one only
	// when the pointer moves: the late answer is sent as an unsolicited XdndStatus (a drop right after entering would
	// otherwise be refused).
	public void AcceptedOperationChanged(CoreDragInfo info, DataPackageOperation accepted)
	{
		if (_currentSession is { Dropped: false } session && session.Info == info)
		{
			UpdateStatus(session, accepted, force: false);
		}
	}

	// A source that ended its drag without XdndLeave no longer owns the selection.
	private void WatchSource()
	{
		if (_host.Closed.IsCompleted)
		{
			StopSourceWatch();
			_currentSession = null;
			return;
		}
		if (_currentSession is not { Info: not null, Dropped: false } session)
		{
			return;
		}
		var display = _host.RootX11Window.Display;
		using (X11Helper.XLock(display))
		{
			if (XLib.XGetSelectionOwner(display, XdndSelection) == session.SourceWindow)
			{
				return;
			}
		}
		if (X11DragDiagnostics.IsEnabled)
		{
			X11DragDiagnostics.Trace("XDnd: the source no longer owns XdndSelection, ending the drag");
		}
		StopSourceWatch();
		_currentSession = null;
		_coreDragDropManager.ProcessAborted(_fakePointerId);
	}

	private void UpdateStatus(XdndSession session, DataPackageOperation accepted, bool force)
	{
		var action = X11DragActions.ChooseTargetAction(accepted & session.Info!.AllowedOperations, session.Requested);
		if (!force && action == session.Accepted)
		{
			return;
		}
		session.Accepted = action;

		var display = _host.RootX11Window.Display;
		using var lockDisposable = X11Helper.XLock(display);
		XClientMessageEvent m = default;
		m.type = XEventName.ClientMessage;
		m.display = display;
		m.window = session.SourceWindow;
		m.message_type = X11Helper.GetAtom(display, X11Helper.XdndStatus);
		m.format = 32;
		m.ptr1 = _host.RootX11Window.Window;
		// Bit 1 asks for XdndPosition on every motion: the answer depends on the element under the pointer.
		m.ptr2 = (action != DataPackageOperation.None ? 1 : 0) | 2;
		m.ptr3 = 0;
		m.ptr4 = 0;
		m.ptr5 = action != DataPackageOperation.None ? X11Helper.GetAtom(display, X11DragActions.ToName(action)) : X11Helper.None;

		XEvent xev = default;
		xev.ClientMessageEvent = m;
		_ = XLib.XSendEvent(display, session.SourceWindow, false, IntPtr.Zero /* NoEventMask */, ref xev);
		_ = XLib.XFlush(display);

		if (X11DragDiagnostics.IsEnabled)
		{
			X11DragDiagnostics.Trace($"XDndStatus: to {session.SourceWindow:X} from {_host.RootX11Window.Window:X} at {session.Position} accepted={accepted} action={action} requested={session.Requested}{(force ? "" : " (update)")}");
		}
	}

	private void StopSourceWatch() => Interlocked.Exchange(ref _sourceWatch, null)?.Dispose();

	private void ProcessXdndLeave(XClientMessageEvent ev)
	{
		StopSourceWatch();
		_coreDragDropManager.ProcessAborted(_fakePointerId);
		_currentSession = null;

		if (X11DragDiagnostics.IsEnabled)
		{
			X11DragDiagnostics.Trace($"XDndLeave: aborted current dragging session.");
		}
	}

	private void ProcessXdndDrop(XClientMessageEvent ev)
	{
		StopSourceWatch();
		if (_currentSession is not { } session)
		{
			return;
		}
		_currentSession = null;
		session.Dropped = true;
		if (session.Info is null)
		{
			Complete(session, DataPackageOperation.None);
			return;
		}
		var acceptedOperation = _coreDragDropManager.ProcessDropped(new DragEventSource(session.Position));

		if (X11DragDiagnostics.IsEnabled)
		{
			X11DragDiagnostics.Trace($"XDndDrop: called DragDropManager.ProcessDropped and received acceptedOperation={acceptedOperation}.");
		}
	}

	// Runs once the drop handlers completed (or the drag was aborted). XdndFinished only follows a drop, and only once the
	// handlers are done: the source may release the data as soon as it receives it.
	private void Complete(XdndSession session, DataPackageOperation result)
	{
		if (!session.Dropped || session.Finished)
		{
			return;
		}
		session.Finished = true;
		var action = X11DragActions.ChooseTargetAction(result, session.Requested);
		var display = _host.RootX11Window.Display;
		using var lockDisposable = X11Helper.XLock(display);
		XClientMessageEvent m = default;
		m.type = XEventName.ClientMessage;
		m.display = display;
		m.window = session.SourceWindow;
		m.message_type = X11Helper.GetAtom(display, X11Helper.XdndFinished);
		m.format = 32;
		m.ptr1 = _host.RootX11Window.Window;
		m.ptr2 = action != DataPackageOperation.None ? 1 : 0;
		m.ptr3 = action != DataPackageOperation.None ? X11Helper.GetAtom(display, X11DragActions.ToName(action)) : X11Helper.None;

		XEvent xev = default;
		xev.ClientMessageEvent = m;
		_ = XLib.XSendEvent(display, session.SourceWindow, false, IntPtr.Zero /* NoEventMask */, ref xev);
		_ = XLib.XFlush(display);

		if (X11DragDiagnostics.IsEnabled)
		{
			X11DragDiagnostics.Trace($"XDndFinished: result={result} action={action}");
		}
	}

	// The XDND source takes the pointer grab from the start, so it can track the windows over Uno's and route the
	// pointer back while it is over the window the drag started in.
	public bool StartsNativeDragImmediately => true;

	public bool StartNativeDrag(CoreDragInfo info, Action<DataPackageOperation> onCompleted)
	{
		if (info.SourceId == _fakePointerId || (Windows.Devices.Input.PointerDeviceType)(info.SourceId >> 32) != Windows.Devices.Input.PointerDeviceType.Mouse
			|| _host.PointerSource is null || _host.Closed.IsCompleted)
		{
			return false;
		}
		var formats = X11DragFormats.GetDragMimeTypes(info.Data);
		if (formats.Count == 0)
		{
			return false;
		}
		uint button;
		using (X11Helper.XLock(_host.TopX11Window.Display))
		{
			if (!X11Helper.XQueryPointer(_host.TopX11Window.Display, _host.TopX11Window.Window,
				out _, out _, out _, out _, out _, out _, out var mask) || (button = X11DragActions.ChooseButton(mask)) == 0)
			{
				return false;
			}
		}
		var scale = ((IXamlRootHost)_host).RootElement?.XamlRoot?.RasterizationScale ?? 1;
		return X11DragSource.Instance.TryStart(_host, _coreDragDropManager, info, formats, scale, button, onCompleted);
	}

	private readonly struct DragEventSource(Point location) : IDragEventSource
	{
		private static long _nextFrameId;
		private readonly Point _location = location;

		public long Id => _fakePointerId;

		public uint FrameId { get; } = (uint)Interlocked.Increment(ref _nextFrameId);

		/// <inheritdoc />
		public (Point location, DragDropModifiers modifier) GetState() => (_location, DragDropModifiers.None);

		/// <inheritdoc />
		public Point GetPosition(object? relativeTo)
		{
			if (relativeTo is null)
			{
				return _location;
			}

			if (relativeTo is UIElement elt)
			{
				var eltToRoot = UIElement.GetTransform(elt, null);
				var rootToElt = eltToRoot.Inverse();

				return rootToElt.Transform(_location);
			}

			throw new InvalidOperationException("The relative to must be a UIElement.");
		}
	}

	private string GetAtomNameSafe(IntPtr atom) => atom == IntPtr.Zero ? "NULL" : XLib.GetAtomName(_host.RootX11Window.Display, atom);

	// From the spec: "If (the target window) retrieved the data, it should cache it so it does not need to be retrieved again when the actual drop occurs.
	// XdndEnter doesn't provide pointer coords, so we fire DragEntered with the first XdndPosition that comes after XdndEnter.
	// The last position is kept because XdndLeave and XdndDrop don't send coordinates. All members are used on the UI thread.
	private sealed class XdndSession(IntPtr version, IntPtr sourceWindow, IntPtr[] availableFormats, DataPackageOperation sourceActions)
	{
		public IntPtr Version { get; } = version;
		public IntPtr SourceWindow { get; } = sourceWindow;
		public IntPtr[] AvailableFormats { get; } = availableFormats;
		public DataPackageOperation SourceActions { get; } = sourceActions;
		public CoreDragInfo? Info { get; set; }
		public Point Position { get; set; }
		public DataPackageOperation Requested { get; set; }
		// The action of the last XdndStatus, None when refused.
		public DataPackageOperation Accepted { get; set; }
		public bool Dropped { get; set; }
		public bool Finished { get; set; }
	}
}
