// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Uno.Extensions;
using Uno.Foundation.Logging;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.ApplicationModel.DataTransfer.DragDrop.Core;
using Windows.Foundation;

namespace Uno.WinUI.Runtime.Skia.X11;

// One process-wide selection owner. Only Run and the wake helper use its private connection.
internal sealed class X11DragSource
{
	private const uint PointerMask = (uint)(XEventMask.ButtonPressMask | XEventMask.ButtonReleaseMask | XEventMask.PointerMotionMask);
	private const int ChunkSize = 64 * 1024;
	private static readonly Lazy<X11DragSource> _instance = new(() => new X11DragSource());
	private readonly ConcurrentQueue<Action> _queue = new();
	private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Dictionary<string, IntPtr> _atoms = new();
	private readonly List<PendingRead> _reads = new();
	private readonly List<IncrementalSend> _sends = new();
	private IntPtr _display;
	private IntPtr _window;
	private IntPtr _root;
	private Session? _starting;
	private Session? _drag;
	private Session? _selectionOwner;
	private bool _keyboardGrabbed;
	private bool _pointerGrabbed;
	private byte _escape;
	private byte _shiftLeft;
	private byte _shiftRight;
	private byte _controlLeft;
	private byte _controlRight;

	internal static X11DragSource Instance => _instance.Value;

	private X11DragSource()
	{
		new Thread(Run) { IsBackground = true, Name = "Uno XDND source" }.Start();
	}

	// UI thread. All providers are started before releasing the implicit grab.
	internal bool TryStart(X11XamlRootHost host, CoreDragDropManager manager, CoreDragInfo info,
		IReadOnlyList<string> formats, double scale, uint button, Action<DataPackageOperation> onCompleted)
	{
		var deadline = Environment.TickCount64 + 450;
		if (!_ready.Task.Wait(100) || !_ready.Task.Result || host.PointerSource is not { } pointer)
		{
			return false;
		}
		var cache = formats.ToDictionary(format => format, format => ProduceAsync(info.Data, format));
		var capture = CaptureAsync(info);
		var generation = pointer.BeginNativeDrag((uint)info.SourceId, info.Position);
		var session = new Session(host, pointer, generation, manager, info, scale, button, onCompleted, cache, deadline);
		try
		{
			info.RegisterCompletedCallback(_ => Post(() => EndFromUno(session)));
			host.ReleasePointerGrabForDrag();
			Post(() => Begin(session));
			var remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
			if (remaining > 0)
			{
				session.Started.Task.Wait(remaining);
			}
			// A timed-out request must never acquire the pointer after cancellation is queued.
			Interlocked.CompareExchange(ref session.StartResult, 2, 0);
			if (Volatile.Read(ref session.StartResult) != 1)
			{
				EndInput(session);
				Post(() => RejectStart(session));
				return false;
			}
			_ = capture.ContinueWith(task => Post(() => ShowIcon(session, task.Result)), TaskScheduler.Default);
			return true;
		}
		catch (Exception error)
		{
			if (Volatile.Read(ref session.StartResult) == 1)
			{
				Post(() => Cancel(session));
				return true;
			}
			Interlocked.CompareExchange(ref session.StartResult, 2, 0);
			EndInput(session);
			Post(() => RejectStart(session));
			if (this.Log().IsEnabled(LogLevel.Error))
			{
				this.Log().Error("Failed to hand the pointer to the XDND source.", error);
			}
			return false;
		}
	}

	private static async Task<byte[]?> ProduceAsync(DataPackageView view, string format)
	{
		try
		{
			return await X11DragFormats.GetBytesAsync(view, format, static reference => reference.OwnsOpenedStream).ConfigureAwait(false);
		}
		catch (Exception error)
		{
			if (typeof(X11DragSource).Log().IsEnabled(LogLevel.Debug))
			{
				typeof(X11DragSource).Log().Debug($"Could not produce drag format {format}: {error.Message}");
			}
			return null;
		}
	}

	private static async Task<X11DragIconImage?> CaptureAsync(CoreDragInfo info)
	{
		try
		{
			return await X11DragIcon.CaptureAsync(info);
		}
		catch (Exception error)
		{
			if (typeof(X11DragSource).Log().IsEnabled(LogLevel.Debug))
			{
				typeof(X11DragSource).Log().Debug($"Could not capture drag icon: {error.Message}");
			}
			return null;
		}
	}

	private void Post(Action action)
	{
		_queue.Enqueue(action);
		var display = _display;
		if (display == IntPtr.Zero || _window == IntPtr.Zero)
		{
			return;
		}
		using var xlock = X11Helper.XLock(display);
		Send(_window, _window, Atom("_UNO_DND_WAKE"));
		_ = XLib.XFlush(display);
	}

	private IntPtr Atom(string name)
	{
		if (!_atoms.TryGetValue(name, out var atom))
		{
			_atoms[name] = atom = X11Helper.GetAtom(_display, name);
		}
		return atom;
	}

