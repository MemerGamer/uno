#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Uno.UI.Hosting;
using Microsoft.Win32.SafeHandles;

namespace Uno.UI.Runtime.Wayland.Input;

internal sealed class WaylandKeyboardInputSource : IUnoKeyboardInputSource, IDisposable
{
	public event TypedEventHandler<object, KeyEventArgs>? KeyDown;
	public event TypedEventHandler<object, KeyEventArgs>? KeyUp;
	// Like X11, text is carried by KeyEventArgs.UnicodeKey, avoiding duplicate insertion.
	event TypedEventHandler<object, CharacterReceivedEventArgs>? IUnoKeyboardInputSource.CharacterReceived { add { } remove { } }
	private readonly Action<Action> _dispatch;
	private readonly Action<bool> _activate;
	private readonly IWaylandInputConnection _connection;
	private readonly Dictionary<uint, VirtualKey> _pressed = new();
	private nint _context, _keymap, _state;
	private Timer? _repeat;
	private uint _repeatKey;
	private int _generation, _rate = 25, _delay = 600;
	private bool _focused, _disposed, _levelThree;
	public VirtualKeyModifiers Modifiers
	{
		get; private set;
	}

	public WaylandKeyboardInputSource(IWaylandInputConnection connection, Action<Action> dispatch, Action<bool> activate)
	{
		_connection = connection;
		_dispatch = dispatch;
		_activate = activate;
	}

	public void Keymap(uint format, int fd, uint size)
	{
		StopRepeat();
		if (format != 1)
		{
			using var unused = new SafeFileHandle(fd, ownsHandle: true);
			ClearKeymap();
			return;
		}
		var text = WaylandXkb.ReadKeymap(fd, size);
		var context = WaylandXkb.ContextNew(0);
		nint keymap = 0, state = 0;
		try
		{
			if (context == 0)
			{
				throw new InvalidOperationException("Cannot create XKB context.");
			}
			keymap = WaylandXkb.KeymapNew(context, text, 1, 0);
			if (keymap == 0)
			{
				throw new InvalidOperationException("Cannot compile compositor keymap.");
			}
			state = WaylandXkb.StateNew(keymap);
			if (state == 0)
			{
				throw new InvalidOperationException("Cannot create XKB state.");
			}
			ClearKeymap();
			_context = context;
			_keymap = keymap;
			_state = state;
		}
		catch
		{
			if (state != 0)
			{
				WaylandXkb.StateUnref(state);
			}
			if (keymap != 0)
			{
				WaylandXkb.KeymapUnref(keymap);
			}
			if (context != 0)
			{
				WaylandXkb.ContextUnref(context);
			}
			throw;
		}
	}

	public void UpdateModifiers(uint depressed, uint latched, uint locked, uint group)
	{
		if (_state == 0)
		{
			return;
		}
		// The compositor owns state: do not additionally call xkb_state_update_key.
		WaylandXkb.UpdateMask(_state, depressed, latched, locked, 0, 0, group);
		Modifiers = VirtualKeyModifiers.None;
		_levelThree = WaylandXkb.ModActive(_state, "Mod5", 8) > 0;
		if (WaylandXkb.ModActive(_state, "Shift", 8) > 0)
		{
			Modifiers |= VirtualKeyModifiers.Shift;
		}
		if (WaylandXkb.ModActive(_state, "Control", 8) > 0)
		{
			Modifiers |= VirtualKeyModifiers.Control;
		}
		if (WaylandXkb.ModActive(_state, "Mod1", 8) > 0)
		{
			Modifiers |= VirtualKeyModifiers.Menu;
		}
		if (WaylandXkb.ModActive(_state, "Mod4", 8) > 0)
		{
			Modifiers |= VirtualKeyModifiers.Windows;
		}
	}

