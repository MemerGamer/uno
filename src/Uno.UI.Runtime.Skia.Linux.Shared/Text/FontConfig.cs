#nullable enable

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Uno.WinUI.Runtime.Skia.Linux;

internal static partial class FontConfig
{
	private const string Library = "libfontconfig.so.1";

	internal static LinuxFontRenderingSettings ReadDefault()
	{
		try
		{
			var config = FcConfigGetCurrent(); // Borrowed: never destroy the config or call FcFini.
			if (config == 0)
			{
				return LinuxFontRenderingSettings.Unavailable;
			}
			var pattern = FcPatternCreate();
			if (pattern == 0)
			{
				return LinuxFontRenderingSettings.Unavailable;
			}
			try
			{
				if (FcConfigSubstitute(config, pattern, 0 /* FcMatchPattern */) == 0)
				{
					return LinuxFontRenderingSettings.Unavailable;
				}
				FcDefaultSubstitute(pattern);
				// Default-pattern rules only; per-face/size substitutions are deliberately not matched.
				return LinuxFontRenderingSettings.FromPattern(GetBool(pattern, "antialias"), GetBool(pattern, "hinting"),
					GetInteger(pattern, "hintstyle"), GetInteger(pattern, "rgba"), GetInteger(pattern, "lcdfilter"), GetBool(pattern, "autohint"));
			}
			finally
			{
				FcPatternDestroy(pattern);
			}
		}
		catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
		{
			return LinuxFontRenderingSettings.Unavailable;
		}
	}

	private static bool? GetBool(nint pattern, string name)
		=> FcPatternGetBool(pattern, name, 0, out var value) == 0 ? value != 0 : null;

	private static int? GetInteger(nint pattern, string name)
		=> FcPatternGetInteger(pattern, name, 0, out var value) == 0 ? value : null;

	[LibraryImport(Library)]
	[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	private static partial nint FcConfigGetCurrent();

	[LibraryImport(Library)]
	[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	private static partial nint FcPatternCreate();

	[LibraryImport(Library)]
	[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	private static partial void FcPatternDestroy(nint pattern);

	[LibraryImport(Library)]
	[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	private static partial int FcConfigSubstitute(nint config, nint pattern, int kind);

	[LibraryImport(Library)]
	[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	private static partial void FcDefaultSubstitute(nint pattern);

	[LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
	[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	private static partial int FcPatternGetBool(nint pattern, string name, int index, out int value);

	[LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
	[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	private static partial int FcPatternGetInteger(nint pattern, string name, int index, out int value);
}
