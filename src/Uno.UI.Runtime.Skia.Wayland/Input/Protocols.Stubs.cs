// Licensed under the MIT License. See LICENSE in the project root.
#nullable enable

using System;

namespace Uno.WinUI.Runtime.Skia.Wayland.Protocols;

// Temporary proxy API until the protocol generator is integrated. No native implementation.
internal sealed class InputEventArgs : EventArgs
{
	public uint Serial
	{
		get; init;
	}
	public uint Time
	{
		get; init;
	}
	public uint Axis
	{
		get; init;
	}
	public uint Button
	{
		get; init;
	}
	public uint Key
	{
		get; init;
	}
	public uint State
	{
		get; init;
	}
	public uint Format
	{
		get; init;
	}
	public uint Size
	{
		get; init;
	}
	public uint Depressed
	{
		get; init;
	}
	public uint Latched
	{
		get; init;
	}
	public uint Locked
	{
		get; init;
	}
	public uint Group
	{
		get; init;
	}
	public uint Capabilities
	{
		get; init;
	}
	public int Fd
	{
		get; init;
	}
	public int Value
	{
		get; init;
	}
	public int Rate
	{
		get; init;
	}
	public int Delay
	{
		get; init;
	}
	public int Id
	{
		get; init;
	}
	public double X
	{
		get; init;
	}
	public double Y
	{
		get; init;
	}
	public double AxisValue
	{
		get; init;
	}
	public nint Surface
	{
		get; init;
	}
	public uint[] Keys = Array.Empty<uint>();
}
internal abstract class WlSeat : IDisposable
{
	public abstract event EventHandler<InputEventArgs>? Capabilities;
	public abstract WlPointer GetPointer();
	public abstract WlKeyboard GetKeyboard();
	public abstract WlTouch GetTouch();
	public abstract void Dispose();
}
internal abstract class WlPointer : IDisposable
{
	public abstract void SetCursor(uint serial, WlSurface? surface, int hotspotX, int hotspotY);
	public abstract uint Version
	{
		get;
	}
	public abstract event EventHandler<InputEventArgs>? Enter;
	public abstract event EventHandler<InputEventArgs>? Leave;
	public abstract event EventHandler<InputEventArgs>? Motion;
	public abstract event EventHandler<InputEventArgs>? Button;
	public abstract event EventHandler<InputEventArgs>? Axis;
	public abstract event EventHandler<InputEventArgs>? AxisDiscrete;
	public abstract event EventHandler<InputEventArgs>? AxisValue120;
	public abstract event EventHandler<InputEventArgs>? AxisStop;
	public abstract event EventHandler<InputEventArgs>? Frame;
	public abstract void Dispose();
}
internal abstract class WlKeyboard : IDisposable
{
	public abstract event EventHandler<InputEventArgs>? Keymap;
	public abstract event EventHandler<InputEventArgs>? Enter;
	public abstract event EventHandler<InputEventArgs>? Leave;
	public abstract event EventHandler<InputEventArgs>? Key;
	public abstract event EventHandler<InputEventArgs>? Modifiers;
	public abstract event EventHandler<InputEventArgs>? RepeatInfo;
	public abstract void Dispose();
}
internal abstract class WlTouch : IDisposable
{
	public abstract event EventHandler<InputEventArgs>? Down;
	public abstract event EventHandler<InputEventArgs>? Up;
	public abstract event EventHandler<InputEventArgs>? Motion;
	public abstract event EventHandler<InputEventArgs>? Frame;
	public abstract event EventHandler<InputEventArgs>? Cancel;
	public abstract void Dispose();
}

internal abstract class WpCursorShapeDeviceV1 : IDisposable
{
	public abstract void SetShape(uint serial, uint shape);
	public abstract void Dispose();
}

internal abstract class WlSurface : IDisposable
{
	// A generated adapter must marshal this borrowed wl_buffer pointer without taking ownership.
	public abstract void Attach(nint buffer, int x, int y);
	public abstract void SetBufferScale(int scale);
	public abstract void Damage(int x, int y, int width, int height);
	public abstract void Commit();
	public abstract void Dispose();
}
