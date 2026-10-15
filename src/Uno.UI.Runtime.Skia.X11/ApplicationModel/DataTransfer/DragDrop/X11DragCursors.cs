// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel.DataTransfer;

namespace Uno.WinUI.Runtime.Skia.X11;

internal sealed class X11DragCursors : IDisposable
{
	private readonly IntPtr _display;
	private readonly Dictionary<DataPackageOperation, IntPtr> _cursors = new();

	private X11DragCursors(IntPtr display) => _display = display;

	internal IntPtr this[DataPackageOperation operation] => _cursors.TryGetValue(operation, out var cursor) ? cursor : _cursors[DataPackageOperation.None];

	internal static unsafe X11DragCursors Create(IntPtr display)
	{
		using var xlock = X11Helper.XLock(display);
		var cursors = new X11DragCursors(display);
		IntPtr library = IntPtr.Zero;
		try
		{
			delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr> load = null;
			if (NativeLibrary.TryLoad("libXcursor.so.1", out library) && NativeLibrary.TryGetExport(library, "XcursorLibraryLoadCursor", out var entry))
			{
				load = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr>)entry;
			}
			foreach (var operation in new[] { DataPackageOperation.None, DataPackageOperation.Copy, DataPackageOperation.Move, DataPackageOperation.Link })
			{
				var name = operation switch
				{
					DataPackageOperation.Copy => "dnd-copy",
					DataPackageOperation.Move => "dnd-move",
					DataPackageOperation.Link => "dnd-link",
					_ => "dnd-none"
				};
				IntPtr cursor = IntPtr.Zero;
				if (load != null)
				{
					fixed (byte* text = Encoding.UTF8.GetBytes(name + '\0'))
					{
						cursor = load(display, text);
					}
					if (cursor == IntPtr.Zero && operation == DataPackageOperation.None)
					{
						fixed (byte* text = "dnd-no-drop\0"u8)
						{
							cursor = load(display, text);
						}
					}
				}
				if (cursor == IntPtr.Zero)
				{
					cursor = XLib.XCreateFontCursor(display, operation switch
					{
						DataPackageOperation.Copy => CursorFontShape.XC_plus,
						DataPackageOperation.Move => CursorFontShape.XC_fleur,
						DataPackageOperation.Link => CursorFontShape.XC_hand2,
						_ => CursorFontShape.XC_circle
					});
				}
				cursors._cursors[operation] = cursor;
			}
			return cursors;
		}
		catch
		{
			cursors.Dispose();
			throw;
		}
		finally
		{
			if (library != IntPtr.Zero)
			{
				NativeLibrary.Free(library);
			}
		}
	}

	public void Dispose()
	{
		using var xlock = X11Helper.XLock(_display);
		foreach (var cursor in _cursors.Values)
		{
			if (cursor != IntPtr.Zero)
			{
				XLib.XFreeCursor(_display, cursor);
			}
		}
		_cursors.Clear();
	}
}