	private unsafe void Run()
	{
		try
		{
			_display = XLib.XOpenDisplay(IntPtr.Zero);
			if (_display == IntPtr.Zero)
			{
				_ready.TrySetResult(false);
				return;
			}
			int fd;
			using (X11Helper.XLock(_display))
			{
				_root = XLib.XDefaultRootWindow(_display);
				_window = XLib.XCreateSimpleWindow(_display, _root, 0, 0, 1, 1, 0, IntPtr.Zero, IntPtr.Zero);
				XLib.XSelectInput(_display, _window, (IntPtr)XEventMask.PropertyChangeMask);
				Atom("_UNO_DND_WAKE");
				_escape = X11Helper.XKeysymToKeycode(_display, 0xff1b);
				_shiftLeft = X11Helper.XKeysymToKeycode(_display, 0xffe1);
				_shiftRight = X11Helper.XKeysymToKeycode(_display, 0xffe2);
				_controlLeft = X11Helper.XKeysymToKeycode(_display, 0xffe3);
				_controlRight = X11Helper.XKeysymToKeycode(_display, 0xffe4);
				fd = XLib.XConnectionNumber(_display);
				_ = XLib.XFlush(_display);
			}
			_ready.TrySetResult(true);
			while (true)
			{
				int timeout;
				using (X11Helper.XLock(_display))
				{
					try
					{
						while (_queue.TryDequeue(out var work))
						{
							work();
						}
						while (XLib.XPending(_display) > 0)
						{
							XLib.XNextEvent(_display, out var ev);
							ProcessEvent(ev);
						}
						Tick();
					}
					catch (Exception error)
					{
						if (this.Log().IsEnabled(LogLevel.Error))
						{
							this.Log().Error("XDND source dispatch failed.", error);
						}
						if (_starting is { } starting)
						{
							RejectStart(starting);
						}
						if (_drag is { } drag)
						{
							Cancel(drag);
						}
					}
					_ = XLib.XFlush(_display);
					timeout = !_queue.IsEmpty || XLib.XPending(_display) > 0 ? 0
						: _drag is not null || _starting is not null || _reads.Count > 0 || _sends.Count > 0 ? 15 : 1000;
				}
				var poll = new X11Helper.Pollfd { fd = fd, events = X11Helper.POLLIN };
				_ = X11Helper.poll(&poll, 1, timeout);
			}
		}
		catch (Exception error)
		{
			_ready.TrySetResult(false);
			if (_starting is { } starting)
			{
				Interlocked.CompareExchange(ref starting.StartResult, 2, 0);
				EndInput(starting);
				starting.Started.TrySetResult(false);
			}
			if (_drag is { } drag)
			{
				drag.State.Complete(DataPackageOperation.None);
				drag.Done = true;
				_drag = null;
				CompleteOnUI(drag, notify: true);
			}
			if (this.Log().IsEnabled(LogLevel.Error))
			{
				this.Log().Error("Could not initialize the XDND connection.", error);
			}
		}
	}

	private void Begin(Session session)
	{
		if (_starting is { } starting)
		{
			RejectStart(starting);
		}
		if (_drag is { } previous)
		{
			Cancel(previous);
		}
		if (session.StartResult != 0 || session.Host.Closed.IsCompleted)
		{
			RejectStart(session);
			return;
		}
		_starting = session;
		session.Window = XLib.XCreateSimpleWindow(_display, _root, 0, 0, 1, 1, 0, IntPtr.Zero, IntPtr.Zero);
		if (session.Window == IntPtr.Zero)
		{
			RejectStart(session);
			return;
		}
		XLib.XSelectInput(_display, session.Window, (IntPtr)XEventMask.PropertyChangeMask);
		// KWin's XWayland bridge requires an actual server time when the selection is claimed.
		XLib.XChangeProperty(_display, session.Window, Atom("_UNO_DND_TIME"), Atom("ATOM"), 32, PropertyMode.Append, Array.Empty<IntPtr>(), 0);
	}