	public void Enter(ReadOnlySpan<uint> heldKeys)
	{
		_focused = true;
		_pressed.Clear();
		foreach (var key in heldKeys)
		{
			_pressed[key] = _state == 0 || key > uint.MaxValue - 8 ? VirtualKey.None : WaylandKeyMap.FromKeysym(WaylandXkb.GetSym(_state, key + 8));
		}
		_dispatch(() => _activate(true));
	}
	public void Leave()
	{
		StopRepeat();
		foreach (var key in _pressed)
		{
			Emit(key.Key, key.Value, false, false);
		}
		_pressed.Clear();
		Modifiers = VirtualKeyModifiers.None;
		_levelThree = false;
		if (_focused)
		{
			_dispatch(() => _activate(false));
		}
		_focused = false;
	}
	public void RepeatInfo(int rate, int delay)
	{
		_rate = Math.Max(0, rate);
		_delay = Math.Max(0, delay);
		StopRepeat();
	}
	public void Key(uint key, uint state)
	{
		if (!_focused || _disposed || key > uint.MaxValue - 8)
		{
			return;
		}
		var down = state == 1;
		if (state == 2)
		{
			if (_rate == 0 && _pressed.TryGetValue(key, out var repeated))
			{
				Emit(key, repeated, true, true);
			}
			return;
		}
		if (state > 2)
		{
			return;
		}
		if (down)
		{
			if (_state == 0)
			{
				return;
			}
			var vk = WaylandKeyMap.FromKeysym(WaylandXkb.GetSym(_state, key + 8));
			var wasDown = _pressed.ContainsKey(key);
			_pressed[key] = vk;
			Emit(key, vk, true, wasDown);
			if (_rate > 0 && WaylandXkb.Repeats(_keymap, key + 8) != 0)
			{
				StopRepeat();
				_repeatKey = key;
				var generation = _generation;
				_repeat = new Timer(_ => _connection.Post(() =>
				{
					if (!_disposed && _focused && generation == _generation && _pressed.TryGetValue(key, out var repeated))
					{
						Emit(key, repeated, true, true);
					}
				}), null, _delay, Math.Max(1, 1000 / _rate));
			}
		}
		else if (_pressed.Remove(key, out var vk))
		{
			if (key == _repeatKey)
			{
				StopRepeat();
			}
			Emit(key, vk, false, true);
		}
	}
	private void Emit(uint key, VirtualKey vk, bool down, bool previous)
	{
		var unicode = down && _state != 0 ? WaylandXkb.GetUtf32(_state, key + 8) : 0;
		char? character = unicode is >= 32 and <= 0xffff && unicode is not (>= 0xd800 and <= 0xdfff) || unicode == 13 ? (char)unicode : null;
		if (!_levelThree && (Modifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu | VirtualKeyModifiers.Windows)) != 0)
		{
			character = null;
		}
		var args = new KeyEventArgs("keyboard", vk, Modifiers, new CorePhysicalKeyStatus
		{
			ScanCode = key,
			RepeatCount = 1,
			WasKeyDown = previous,
			IsKeyReleased = !down,
			IsMenuKeyDown = (Modifiers & VirtualKeyModifiers.Menu) != 0
		}, character);
		_dispatch(() => { if (down) { KeyDown?.Invoke(this, args); } else { KeyUp?.Invoke(this, args); } });
	}
	private void StopRepeat()
	{
		_generation++;
		_repeat?.Dispose();
		_repeat = null;
	}
	private void ClearKeymap()
	{
		if (_state != 0)
		{
			WaylandXkb.StateUnref(_state);
		}
		if (_keymap != 0)
		{
			WaylandXkb.KeymapUnref(_keymap);
		}
		if (_context != 0)
		{
			WaylandXkb.ContextUnref(_context);
		}
		_context = _keymap = _state = 0;
		_levelThree = false;
		Modifiers = VirtualKeyModifiers.None;
	}
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		Leave();
		_disposed = true;
		ClearKeymap();
	}
}
