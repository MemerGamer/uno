// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable
using System;

namespace Uno.WinUI.Runtime.Skia.Wayland.Rendering;

internal static class WaylandEglConfig
{
	public static IntPtr Select(ReadOnlySpan<IntPtr> configs, Func<IntPtr, int?> getMinimumSwapInterval, Func<IntPtr, int?> getSampleBuffers, Func<IntPtr, int?> getSamples)
	{
		foreach (var config in configs)
		{
			if (getMinimumSwapInterval(config) == 0 && getSampleBuffers(config) == 0 && getSamples(config) == 0)
			{
				return config;
			}
		}
		throw new NotSupportedException("No single-sampled EGL config supports swap interval zero.");
	}
}
