// Licensed under the MIT License. See LICENSE in the project root.
#nullable enable

using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.IO;
using Uno.WinUI.Runtime.Skia.Linux.FrameBuffer.Devices.Input;

namespace Uno.WinUI.Runtime.Skia.Wayland.Input;

// Wayland-specific additions to the FrameBuffer binding; see input.md for shared-source extraction.
internal static partial class WaylandXkb
{
	private const string Library = "libxkbcommon.so.0";
	internal static nint ContextNew(uint flags) => LibXKBCommon.xkb_context_new((LibXKBCommon.xkb_context_flags)flags);
	[LibraryImport(Library, EntryPoint = "xkb_context_unref")] internal static partial void ContextUnref(nint context);
	[LibraryImport(Library, EntryPoint = "xkb_keymap_new_from_string", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint KeymapNew(nint context, string text, uint format, uint flags);
	[LibraryImport(Library, EntryPoint = "xkb_keymap_unref")] internal static partial void KeymapUnref(nint keymap);
	internal static nint StateNew(nint keymap) => LibXKBCommon.xkb_state_new(keymap);
	[LibraryImport(Library, EntryPoint = "xkb_state_unref")] internal static partial void StateUnref(nint state);
	[LibraryImport(Library, EntryPoint = "xkb_state_update_mask")] internal static partial uint UpdateMask(nint state, uint depressed, uint latched, uint locked, uint depressedLayout, uint latchedLayout, uint lockedLayout);
	[LibraryImport(Library, EntryPoint = "xkb_state_mod_name_is_active", StringMarshalling = StringMarshalling.Utf8)] internal static partial int ModActive(nint state, string name, uint components);
	internal static uint GetSym(nint state, uint key) => LibXKBCommon.xkb_state_key_get_one_sym(state, key);
	[LibraryImport(Library, EntryPoint = "xkb_state_key_get_utf32")] internal static partial uint GetUtf32(nint state, uint key);
	[LibraryImport(Library, EntryPoint = "xkb_keymap_key_repeats")] internal static partial int Repeats(nint keymap, uint key);

	internal static string ReadKeymap(int fd, uint size)
	{
		using var handle = new SafeFileHandle(fd, ownsHandle: true);
		if (size == 0 || size > 16 * 1024 * 1024)
		{
			throw new InvalidDataException("Invalid Wayland keymap size.");
		}
		var bytes = new byte[checked((int)size)];
		var offset = 0;
		while (offset < bytes.Length)
		{
			var read = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
			if (read == 0)
			{
				throw new EndOfStreamException("Truncated Wayland keymap.");
			}
			offset += read;
		}
		if (bytes[^1] != 0)
		{
			throw new InvalidDataException("Wayland keymap is not terminated.");
		}
		return System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1);
	}
}
