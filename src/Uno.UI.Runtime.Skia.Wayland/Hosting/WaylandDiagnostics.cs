// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using SkiaSharp;
using Uno.Foundation.Logging;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// Opt-in trace of the window lifecycle plus PNG dumps of presented buffers, for headless verification.
// UNO_WAYLAND_DEBUG_DIR=<dir> enables it; UNO_WAYLAND_DUMP_FRAMES=<n> limits dumps per window (default 3);
// UNO_WAYLAND_LOG_LEVEL=<level> also routes Uno logs to <dir>/uno.log when the app installed no logger.
internal static class WaylandDiagnostics
{
	private static readonly object _gate = new();
	private static readonly Stopwatch _clock = Stopwatch.StartNew();
	private static readonly string? _directory = Environment.GetEnvironmentVariable("UNO_WAYLAND_DEBUG_DIR") is { Length: > 0 } dir ? dir : null;
	private static readonly int _maxDumps = int.TryParse(Environment.GetEnvironmentVariable("UNO_WAYLAND_DUMP_FRAMES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 3;

	internal static bool IsEnabled => _directory is not null;

	internal static void InstallLoggerIfRequested()
	{
		if (_directory is not null
			&& LoggerFactory.ExternalLoggerFactory is null
			&& Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("UNO_WAYLAND_LOG_LEVEL"), ignoreCase: true, out var level))
		{
			LoggerFactory.ExternalLoggerFactory = new FileLoggerFactory(Path.Combine(_directory, "uno.log"), level);
		}
	}

	internal static void Trace(string message)
	{
		if (typeof(WaylandDiagnostics).Log().IsEnabled(LogLevel.Debug))
		{
			typeof(WaylandDiagnostics).Log().Debug(message);
		}
		if (_directory is null)
		{
			return;
		}
		lock (_gate)
		{
			Directory.CreateDirectory(_directory);
			File.AppendAllText(Path.Combine(_directory, "events.log"), FormattableString.Invariant($"{_clock.Elapsed.TotalMilliseconds,10:F1} [{Environment.CurrentManagedThreadId,3}] {message}\n"));
		}
	}

	internal static void DumpFrame(int windowId, int frame, SKSurface surface)
	{
		if (_directory is null || frame > _maxDumps)
		{
			return;
		}
		using var image = surface.Snapshot();
		using var data = image.Encode(SKEncodedImageFormat.Png, 100);
		var path = Path.Combine(_directory, FormattableString.Invariant($"window{windowId}-frame{frame}.png"));
		lock (_gate)
		{
			Directory.CreateDirectory(_directory);
			using var stream = File.Create(path);
			data.SaveTo(stream);
		}
		Trace(FormattableString.Invariant($"window{windowId}: dumped {image.Width}x{image.Height} frame {frame} to {path}"));
	}

	private sealed class FileLoggerFactory(string path, LogLevel level) : IExternalLoggerFactory
	{
		public IExternalLogger CreateLogger(string categoryName) => new FileLogger(path, level, categoryName);
	}

	private sealed class FileLogger(string path, LogLevel level, string category) : IExternalLogger
	{
		public LogLevel LogLevel => level;

		public void Log(LogLevel logLevel, string? message, Exception? exception = null)
		{
			if (logLevel < level)
			{
				return;
			}
			lock (_gate)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.AppendAllText(path, FormattableString.Invariant($"{_clock.Elapsed.TotalMilliseconds,10:F1} {logLevel} {category}: {message}{(exception is null ? "" : "\n" + exception)}\n"));
			}
		}
	}
}
