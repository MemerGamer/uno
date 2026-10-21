// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Uno.UI.Runtime.X11;

internal sealed class X11DragSelection(IntPtr window, IntPtr timestamp, Dictionary<IntPtr, Task<byte[]?>> content)
{
	internal IntPtr Window { get; } = window;
	internal IntPtr Timestamp { get; } = timestamp;
	internal Dictionary<IntPtr, Task<byte[]?>> Content { get; private set; } = content;
	internal long Deadline { get; private set; } = long.MaxValue;
	internal bool IsRetired { get; private set; }

	internal void Retire(Action<IntPtr> destroyWindow)
	{
		if (IsRetired)
		{
			return;
		}
		IsRetired = true;
		Content.Clear();
		destroyWindow(Window);
	}

	internal void RetainCompleted(long now)
	{
		var snapshot = new Dictionary<IntPtr, Task<byte[]?>>();
		foreach (var pair in Content)
		{
			if (pair.Value.IsCompletedSuccessfully && pair.Value.Result is { } bytes)
			{
				snapshot[pair.Key] = Task.FromResult<byte[]?>(bytes);
			}
		}
		Content = snapshot;
		Deadline = now + 2_000;
	}

	internal bool IsExpired(long now) => now >= Deadline;
}
