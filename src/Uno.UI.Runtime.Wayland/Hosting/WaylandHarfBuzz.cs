#nullable enable

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Uno.Foundation.Logging;

namespace Uno.UI.Runtime.Wayland;

// Mirrors the X11 host's HarfBuzz isolation. libHarfBuzzSharp exports the same symbols as the system
// libharfbuzz.so.0, which fontconfig, freetype or GTK may load first; lazily bound hb_* calls made inside
// libHarfBuzzSharp can then resolve to the system library and mix objects of two builds (free(): invalid pointer).
internal static partial class WaylandHarfBuzz
{
	private const int RTLD_NOW = 2;
	private const int RTLD_DEEPBIND = 8;

	// HarfBuzzSharp is the font providers' dependency, not the host's: an app whose font provider doesn't use it
	// has nothing to isolate.
	internal static void Isolate(bool useSystemHarfBuzz)
	{
		if (TryLoadHarfBuzzSharp() is not { } harfBuzzSharp)
		{
			return;
		}

		if (useSystemHarfBuzz)
		{
			// Redirects every libHarfBuzzSharp call to the system library instead.
			NativeLibrary.SetDllImportResolver(harfBuzzSharp, static (libraryName, assembly, searchPath)
				=> libraryName == "libHarfBuzzSharp" && NativeLibrary.TryLoad("libharfbuzz.so.0", assembly, searchPath, out var library) ? library : IntPtr.Zero);
			return;
		}

		// Otherwise libHarfBuzzSharp is preloaded with RTLD_DEEPBIND so that its own symbols win; later loads reuse it.
		var search = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES")?.ToString() ?? "";
		try
		{
			foreach (var path in search.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
			{
				var libraryPath = Path.Combine(path, "libHarfBuzzSharp.so");
				if (File.Exists(libraryPath) && dlopen(libraryPath, RTLD_NOW | RTLD_DEEPBIND) != IntPtr.Zero)
				{
					return;
				}
			}
			typeof(WaylandHarfBuzz).LogError()?.Error($"Could not preload HarfBuzz with RTLD_DEEPBIND: libHarfBuzzSharp.so was not found in the native DLL search directories ({search}).");
		}
		catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
		{
			typeof(WaylandHarfBuzz).LogError()?.Error($"Could not preload HarfBuzz with RTLD_DEEPBIND: {error.Message}");
		}
	}

	private static Assembly? TryLoadHarfBuzzSharp()
	{
		try
		{
			return Assembly.Load("HarfBuzzSharp");
		}
		catch (FileNotFoundException)
		{
			return null;
		}
	}

	// libdl.so.2 rather than libc.so.6, so that glibc versions older than 2.34 are covered too.
	[LibraryImport("libdl.so.2", StringMarshalling = StringMarshalling.Utf8)]
	private static partial IntPtr dlopen(string path, int mode);
}