	private void TryGrab(Session session, long now)
	{
		if (session.StartResult != 0 || now >= session.StartDeadline || session.Host.Closed.IsCompleted)
		{
			RejectStart(session);
			return;
		}
		if (session.Time == IntPtr.Zero)
		{
			return;
		}
		if (session.GrabDeadline == 0)
		{
			session.GrabDeadline = Math.Min(session.StartDeadline, now + 100);
			session.Cursors = X11DragCursors.Create(_display);
		}
		if (!X11Helper.XQueryPointer(_display, _root, out _, out _, out var x, out var y, out _, out _, out var mask)
			|| !X11DragActions.IsButtonHeld(mask, session.Button))
		{
			RejectStart(session);
			return;
		}
		var grabbed = X11Helper.XGrabPointer(_display, _root, false, PointerMask, 1, 1, IntPtr.Zero, session.Cursors![DataPackageOperation.None], session.Time);
		if (grabbed != 0)
		{
			if (grabbed != 1 /* AlreadyGrabbed */ || now >= session.GrabDeadline)
			{
				RejectStart(session);
			}
			return;
		}
		_pointerGrabbed = true;
		_keyboardGrabbed = X11Helper.XGrabKeyboard(_display, _root, false, 1, 1, session.Time) == 0;
		_ = XLib.XSetSelectionOwner(_display, Atom("XdndSelection"), session.Window, session.Time);
		if (XLib.XGetSelectionOwner(_display, Atom("XdndSelection")) != session.Window)
		{
			RejectStart(session);
			return;
		}
		var previousOwner = _selectionOwner;
		_selectionOwner = session;
		session.SelectionTime = session.Time;
		RetireWindow(previousOwner);
		session.SelectionContent = session.Content.ToDictionary(pair => Atom(pair.Key), pair => pair.Value);
		session.Types = session.SelectionContent.Keys.ToArray();
		if (session.Types.Length > 3)
		{
			XLib.XChangeProperty(_display, session.Window, Atom("XdndTypeList"), Atom("ATOM"), 32, PropertyMode.Replace, session.Types, session.Types.Length);
		}
		if (Interlocked.CompareExchange(ref session.StartResult, 1, 0) != 0)
		{
			RejectStart(session);
			return;
		}
		_starting = null;
		_drag = session;
		session.Started.TrySetResult(true);
		if (X11DragDiagnostics.IsEnabled)
		{
			X11DragDiagnostics.Trace($"XDND start time={session.Time} source={session.Window} formats={string.Join(",", session.Content.Keys)}");
		}
		Move(session, x, y, mask, session.Time);
	}

	private void RejectStart(Session session)
	{
		if (_starting == session)
		{
			_starting = null;
			ReleaseGrabs();
			if (_selectionOwner == session)
			{
				if (XLib.XGetSelectionOwner(_display, Atom("XdndSelection")) == session.Window)
				{
					_ = XLib.XSetSelectionOwner(_display, Atom("XdndSelection"), IntPtr.Zero, IntPtr.Zero);
				}
				_selectionOwner = null;
			}
		}
		Interlocked.CompareExchange(ref session.StartResult, 2, 0);
		session.Done = true;
		session.Cursors?.Dispose();
		session.Cursors = null;
		RetireWindow(session);
		EndInput(session);
		session.Started.TrySetResult(false);
	}

	private void RetireWindow(Session? session)
	{
		if (session is { Done: true } && session.Window != IntPtr.Zero
			&& XLib.XGetSelectionOwner(_display, Atom("XdndSelection")) != session.Window)
		{
			_ = XLib.XDestroyWindow(_display, session.Window);
			session.Window = IntPtr.Zero;
		}
	}

	private static void EndInput(Session session)
	{
		if (Interlocked.Exchange(ref session.InputEnded, 1) == 0)
		{
			session.Pointer.EndNativeDrag(session.Generation);
		}
	}

	private void ProcessEvent(XEvent ev)
	{
		switch (ev.type)
		{
			case XEventName.PropertyNotify:
				if (_starting is { } starting && ev.PropertyEvent.window == starting.Window && ev.PropertyEvent.atom == Atom("_UNO_DND_TIME"))
				{
					starting.Time = ev.PropertyEvent.time;
				}
				SendIncrement(ev.PropertyEvent);
				break;
			case XEventName.SelectionRequest:
				ReadSelection(ev.SelectionRequestEvent);
				break;
			case XEventName.SelectionClear:
				if (ev.SelectionClearEvent.selection == Atom("XdndSelection")
					&& _selectionOwner is { } owner && ev.SelectionClearEvent.window == owner.Window
					&& XLib.XGetSelectionOwner(_display, Atom("XdndSelection")) != owner.Window)
				{
					_selectionOwner = null;
					if (_drag == owner)
					{
						Cancel(owner);
					}
					RetireWindow(owner);
				}
				break;
			case XEventName.ClientMessage:
				DragMessage(ev.ClientMessageEvent);
				break;
			case XEventName.MotionNotify:
				if (_drag is { State.Released: false, LocalDrop: false } motion)
				{
					Move(motion, ev.MotionEvent.x_root, ev.MotionEvent.y_root, (uint)ev.MotionEvent.state, ev.MotionEvent.time);
				}
				break;
			case XEventName.ButtonRelease:
				if (_drag is { State.Released: false, LocalDrop: false } released
					&& X11DragActions.IsReleaseButton((uint)ev.ButtonEvent.button, released.Button))
				{
					Move(released, ev.ButtonEvent.x_root, ev.ButtonEvent.y_root, (uint)ev.ButtonEvent.state, ev.ButtonEvent.time);
					Release(released);
				}
				break;
			case XEventName.KeyPress:
			case XEventName.KeyRelease:
				Key(ev.KeyEvent);
				break;
			case XEventName.Expose:
				if (_drag?.Icon is { } icon && ev.ExposeEvent.window == icon.Window)
				{
					icon.Draw();
				}
				break;
		}
	}

