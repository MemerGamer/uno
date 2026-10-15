#nullable enable

using System;
using System.IO;

namespace Uno.WinUI.Runtime.Skia.Linux;

internal static class KdeTextGeometry
{
	internal static bool IsDesktop(string name)
		=> Array.Exists((Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty).Split(':'),
			part => part.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));

	internal static LinuxTextGeometry Read()
	{
		try
		{
			var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
			if (string.IsNullOrEmpty(configHome))
			{
				var home = Environment.GetEnvironmentVariable("HOME");
				if (string.IsNullOrEmpty(home))
				{
					return LinuxTextGeometry.Unknown;
				}
				configHome = Path.Combine(home, ".config");
			}
			return Parse(File.ReadAllText(Path.Combine(configHome, "kdeglobals")));
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return LinuxTextGeometry.Unknown;
		}
	}

	internal static LinuxTextGeometry Parse(string contents)
	{
		using var reader = new StringReader(contents);
		var general = false;
		var result = LinuxTextGeometry.Unknown;
		while (reader.ReadLine() is { } line)
		{
			line = line.Trim();
			if (line.StartsWith('#') || line.StartsWith(';'))
			{
				continue;
			}
			if (line.StartsWith('['))
			{
				general = line == "[General]";
			}
			else if (general)
			{
				var separator = line.IndexOf('=');
				if (separator >= 0 && line[..separator].Trim() == "XftSubPixel")
				{
					result = LinuxTextGeometry.Parse(line[(separator + 1)..], LinuxTextGeometrySource.Kde) ?? LinuxTextGeometry.Unknown;
				}
			}
		}
		return result;
	}
}
