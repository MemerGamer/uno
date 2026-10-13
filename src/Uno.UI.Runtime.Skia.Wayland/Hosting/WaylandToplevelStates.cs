// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// xdg_toplevel.state values as flags (bit = 1 << protocol value).
[Flags]
internal enum WaylandToplevelStates : uint
{
	None = 0,
	Maximized = 1 << 1,
	Fullscreen = 1 << 2,
	Resizing = 1 << 3,
	Activated = 1 << 4,
	TiledLeft = 1 << 5,
	TiledRight = 1 << 6,
	TiledTop = 1 << 7,
	TiledBottom = 1 << 8,
	Suspended = 1 << 9,
	Tiled = TiledLeft | TiledRight | TiledTop | TiledBottom,
	All = Maximized | Fullscreen | Resizing | Activated | Tiled | Suspended,
}

// xdg_toplevel.wm_capabilities values as flags; All when the compositor does not send the event.
[Flags]
internal enum WaylandWindowCapabilities : uint
{
	None = 0,
	WindowMenu = 1 << 1,
	Maximize = 1 << 2,
	Fullscreen = 1 << 3,
	Minimize = 1 << 4,
	All = WindowMenu | Maximize | Fullscreen | Minimize,
}

internal static class WaylandToplevelStateParser
{
	// Both events carry a wl_array of native-endian uint32 values; unknown values are ignored.
	internal static WaylandToplevelStates ParseStates(ReadOnlySpan<byte> array)
		=> (WaylandToplevelStates)Parse(array, (uint)WaylandToplevelStates.All);

	internal static WaylandWindowCapabilities ParseCapabilities(ReadOnlySpan<byte> array)
		=> (WaylandWindowCapabilities)Parse(array, (uint)WaylandWindowCapabilities.All);

	private static uint Parse(ReadOnlySpan<byte> array, uint known)
	{
		uint flags = 0;
		for (var i = 0; i + sizeof(uint) <= array.Length; i += sizeof(uint))
		{
			var value = BitConverter.ToUInt32(array[i..]);
			if (value < 32)
			{
				flags |= 1u << (int)value;
			}
		}
		return flags & known;
	}
}