	private void Tick()
	{
		var now = Environment.TickCount64;
		if (_starting is { } starting)
		{
			TryGrab(starting, now);
		}
		if (_drag is { } drag)
		{
			if (drag.Host.Closed.IsCompleted)
			{
				Cancel(drag);
			}
			else if (drag.State.Timeout(now))
			{
				Finish(drag);
			}
			else if (!drag.LocalDrop && !drag.State.Dropped)
			{
				if (!drag.State.Released && now - drag.ButtonPollTick >= 100)
				{
					PollButtons(drag, now);
					if (_drag == drag && !drag.State.Released)
					{
						CheckTarget(drag, now);
					}
				}
				if (_drag != drag || drag.LocalDrop)
				{
					return;
				}
				if (drag.Own && !drag.State.Released && drag.Feedback == DataPackageOperation.None && now - drag.OwnPollTick >= 100)
				{
					RouteOwn(drag, drag.OwnPosition);
				}
				SendPosition(drag, now);
				if (drag.State.Released)
				{
					DecideRelease(drag, now);
				}
			}
		}
		for (var i = _reads.Count - 1; i >= 0; i--)
		{
			var read = _reads[i];
			if (now >= read.Deadline || read.Data.IsCompleted)
			{
				_reads.RemoveAt(i);
				ReplyData(read.Request, now < read.Deadline && read.Data.IsCompletedSuccessfully ? read.Data.Result : null);
			}
		}
		for (var i = _sends.Count - 1; i >= 0; i--)
		{
			if (now - _sends[i].LastActivity >= 10_000)
			{
				RemoveSend(i);
			}
		}
	}

	// The grab can end without a ButtonRelease reaching us (XWayland cancels it when the compositor's bridged drag is
	// escaped), so a released button seen by polling ends the drag like a release would.
	private void PollButtons(Session drag, long now)
	{
		drag.ButtonPollTick = now;
		if (X11Helper.XQueryPointer(_display, _root, out _, out _, out var x, out var y, out _, out _, out var mask)
			&& !X11DragActions.IsButtonHeld(mask, drag.Button))
		{
			if (X11DragDiagnostics.IsEnabled)
			{
				X11DragDiagnostics.Trace("XDND buttons released without a ButtonRelease");
			}
			Move(drag, x, y, mask, drag.Time);
			Release(drag);
		}
	}

	// KWin's XWayland bridge ends its drag on Escape without telling the X source: it unmaps the proxy window and the
	// X server sees neither motion nor the button release until the pointer comes back, when the stale press would
	// drop wherever it lands. A target that disappears while the X pointer stays put therefore cancels the drag.
	private void CheckTarget(Session drag, long now)
	{
		if (drag.Own || drag.Target.Window == IntPtr.Zero)
		{
			drag.TargetLostTick = 0;
			return;
		}
		var attributes = default(XWindowAttributes);
		if (XLib.XGetWindowAttributes(_display, drag.Target.Window, ref attributes) != 0 && attributes.map_state == MapState.IsViewable)
		{
			drag.TargetLostTick = 0;
		}
		else if (drag.TargetLostTick == 0)
		{
			drag.TargetLostTick = now;
		}
		else if (now - drag.TargetLostTick >= 300
			&& X11Helper.XQueryPointer(_display, _root, out _, out _, out var x, out var y, out _, out _, out _)
			&& x == drag.X && y == drag.Y && FindTarget(x, y, drag.Icon?.Window ?? IntPtr.Zero).Window == IntPtr.Zero)
		{
			if (X11DragDiagnostics.IsEnabled)
			{
				X11DragDiagnostics.Trace($"XDND target {drag.Target.Window} vanished under a still pointer, cancelling");
			}
			Cancel(drag);
		}
	}

	private void Key(XKeyEvent ev)
	{
		if (_drag is not { State.Released: false, LocalDrop: false } drag)
		{
			return;
		}
		if (ev.type == XEventName.KeyPress && ev.keycode == _escape)
		{
			Cancel(drag);
			return;
		}
		var state = (uint)ev.state;
		var bit = ev.keycode == _shiftLeft || ev.keycode == _shiftRight ? X11DragActions.ShiftMask
			: ev.keycode == _controlLeft || ev.keycode == _controlRight ? X11DragActions.ControlMask : 0;
		if (bit != 0)
		{
			state = ev.type == XEventName.KeyPress ? state | bit : state & ~bit;
			Move(drag, drag.X, drag.Y, state, ev.time);
		}
	}

