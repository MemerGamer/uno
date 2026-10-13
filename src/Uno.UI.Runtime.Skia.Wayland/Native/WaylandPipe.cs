// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Uno.WinUI.Runtime.Skia.Wayland.Native;

// Blocking pipe transfers for wl_data_offer.receive and wl_data_source.send. Callers run these on worker
// threads, never on the dispatch or UI thread. Both directions close the descriptor they are given.
internal static unsafe class WaylandPipe
{
	private const int OCloexec = 0x80000;
	private const short PollIn = 1;
	private const short PollOut = 4;
	private const int EIntr = 4;
	private const int EAgain = 11;

	internal static (int Read, int Write) Create()
	{
		int* fds = stackalloc int[2];
		if (Posix.pipe2(fds, OCloexec) != 0)
		{
			throw new IOException($"pipe2 failed: {Marshal.GetLastPInvokeError()}");
		}
		return (fds[0], fds[1]);
	}

	/// <summary>Reads until end of file. Returns null when the writer stays silent for <paramref name="idleTimeout"/>.</summary>
	internal static byte[]? ReadToEnd(int fd, TimeSpan idleTimeout)
	{
		try
		{
			using var buffer = new MemoryStream();
			var chunk = new byte[64 * 1024];
			while (true)
			{
				if (!Wait(fd, PollIn, idleTimeout))
				{
					return null;
				}
				nint count;
				fixed (byte* pointer = chunk)
				{
					count = Posix.read(fd, pointer, (nuint)chunk.Length);
				}
				if (count == 0)
				{
					return buffer.ToArray();
				}
				if (count < 0)
				{
					var error = Marshal.GetLastPInvokeError();
					if (error is EIntr or EAgain)
					{
						continue;
					}
					throw new IOException($"read failed: {error}");
				}
				buffer.Write(chunk, 0, (int)count);
			}
		}
		finally
		{
			_ = Posix.close(fd);
		}
	}

	/// <summary>Writes all of <paramref name="data"/>. Returns false when the reader went away or stopped reading.</summary>
	internal static bool WriteAll(int fd, ReadOnlySpan<byte> data, TimeSpan idleTimeout)
	{
		try
		{
			fixed (byte* start = data)
			{
				var written = 0;
				while (written < data.Length)
				{
					if (!Wait(fd, PollOut, idleTimeout))
					{
						return false;
					}
					var count = Posix.write(fd, start + written, (nuint)(data.Length - written));
					if (count < 0)
					{
						var error = Marshal.GetLastPInvokeError();
						if (error is EIntr or EAgain)
						{
							continue;
						}
						// EPIPE: the reader closed its end. The runtime ignores SIGPIPE.
						return false;
					}
					written += (int)count;
				}
			}
			return true;
		}
		finally
		{
			_ = Posix.close(fd);
		}
	}

	private static bool Wait(int fd, short events, TimeSpan timeout)
	{
		var pollFd = new Posix.PollFd { Fd = fd, Events = events };
		while (true)
		{
			var result = Posix.poll(&pollFd, 1, (int)timeout.TotalMilliseconds);
			if (result >= 0)
			{
				// Hang-up and error states are reported by the following read() or write().
				return result > 0;
			}
			if (Marshal.GetLastPInvokeError() != EIntr)
			{
				throw new IOException($"poll failed: {Marshal.GetLastPInvokeError()}");
			}
		}
	}
}
