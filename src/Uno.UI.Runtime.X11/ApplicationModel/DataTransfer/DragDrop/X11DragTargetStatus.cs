// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using Windows.ApplicationModel.DataTransfer;

namespace Uno.UI.Runtime.X11;

internal sealed class X11DragTargetStatus
{
	private bool _pending;

	internal bool ProcessPosition(Action processMoved)
	{
		if (_pending)
		{
			return false;
		}
		_pending = true;
		processMoved();
		return true;
	}

	internal void CompletePosition(DataPackageOperation accepted, Action<DataPackageOperation> acknowledge)
	{
		if (!_pending)
		{
			return;
		}
		_pending = false;
		acknowledge(accepted);
	}
}