	private void Move(Session drag, int x, int y, uint mask, IntPtr time)
	{
		var requested = X11DragActions.Choose(mask, drag.Info.AllowedOperations);
		var changed = x != drag.X || y != drag.Y || requested != drag.Requested
			|| ((mask ^ drag.Mask) & (X11DragActions.ShiftMask | X11DragActions.ControlMask)) != 0;
		drag.X = x;
		drag.Y = y;
		drag.Mask = mask;
		drag.Time = time;
		drag.Requested = requested;
		drag.Icon?.Move(x, y);
		drag.TargetLostTick = 0;
		var target = FindTarget(x, y, drag.Icon?.Window ?? IntPtr.Zero);
		var own = target.Window == drag.OriginWindow;
		if (drag.Target != target || drag.Own != own)
		{
			drag.TargetGeneration++;
			Leave(drag);
			drag.Target = target;
			drag.Own = own;
			drag.State = new X11DragSourceState();
			changed = true;
			Feedback(drag, DataPackageOperation.None);
			if (!own && target.Window != IntPtr.Zero)
			{
				var types = drag.Types;
				Send(target.Receiver, target.Window, Atom("XdndEnter"), drag.Window, (IntPtr)X11DragActions.EnterFlags(target.Version, types.Length),
					types.ElementAtOrDefault(0), types.ElementAtOrDefault(1), types.ElementAtOrDefault(2));
				if (X11DragDiagnostics.IsEnabled)
				{
					X11DragDiagnostics.Trace($"XDND enter target={target.Window} proxy={target.Receiver} version={target.Version}");
				}
			}
		}
		if (XLib.XTranslateCoordinates(_display, _root, drag.OriginWindow, x, y, out var localX, out var localY, out _))
		{
			drag.Pointer.UpdateNativeDragPosition(drag.Generation, new Point(localX / drag.Scale, localY / drag.Scale), (uint)time);
			if (own)
			{
				drag.OwnPosition = new Point(localX / drag.Scale, localY / drag.Scale);
			}
		}
		if (changed)
		{
			drag.State.PositionChanged();
			if (own)
			{
				RouteOwn(drag, drag.OwnPosition);
			}
		}
		SendPosition(drag, Environment.TickCount64);
	}

	private void RouteOwn(Session drag, Point position, bool drop = false)
	{
		drag.OwnPollTick = Environment.TickCount64;
		drag.Frame = Math.Max(drag.Frame + 1, (uint)drag.Time);
		var source = new DragEventSource(drag.Info.SourceId, drag.Frame, position, drag.Mask);
		var targetGeneration = drag.TargetGeneration;
		X11XamlRootHost.QueueAction(drag.Host, () =>
		{
			if (drag.Done)
			{
				return;
			}
			if (drop)
			{
				drag.Manager.ProcessDropped(source);
			}
			else
			{
				var accepted = drag.Manager.ProcessMoved(source);
				Post(() =>
				{
					if (_drag == drag && targetGeneration == drag.TargetGeneration && drag.Own && !drag.State.Released && !drag.LocalDrop)
					{
						Feedback(drag, accepted == DataPackageOperation.None ? accepted : X11DragActions.Choose(drag.Mask, accepted));
					}
				});
			}
		});
	}

	private void SendPosition(Session drag, long now)
	{
		if (drag.Own || drag.Target.Window == IntPtr.Zero || !drag.State.CanSendPosition(now))
		{
			return;
		}
		Send(drag.Target.Receiver, drag.Target.Window, Atom("XdndPosition"), drag.Window, IntPtr.Zero,
			(IntPtr)X11DragActions.PackPosition(drag.X, drag.Y), drag.Time, Atom(X11DragActions.ToName(drag.Requested)));
		drag.State.PositionSent(now, drag.Requested);
	}

	private void Leave(Session drag)
	{
		if (drag.Own)
		{
			RouteOwn(drag, new Point(-1, -1));
		}
		else if (drag.Target.Window != IntPtr.Zero && !drag.State.Dropped)
		{
			Send(drag.Target.Receiver, drag.Target.Window, Atom("XdndLeave"), drag.Window);
		}
	}

	private void Release(Session drag)
	{
		ReleaseGrabs();
		drag.Icon?.Dispose();
		drag.Icon = null;
		if (drag.Own)
		{
			drag.LocalDrop = true;
			RouteOwn(drag, drag.OwnPosition, drop: true);
			return;
		}
		drag.State.Release(Environment.TickCount64);
		DecideRelease(drag, Environment.TickCount64);
	}

	private void DecideRelease(Session drag, long now)
	{
		var decision = drag.Target.Window == IntPtr.Zero ? X11DragReleaseDecision.Leave : drag.State.ReleaseDecision(now);
		if (decision == X11DragReleaseDecision.Drop)
		{
			drag.State.Drop(now);
			Send(drag.Target.Receiver, drag.Target.Window, Atom("XdndDrop"), drag.Window, IntPtr.Zero, drag.Time);
			if (X11DragDiagnostics.IsEnabled)
			{
				X11DragDiagnostics.Trace($"XDND drop target={drag.Target.Window} action={drag.State.StatusAction}");
			}
		}
		else if (decision == X11DragReleaseDecision.Leave)
		{
			Cancel(drag);
		}
	}

