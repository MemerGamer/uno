// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Globalization;
using System.IO;
using Uno.Foundation.Logging;

namespace Uno.WinUI.Runtime.Skia.X11;

// XDND trace for both directions, to Uno's trace log and, when UNO_X11_DND_LOG names a file, appended to that file
// so a drag can be traced in an app that does not route Uno's logs anywhere.
internal static class X11DragDiagnostics
{
	private static readonly object _gate = new();
	private static readonly string? _path = Environment.GetEnvironmentVariable("UNO_X11_DND_LOG") is { Length: > 0 } path ? path : null;

	internal static bool IsEnabled => _path is not null || typeof(X11DragDiagnostics).Log().IsEnabled(LogLevel.Trace);

	internal static void Trace(string message)
	{
		if (typeof(X11DragDiagnostics).Log().IsEnabled(LogLevel.Trace))
		{
			typeof(X11DragDiagnostics).Log().Trace(message);
		}
		if (_path is { } path)
		{
			try
			{
				lock (_gate)
				{
					File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine);
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}
}
