// Licensed under the MIT License.
#nullable enable
using System;

namespace Uno.WinUI.Runtime.Skia.Wayland.Rendering;

internal static class WaylandEglConfig
{
	public static IntPtr Select(ReadOnlySpan<IntPtr> configs, Func<IntPtr, int?> getMinimumSwapInterval)
	{
		foreach (var config in configs)
		{
			if (getMinimumSwapInterval(config) == 0)
			{
				return config;
			}
		}
		throw new NotSupportedException("No EGL config supports swap interval zero.");
	}
}
