// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Uno.UI.Runtime.X11;

internal sealed class X11SelectionConnection : IX11SelectionConnection
{
	private readonly IntPtr _display;
	private readonly IntPtr _window;
	private readonly IntPtr _property;

	internal X11SelectionConnection()
	{
		_display = XLib.XOpenDisplay(IntPtr.Zero);
		if (_display == IntPtr.Zero)
		{
			throw new InvalidOperationException("Could not open the X11 selection connection.");
		}
		_window = XLib.XCreateSimpleWindow(_display, XLib.XDefaultRootWindow(_display), 0, 0, 1, 1, 0, IntPtr.Zero, IntPtr.Zero);
		_property = X11Helper.GetAtom(_display, "_UNO_SELECTION");
		IncrementalType = X11Helper.GetAtom(_display, X11Helper.INCR);
		XLib.XSelectInput(_display, _window, (IntPtr)XEventMask.PropertyChangeMask);
	}

	public long TickCount => Environment.TickCount64;
	public IntPtr IncrementalType { get; }
	public IntPtr GetOwner(IntPtr selection) => XLib.XGetSelectionOwner(_display, selection);

	public void Convert(IntPtr selection, IntPtr format, IntPtr timestamp)
	{
		XLib.XConvertSelection(_display, selection, format, _property, _window, timestamp);
		XLib.XFlush(_display);
	}

	public bool TrySelectionNotify(IntPtr selection, IntPtr format, out bool accepted)
	{
		accepted = false;
		if (!X11Helper.XCheckTypedWindowEvent(_display, _window, (int)XEventName.SelectionNotify, out var ev))
		{
			return false;
		}
		accepted = ev.SelectionEvent.selection == selection && ev.SelectionEvent.target == format && ev.SelectionEvent.property == _property;
		return true;
	}

	public bool TryPropertyNotify()
	{
		XLib.XFlush(_display);
		return X11Helper.XCheckTypedWindowEvent(_display, _window, (int)XEventName.PropertyNotify, out var ev)
			&& ev.PropertyEvent.atom == _property && ev.PropertyEvent.state == X11Helper.PropertyNewValue;
	}

	public X11SelectionProperty ReadProperty()
	{
		XLib.XGetWindowProperty(_display, _window, _property, IntPtr.Zero, X11Helper.LONG_LENGTH, true, X11Helper.AnyPropertyType,
			out var type, out var format, out var count, out _, out var data);
		try
		{
			var bytes = new byte[checked((int)count * (format / 8))];
			if (bytes.Length > 0)
			{
				Marshal.Copy(data, bytes, 0, bytes.Length);
			}
			XLib.XFlush(_display);
			return new(type, format, bytes);
		}
		finally
		{
			if (data != IntPtr.Zero)
			{
				XLib.XFree(data);
			}
		}
	}

	public void Wait(CancellationToken cancellationToken)
	{
		cancellationToken.WaitHandle.WaitOne(10);
		cancellationToken.ThrowIfCancellationRequested();
	}

	public void Dispose()
	{
		XLib.XDestroyWindow(_display, _window);
		XLib.XCloseDisplay(_display);
	}
}
