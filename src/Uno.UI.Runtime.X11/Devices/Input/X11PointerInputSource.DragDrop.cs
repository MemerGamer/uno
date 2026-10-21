// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System.Threading;
using Microsoft.UI.Input;
using Windows.Devices.Input;
using Windows.Foundation;
using PointerDeviceType = global::Windows.Devices.Input.PointerDeviceType;
using PointerEventArgs = global::Windows.UI.Core.PointerEventArgs;

namespace Uno.UI.Runtime.X11;

internal partial class X11PointerInputSource
{
	private volatile bool _nativeDrag;
	private int _nativeDragGeneration;
	private volatile PointerPoint? _lastPoint;
	private volatile PointerPoint? _nativeDragPoint;

	internal bool IsNativeDragActive => _nativeDrag;
	internal uint LastFrameId => _lastPoint?.FrameId ?? 0;

	internal int BeginNativeDrag(uint pointerId, Point position)
	{
		var last = _lastPoint;
		_nativeDragPoint = new PointerPoint(last?.FrameId ?? 0, last?.Timestamp ?? 0,
			PointerDevice.For(PointerDeviceType.Mouse), pointerId, last?.RawPosition ?? position, last?.Position ?? position,
			true, last?.Properties ?? new PointerPointProperties());
		_nativeDrag = true;
		return Interlocked.Increment(ref _nativeDragGeneration);
	}

	internal void UpdateNativeDragPosition(int generation, Point position, uint frame)
	{
		if (generation == _nativeDragGeneration && _nativeDragPoint is { } point)
		{
			_nativeDragPoint = new PointerPoint(frame, (ulong)frame * 1000, PointerDevice.For(PointerDeviceType.Mouse),
				point.PointerId, position, position, true, point.Properties);
		}
	}

	internal void RollbackNativeDrag(int generation, bool restoreGrab)
	{
		X11XamlRootHost.QueueAction(_host, () =>
		{
			if (generation != _nativeDragGeneration)
			{
				return;
			}
			var last = _nativeDragPoint;
			_nativeDragPoint = null;
			// A release racing the state query must still reach the internal drag.
			_nativeDrag = false;
			var mask = _host.GetPointerButtonState();
			if (restoreGrab && X11DragActions.ChooseButton(mask) != 0)
			{
				_host.RestorePointerGrabAfterDrag();
				mask = _host.GetPointerButtonState();
			}
			_pressedButtons = (int)(mask >> 7) & 0xe;
			if (_pressedButtons == 0)
			{
				_host.ReleaseRestoredPointerGrab();
			}
			if (last is null)
			{
				return;
			}
			var properties = new PointerPointProperties(last.Properties)
			{
				IsLeftButtonPressed = X11DragActions.IsButtonHeld(mask, 1),
				IsMiddleButtonPressed = X11DragActions.IsButtonHeld(mask, 2),
				IsRightButtonPressed = X11DragActions.IsButtonHeld(mask, 3)
			};
			properties.SetUpdateKindFromPrevious(last.Properties);
			_previousPointerPointProperties = properties;
			_lastPoint = new PointerPoint(last.FrameId + 1, last.Timestamp, PointerDevice.For(PointerDeviceType.Mouse),
				last.PointerId, last.RawPosition, last.Position, properties.HasPressedButton, properties);
			if ((last.Properties.IsLeftButtonPressed && !properties.IsLeftButtonPressed)
				|| (last.Properties.IsMiddleButtonPressed && !properties.IsMiddleButtonPressed)
				|| (last.Properties.IsRightButtonPressed && !properties.IsRightButtonPressed))
			{
				RaisePointerReleased(new PointerEventArgs(_lastPoint, Windows.System.VirtualKeyModifiers.None));
			}
		});
	}

	// The generation protects a newer drag from a previous drag's queued cleanup.
	internal void EndNativeDrag(int generation)
	{
		var last = _nativeDragPoint;
		X11XamlRootHost.QueueAction(_host, () =>
		{
			if (generation != _nativeDragGeneration)
			{
				return;
			}
			_nativeDrag = false;
			_nativeDragPoint = null;
			_pressedButtons = 0;
			if (last is null)
			{
				return;
			}
			var properties = new PointerPointProperties { IsCanceled = true };
			var point = new PointerPoint(last.FrameId + 1, last.Timestamp, PointerDevice.For(PointerDeviceType.Mouse),
				last.PointerId, last.RawPosition, last.Position, false, properties);
			_previousPointerPointProperties = properties;
			var args = new PointerEventArgs(point, Windows.System.VirtualKeyModifiers.None);
			PointerCancelled?.Invoke(this, args);
			PointerCaptureLost?.Invoke(this, args);
		});
	}
}
