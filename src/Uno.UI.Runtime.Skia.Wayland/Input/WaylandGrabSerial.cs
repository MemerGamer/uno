// Licensed under the MIT License. See LICENSE in the project root.
#nullable enable

namespace Uno.WinUI.Runtime.Skia.Wayland.Input;

// Dispatch thread. The serial of the implicit grab in progress on one window: the latest pointer button press
// or touch down, forgotten once that button or touch point is released, the pointer leaves, or the device goes
// away. wl_data_device.start_drag needs it, and the compositor ignores a serial whose grab already ended.
internal sealed class WaylandGrabSerial
{
	private (uint Serial, uint Button)? _button;
	private (uint Serial, int Id)? _touch;

	internal uint? Serial => _button?.Serial ?? _touch?.Serial;

	internal void Press(uint serial, uint button) => _button = (serial, button);

	internal void Release(uint button)
	{
		if (_button is { } press && press.Button == button)
		{
			_button = null;
		}
	}

	internal void TouchDown(uint serial, int id) => _touch = (serial, id);

	internal void TouchUp(int id)
	{
		if (_touch is { } touch && touch.Id == id)
		{
			_touch = null;
		}
	}

	internal void ClearPointer() => _button = null;

	internal void ClearTouch() => _touch = null;
}
