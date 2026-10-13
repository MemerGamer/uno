// Licensed under the MIT License.
#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using Uno.Foundation.Logging;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal static partial class WaylandHarfBuzz
{
	private const int RTLD_NOW = 2;
	private const int RTLD_DEEPBIND = 8;

	/// <summary>
	/// Loads libHarfBuzzSharp.so with RTLD_DEEPBIND so its internal hb_* calls bind to its own symbols,
	/// as the X11 host does.
	/// </summary>
	/// <remarks>
	/// Once the system libharfbuzz.so.0 is loaded (e.g. through fontconfig/freetype or GTK), lazily bound hb_*
	/// calls made inside libHarfBuzzSharp.so can resolve to libharfbuzz.so.0 instead, mixing objects of two
	/// HarfBuzz builds and crashing (e.g. free(): invalid pointer). Later DllImport loads reuse this handle.
	/// </remarks>
	internal static void PreloadWithDeepBind()
	{
		var search = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES")?.ToString() ?? "";
		try
		{
			foreach (var path in search.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
			{
				var libPath = Path.Combine(path, "libHarfBuzzSharp.so");
				if (File.Exists(libPath) && DeepBind(libPath) != IntPtr.Zero)
				{
					return;
				}
			}

			if (typeof(WaylandHarfBuzz).Log().IsEnabled(LogLevel.Error))
			{
				typeof(WaylandHarfBuzz).Log().Error($"Could not preload HarfBuzz with RTLD_DEEPBIND: libHarfBuzzSharp.so was not found in the native DLL search directories ({search}).");
			}
		}
		catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
		{
			if (typeof(WaylandHarfBuzz).Log().IsEnabled(LogLevel.Error))
			{
				typeof(WaylandHarfBuzz).Log().Error($"Could not preload HarfBuzz with RTLD_DEEPBIND: {error.Message}");
			}
		}
	}

	private static IntPtr DeepBind(string path)
	{
		try
		{
			return dlopen2(path, RTLD_NOW | RTLD_DEEPBIND);
		}
		catch (DllNotFoundException)
		{
			return dlopen1(path, RTLD_NOW | RTLD_DEEPBIND);
		}
	}

	[LibraryImport("libdl.so", EntryPoint = "dlopen", StringMarshalling = StringMarshalling.Utf8)]
	private static partial IntPtr dlopen1(string path, int mode);

	[LibraryImport("libdl.so.2", EntryPoint = "dlopen", StringMarshalling = StringMarshalling.Utf8)]
	private static partial IntPtr dlopen2(string path, int mode);
}
