// Licensed under the MIT License. See LICENSE in the project root.
#nullable enable

using System;
using System.Collections.Generic;
using Windows.UI.Core;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland.Input;

internal sealed class WaylandProtocolInputConnection : IWaylandInputConnection
{
	private readonly WlSeat _seat;
	private readonly Func<nint, bool> _ownsSurface;
	private readonly Action<Action> _post;
	private readonly Action<uint, CoreCursor?> _cursor;

	public WaylandProtocolInputConnection(WlSeat seat, Func<nint, bool> ownsSurface, Action<Action> post, Action<uint, CoreCursor?> cursor)
	{
		_seat = seat;
		_ownsSurface = ownsSurface;
		_post = post;
		_cursor = cursor;
	}
	public void Post(Action action) => _post(action);
	public void SetCursor(uint serial, CoreCursor? cursor) => _cursor(serial, cursor);

	public IDisposable GetPointer(WaylandPointerInputSource sink)
	{
		var proxy = _seat.GetPointer();
		var binding = new Binding(proxy);
		bool focused = false;
		EventHandler<InputEventArgs> enter = (_, e) => { focused = _ownsSurface(e.Surface); if (focused) { sink.Enter(e.Serial, e.X, e.Y); } };
		EventHandler<InputEventArgs> leave = (_, e) => { if (focused) { sink.Leave(); } focused = false; };
		EventHandler<InputEventArgs> motion = (_, e) => { if (focused) { sink.Motion(e.Time, e.X, e.Y); } };
		EventHandler<InputEventArgs> button = (_, e) => { if (focused) { sink.Button(e.Time, e.Button, e.State); } };
		EventHandler<InputEventArgs> axis = (_, e) => { if (focused) { sink.Axis(e.Time, e.Axis, e.AxisValue); if (proxy.Version < 5) { sink.Frame(); } } };
		EventHandler<InputEventArgs> discrete = (_, e) => { if (focused) { sink.AxisDiscrete(e.Axis, e.Value); } };
		EventHandler<InputEventArgs> value120 = (_, e) => { if (focused) { sink.AxisValue120(e.Axis, e.Value); } };
		EventHandler<InputEventArgs> stop = (_, e) => { if (focused) { sink.AxisStop(e.Axis); } };
		EventHandler<InputEventArgs> frame = (_, e) => sink.Frame();
		proxy.Enter += enter;
		proxy.Leave += leave;
		proxy.Motion += motion;
		proxy.Button += button;
		proxy.Axis += axis;
		proxy.AxisDiscrete += discrete;
		proxy.AxisValue120 += value120;
		proxy.AxisStop += stop;
		proxy.Frame += frame;
		binding.Remove.Add(() =>
		{
			proxy.Enter -= enter;
			proxy.Leave -= leave;
			proxy.Motion -= motion;
			proxy.Button -= button;
			proxy.Axis -= axis;
			proxy.AxisDiscrete -= discrete;
			proxy.AxisValue120 -= value120;
			proxy.AxisStop -= stop;
			proxy.Frame -= frame;
		});
		return binding;
	}
	public IDisposable GetKeyboard(WaylandKeyboardInputSource sink)
	{
		var proxy = _seat.GetKeyboard();
		var binding = new Binding(proxy);
		EventHandler<InputEventArgs> keymap = (_, e) => sink.Keymap(e.Format, e.Fd, e.Size);
		EventHandler<InputEventArgs> enter = (_, e) => { if (_ownsSurface(e.Surface)) { sink.Enter(e.Keys); } };
		EventHandler<InputEventArgs> leave = (_, e) => { if (_ownsSurface(e.Surface)) { sink.Leave(); } };
		EventHandler<InputEventArgs> key = (_, e) => sink.Key(e.Key, e.State);
		EventHandler<InputEventArgs> modifiers = (_, e) => sink.UpdateModifiers(e.Depressed, e.Latched, e.Locked, e.Group);
		EventHandler<InputEventArgs> repeat = (_, e) => sink.RepeatInfo(e.Rate, e.Delay);
		proxy.Keymap += keymap;
		proxy.Enter += enter;
		proxy.Leave += leave;
		proxy.Key += key;
		proxy.Modifiers += modifiers;
		proxy.RepeatInfo += repeat;
		binding.Remove.Add(() =>
		{
			proxy.Keymap -= keymap;
			proxy.Enter -= enter;
			proxy.Leave -= leave;
			proxy.Key -= key;
			proxy.Modifiers -= modifiers;
			proxy.RepeatInfo -= repeat;
		});
		return binding;
	}
	public IDisposable GetTouch(WaylandPointerInputSource sink)
	{
		var proxy = _seat.GetTouch();
		var binding = new Binding(proxy);
		var ids = new HashSet<int>();
		EventHandler<InputEventArgs> down = (_, e) => { if (_ownsSurface(e.Surface)) { ids.Add(e.Id); sink.TouchDown(e.Time, e.Id, e.X, e.Y); } };
		EventHandler<InputEventArgs> up = (_, e) => { if (ids.Remove(e.Id)) { sink.TouchUp(e.Time, e.Id); } };
		EventHandler<InputEventArgs> motion = (_, e) => { if (ids.Contains(e.Id)) { sink.TouchMotion(e.Time, e.Id, e.X, e.Y); } };
		EventHandler<InputEventArgs> frame = (_, e) => sink.TouchFrame();
		EventHandler<InputEventArgs> cancel = (_, e) => { ids.Clear(); sink.TouchCancel(); };
		proxy.Down += down;
		proxy.Up += up;
		proxy.Motion += motion;
		proxy.Frame += frame;
		proxy.Cancel += cancel;
		binding.Remove.Add(() => { proxy.Down -= down; proxy.Up -= up; proxy.Motion -= motion; proxy.Frame -= frame; proxy.Cancel -= cancel; });
		return binding;
	}
	private sealed class Binding : IDisposable
	{
		private IDisposable? _proxy;
		public List<Action> Remove { get; } = new();
		public Binding(IDisposable proxy) => _proxy = proxy;
		public void Dispose()
		{
			if (_proxy is not { } proxy)
			{
				return;
			}
			_proxy = null;
			foreach (var remove in Remove)
			{
				remove();
			}
			Remove.Clear();
			proxy.Dispose();
		}
	}
}