	private void DragMessage(XClientMessageEvent message)
	{
		if (_drag is not { } drag || drag.Own || message.window != drag.Window || message.format != 32 || message.ptr1 != drag.Target.Window)
		{
			return;
		}
		if (drag.State.Timeout(Environment.TickCount64))
		{
			Finish(drag);
			return;
		}
		if (message.message_type == Atom("XdndStatus") && drag.State.Outstanding && !drag.State.Dropped)
		{
			var accepted = (message.ptr2 & 1) != 0 && message.ptr5 != IntPtr.Zero;
			var action = Action(message.ptr5);
			drag.State.Status(accepted, action, drag.Info.AllowedOperations);
			Feedback(drag, drag.State.StatusAction);
			if (X11DragDiagnostics.IsEnabled)
			{
				X11DragDiagnostics.Trace($"XDND status target={message.ptr1} accepted={drag.State.Accepted} action={action}");
			}
			SendPosition(drag, Environment.TickCount64);
			if (drag.State.Released)
			{
				DecideRelease(drag, Environment.TickCount64);
			}
		}
		else if (message.message_type == Atom("XdndFinished")
			&& drag.State.Finished(drag.Target.Version, (message.ptr2 & 1) != 0, message.ptr3 == IntPtr.Zero ? null : Action(message.ptr3), drag.Info.AllowedOperations))
		{
			if (X11DragDiagnostics.IsEnabled)
			{
				X11DragDiagnostics.Trace($"XDND finished target={message.ptr1} result={drag.State.Result}");
			}
			Finish(drag);
		}
	}

	private DataPackageOperation Action(IntPtr atom) => atom == Atom("XdndActionMove") ? DataPackageOperation.Move
		: atom == Atom("XdndActionLink") ? DataPackageOperation.Link : DataPackageOperation.Copy;

	private void Feedback(Session drag, DataPackageOperation operation)
	{
		if (drag.Feedback == operation)
		{
			return;
		}
		drag.Feedback = operation;
		if (_pointerGrabbed && drag.Cursors is { } cursors)
		{
			_ = X11Helper.XChangeActivePointerGrab(_display, PointerMask, cursors[operation], IntPtr.Zero);
		}
	}

