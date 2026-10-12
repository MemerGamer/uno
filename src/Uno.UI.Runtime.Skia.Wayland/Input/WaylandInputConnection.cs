// Licensed under the MIT License. See LICENSE in the project root.
#nullable enable

using System;
using Windows.UI.Core;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland.Input;

// The protocol adapter owns proxies; all callbacks and requests run on the display thread.
internal interface IWaylandInputConnection
{
	IDisposable GetPointer(WaylandPointerInputSource sink);
	IDisposable GetKeyboard(WaylandKeyboardInputSource sink);
	IDisposable GetTouch(WaylandPointerInputSource sink);
	void SetCursor(uint enterSerial, CoreCursor? cursor);
	void Post(Action action);
}

internal sealed class WaylandSeat : IDisposable
{
	private readonly IWaylandInputConnection _connection;
	private readonly WaylandPointerInputSource _pointer;
	private readonly WaylandKeyboardInputSource _keyboard;
	private IDisposable? _pointerProxy, _keyboardProxy, _touchProxy;
	private bool _disposed;
	private readonly WlSeat _seat;

	public WaylandSeat(WlSeat seat, IWaylandInputConnection connection, WaylandPointerInputSource pointer, WaylandKeyboardInputSource keyboard)
	{
		_seat = seat;
		_seat.Capabilities += OnCapabilities;
		_connection = connection;
		_pointer = pointer;
		_keyboard = keyboard;
	}

	private void OnCapabilities(object? sender, InputEventArgs args) => Capabilities(args.Capabilities);

	public void Capabilities(uint capabilities)
	{
		if (_disposed)
		{
			return;
		}
		if ((capabilities & 1) != 0)
		{
			_pointerProxy ??= _connection.GetPointer(_pointer);
		}
		else if (_pointerProxy is { } pointer)
		{
			_pointer.ResetPointer();
			pointer.Dispose();
			_pointerProxy = null;
		}
		if ((capabilities & 2) != 0)
		{
			_keyboardProxy ??= _connection.GetKeyboard(_keyboard);
		}
		else if (_keyboardProxy is { } keyboard)
		{
			_keyboard.Leave();
			keyboard.Dispose();
			_keyboardProxy = null;
		}
		if ((capabilities & 4) != 0)
		{
			_touchProxy ??= _connection.GetTouch(_pointer);
		}
		else if (_touchProxy is { } touch)
		{
			_pointer.TouchCancel();
			touch.Dispose();
			_touchProxy = null;
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		Capabilities(0);
		_disposed = true;
		_seat.Capabilities -= OnCapabilities;
		_keyboard.Dispose();
	}
}