	private void ShowIcon(Session drag, X11DragIconImage? image)
	{
		if (_drag != drag || drag.State.Released || drag.LocalDrop || image is null)
		{
			return;
		}
		try
		{
			drag.Icon = X11DragIcon.Create(_display, image, drag.Scale);
			drag.Icon?.Move(drag.X, drag.Y);
		}
		catch (Exception error)
		{
			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"Could not create XDND icon: {error.Message}");
			}
		}
	}

	private void Cancel(Session drag)
	{
		if (drag.Done)
		{
			return;
		}
		Leave(drag);
		drag.State.Complete(DataPackageOperation.None);
		if (X11DragDiagnostics.IsEnabled)
		{
			X11DragDiagnostics.Trace($"XDND cancel target={drag.Target.Window}");
		}
		Finish(drag);
	}

	private void EndFromUno(Session session)
	{
		if (_starting == session)
		{
			RejectStart(session);
		}
		if (_drag == session && !session.Done)
		{
			Leave(session);
			Finish(session, notify: false);
		}
	}

	private void Finish(Session drag, bool notify = true)
	{
		if (drag.Done)
		{
			return;
		}
		drag.Done = true;
		if (_drag == drag)
		{
			_drag = null;
		}
		ReleaseGrabs();
		drag.Icon?.Dispose();
		drag.Icon = null;
		drag.Cursors?.Dispose();
		drag.Cursors = null;
		RetireWindow(drag);
		// Keep the selection and its cached data alive for targets that read after Finished.
		CompleteOnUI(drag, notify);
	}

	private static void CompleteOnUI(Session drag, bool notify)
	{
		X11XamlRootHost.QueueAction(drag.Host, () =>
		{
			try
			{
				if (notify)
				{
					drag.OnCompleted(drag.State.Result);
				}
			}
			finally
			{
				EndInput(drag);
			}
		});
	}

	private void ReleaseGrabs()
	{
		if (_pointerGrabbed)
		{
			_ = X11Helper.XUngrabPointer(_display, IntPtr.Zero);
			_pointerGrabbed = false;
		}
		if (_keyboardGrabbed)
		{
			_ = X11Helper.XUngrabKeyboard(_display, IntPtr.Zero);
			_keyboardGrabbed = false;
		}
	}

	private readonly record struct Target(IntPtr Window, IntPtr Receiver, int Version);

	private unsafe Target FindTarget(int x, int y, IntPtr icon)
	{
		IntPtr current = IntPtr.Zero;
		// The icon is topmost but input-transparent. XTranslateCoordinates alone would stop at it.
		if (XLib.XQueryTree(_display, _root, out _, out _, out var children, out var count) != 0 && children != IntPtr.Zero)
		{
			try
			{
				for (var i = count - 1; i >= 0; i--)
				{
					var child = ((IntPtr*)children)[i];
					if (child == icon || child == _window)
					{
						continue;
					}
					XWindowAttributes attributes = default;
					if (XLib.XGetWindowAttributes(_display, child, ref attributes) != 0 && (int)attributes.map_state == 2
						&& XLib.XTranslateCoordinates(_display, _root, child, x, y, out var dx, out var dy, out _)
						&& dx >= 0 && dy >= 0 && dx < attributes.width && dy < attributes.height)
					{
						current = child;
						break;
					}
				}
			}
			finally
			{
				_ = XLib.XFree(children);
			}
		}
		Target best = default;
		for (var depth = 0; current != IntPtr.Zero && depth < 64; depth++)
		{
			var receiver = current;
			if (ReadWord(current, "XdndProxy", "WINDOW") is { } proxy && proxy != IntPtr.Zero
				&& ReadWord(proxy, "XdndProxy", "WINDOW") == proxy)
			{
				receiver = proxy;
			}
			if (ReadWord(receiver, "XdndAware", "ATOM") is { } aware)
			{
				var version = (int)Math.Min(5, (long)aware);
				best = version >= 3 ? new Target(current, receiver, version) : default;
			}
			if (!XLib.XTranslateCoordinates(_display, _root, current, x, y, out _, out _, out var child) || child == current)
			{
				break;
			}
			current = child;
		}
		return best;
	}

	private unsafe IntPtr? ReadWord(IntPtr window, string property, string type)
	{
		var status = XLib.XGetWindowProperty(_display, window, Atom(property), IntPtr.Zero, 1, false, Atom(type),
			out var actualType, out var format, out var count, out _, out var data);
		try
		{
			return status == 0 && data != IntPtr.Zero && actualType == Atom(type) && format == 32 && count >= 1 ? *(IntPtr*)data : null;
		}
		finally
		{
			if (data != IntPtr.Zero)
			{
				_ = XLib.XFree(data);
			}
		}
	}

	private void Send(IntPtr receiver, IntPtr window, IntPtr message, IntPtr a = default, IntPtr b = default,
		IntPtr c = default, IntPtr d = default, IntPtr e = default)
	{
		var ev = new XEvent
		{
			ClientMessageEvent = new XClientMessageEvent
			{
				type = XEventName.ClientMessage,
				display = _display,
				window = window,
				message_type = message,
				format = 32,
				ptr1 = a,
				ptr2 = b,
				ptr3 = c,
				ptr4 = d,
				ptr5 = e,
			}
		};
		XLib.XSendEvent(_display, receiver, false, IntPtr.Zero, ref ev);
	}

	private void ReadSelection(XSelectionRequestEvent request)
	{
		if (request.selection != Atom("XdndSelection") || _selectionOwner is not { } owner
			|| request.owner != owner.Window || XLib.XGetSelectionOwner(_display, request.selection) != owner.Window
			|| !X11DragSourceState.IsSelectionTimeValid(unchecked((uint)request.time), unchecked((uint)owner.SelectionTime)))
		{
			Notify(request, IntPtr.Zero);
			return;
		}
		var property = request.property == IntPtr.Zero ? request.target : request.property;
		if (request.target == Atom("TARGETS"))
		{
			var targets = owner.SelectionContent.Keys.Concat(new[] { Atom("TARGETS"), Atom("TIMESTAMP") }).ToArray();
			XLib.XChangeProperty(_display, request.requestor, property, Atom("ATOM"), 32, PropertyMode.Replace, targets, targets.Length);
			Notify(request, property);
		}
		else if (request.target == Atom("TIMESTAMP"))
		{
			XLib.XChangeProperty(_display, request.requestor, property, Atom("INTEGER"), 32, PropertyMode.Replace, new[] { owner.SelectionTime }, 1);
			Notify(request, property);
		}
		else if (owner.SelectionContent.TryGetValue(request.target, out var data))
		{
			if (data.IsCompleted)
			{
				ReplyData(request, data.IsCompletedSuccessfully ? data.Result : null);
			}
			else
			{
				_reads.Add(new PendingRead(request, data, Environment.TickCount64 + 5000));
				_ = data.ContinueWith(_ => Post(() => { }), TaskScheduler.Default);
			}
		}
		else
		{
			Notify(request, IntPtr.Zero);
		}
	}

	private void ReplyData(XSelectionRequestEvent request, byte[]? data)
	{
		if (data is null)
		{
			Notify(request, IntPtr.Zero);
			return;
		}
		var property = request.property == IntPtr.Zero ? request.target : request.property;
		var type = request.target == Atom("TEXT") ? Atom(X11DragFormats.ReplyType("TEXT")) : request.target;
		// Only one stream may drive a requestor property at a time.
		for (var i = _sends.Count - 1; i >= 0; i--)
		{
			if (_sends[i].Requestor == request.requestor && _sends[i].Property == property)
			{
				RemoveSend(i);
			}
		}
		if (data.Length > ChunkSize)
		{
			XLib.XSelectInput(_display, request.requestor, (IntPtr)XEventMask.PropertyChangeMask);
			XLib.XChangeProperty(_display, request.requestor, property, Atom("INCR"), 32, PropertyMode.Replace, new[] { (IntPtr)data.Length }, 1);
			_sends.Add(new IncrementalSend(request.requestor, property, type, data));
		}
		else
		{
			ChangeBytes(request.requestor, property, type, data, 0, data.Length);
		}
		Notify(request, property);
	}

	private unsafe void ChangeBytes(IntPtr window, IntPtr property, IntPtr type, byte[] bytes, int offset, int count)
	{
		fixed (byte* data = bytes)
		{
			_ = XLib.XChangeProperty(_display, window, property, type, 8, PropertyMode.Replace, (IntPtr)(data + offset), count);
		}
	}

	private void Notify(XSelectionRequestEvent request, IntPtr property)
	{
		var ev = new XEvent
		{
			SelectionEvent = new XSelectionEvent
			{
				type = XEventName.SelectionNotify,
				display = _display,
				requestor = request.requestor,
				selection = request.selection,
				target = request.target,
				property = property,
				time = request.time,
			}
		};
		XLib.XSendEvent(_display, request.requestor, false, IntPtr.Zero, ref ev);
	}

	private void SendIncrement(XPropertyEvent ev)
	{
		if (ev.state != 1 /* PropertyDelete */)
		{
			return;
		}
		for (var i = 0; i < _sends.Count; i++)
		{
			var send = _sends[i];
			if (send.Requestor != ev.window || send.Property != ev.atom)
			{
				continue;
			}
			var count = Math.Min(ChunkSize, send.Data.Length - send.Offset);
			ChangeBytes(send.Requestor, send.Property, send.Type, send.Data, send.Offset, count);
			send.Offset += count;
			send.LastActivity = Environment.TickCount64;
			if (count == 0)
			{
				RemoveSend(i);
			}
			return;
		}
	}

	private void RemoveSend(int index)
	{
		var requestor = _sends[index].Requestor;
		_sends.RemoveAt(index);
		if (!_sends.Any(send => send.Requestor == requestor))
		{
			XLib.XSelectInput(_display, requestor, IntPtr.Zero);
		}
	}

	private sealed record PendingRead(XSelectionRequestEvent Request, Task<byte[]?> Data, long Deadline);

	private sealed class IncrementalSend(IntPtr requestor, IntPtr property, IntPtr type, byte[] data)
	{
		internal IntPtr Requestor { get; } = requestor;
		internal IntPtr Property { get; } = property;
		internal IntPtr Type { get; } = type;
		internal byte[] Data { get; } = data;
		internal int Offset { get; set; }
		internal long LastActivity { get; set; } = Environment.TickCount64;
	}

	private sealed class Session(X11XamlRootHost host, X11PointerInputSource pointer, int generation, CoreDragDropManager manager,
		CoreDragInfo info, double scale, uint button, Action<DataPackageOperation> onCompleted, Dictionary<string, Task<byte[]?>> content, long deadline)
	{
		internal X11XamlRootHost Host { get; } = host;
		internal X11PointerInputSource Pointer { get; } = pointer;
		internal int Generation { get; } = generation;
		internal CoreDragDropManager Manager { get; } = manager;
		internal CoreDragInfo Info { get; } = info;
		internal double Scale { get; } = scale > 0 ? scale : 1;
		internal uint Button { get; } = button;
		internal Action<DataPackageOperation> OnCompleted { get; } = onCompleted;
		internal Dictionary<string, Task<byte[]?>> Content { get; } = content;
		internal Dictionary<IntPtr, Task<byte[]?>> SelectionContent { get; set; } = new();
		internal IntPtr SelectionTime { get; set; }
		internal IntPtr Window { get; set; }
		internal IntPtr OriginWindow { get; } = host.RootX11Window.Window;
		internal long StartDeadline { get; } = deadline;
		internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal int StartResult;
		internal int InputEnded;
		internal volatile bool Done;
		internal X11DragSourceState State { get; set; } = new();
		internal IntPtr[] Types { get; set; } = [];
		internal Target Target { get; set; }
		internal X11DragIcon? Icon { get; set; }
		internal X11DragCursors? Cursors { get; set; }
		internal IntPtr Time { get; set; }
		internal long GrabDeadline { get; set; }
		internal int X { get; set; } = int.MinValue;
		internal int Y { get; set; } = int.MinValue;
		internal uint Mask { get; set; }
		internal uint Frame { get; set; } = pointer.LastFrameId;
		internal int TargetGeneration { get; set; }
		internal bool Own { get; set; }
		internal bool LocalDrop { get; set; }
		internal Point OwnPosition { get; set; }
		internal long OwnPollTick { get; set; }
		internal long ButtonPollTick { get; set; }
		internal long TargetLostTick { get; set; }
		internal DataPackageOperation Requested { get; set; }
		internal DataPackageOperation Feedback { get; set; }
	}

	private readonly struct DragEventSource(long id, uint frameId, Point position, uint mask) : IDragEventSource
	{
		public long Id => id;
		public uint FrameId => frameId;
		public (Point location, DragDropModifiers modifier) GetState() => (position,
			((mask & X11DragActions.ShiftMask) != 0 ? DragDropModifiers.Shift : 0)
			| ((mask & X11DragActions.ControlMask) != 0 ? DragDropModifiers.Control : 0));

		public Point GetPosition(object? relativeTo) => relativeTo switch
		{
			null => position,
			UIElement element => UIElement.GetTransform(element, null).Inverse().Transform(position),
			_ => throw new InvalidOperationException("The relative to must be a UIElement.")
		};
	}
}
