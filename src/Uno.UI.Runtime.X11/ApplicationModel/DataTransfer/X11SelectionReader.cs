// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Uno.UI.Runtime.X11;

internal readonly record struct X11SelectionProperty(IntPtr Type, int Format, byte[] Data);

// A connection belongs to one read and never shares the host's event queue or display lock.
internal interface IX11SelectionConnection : IDisposable
{
	long TickCount { get; }
	IntPtr IncrementalType { get; }
	IntPtr GetOwner(IntPtr selection);
	void Convert(IntPtr selection, IntPtr format, IntPtr timestamp);
	bool TrySelectionNotify(IntPtr selection, IntPtr format, out bool accepted);
	bool TryPropertyNotify();
	X11SelectionProperty ReadProperty();
	void Wait(CancellationToken cancellationToken);
}

internal static class X11SelectionReader
{
	internal static byte[]? Read(IX11SelectionConnection connection, IntPtr selection, IntPtr format, IntPtr timestamp,
		IntPtr owner, CancellationToken cancellationToken)
	{
		var deadline = connection.TickCount + 10_000;
		var idleDeadline = connection.TickCount + 2_000;
		bool CanContinue()
		{
			cancellationToken.ThrowIfCancellationRequested();
			return connection.TickCount < deadline && connection.TickCount < idleDeadline
				&& owner != IntPtr.Zero && connection.GetOwner(selection) == owner;
		}

		if (!CanContinue())
		{
			return null;
		}
		connection.Convert(selection, format, timestamp);
		while (true)
		{
			if (!CanContinue())
			{
				return null;
			}
			if (connection.TrySelectionNotify(selection, format, out var accepted))
			{
				if (!accepted)
				{
					return null;
				}
				break;
			}
			connection.Wait(cancellationToken);
		}

		// Reading with delete=true acknowledges the header exactly once.
		var property = connection.ReadProperty();
		if (property.Type != connection.IncrementalType)
		{
			return CanContinue() && property.Type != IntPtr.Zero && property.Format == 8 ? property.Data : null;
		}

		using var stream = new MemoryStream();
		idleDeadline = connection.TickCount + 2_000;
		while (CanContinue())
		{
			if (!connection.TryPropertyNotify())
			{
				connection.Wait(cancellationToken);
				continue;
			}
			property = connection.ReadProperty();
			if (property.Type == IntPtr.Zero)
			{
				continue;
			}
			if (property.Format != 8 || property.Type == connection.IncrementalType)
			{
				return null;
			}
			if (property.Data.Length == 0)
			{
				return CanContinue() ? stream.ToArray() : null;
			}
			stream.Write(property.Data, 0, property.Data.Length);
			idleDeadline = connection.TickCount + 2_000;
		}
		return null;
	}
}

internal sealed class X11SelectionReadSession(IntPtr owner)
{
	private readonly Dictionary<IntPtr, Task<byte[]?>> _reads = new();
	private IntPtr _timestamp;
	internal IntPtr Owner { get; } = owner;
	internal IntPtr Timestamp
	{
		get => Volatile.Read(ref _timestamp);
		set => Volatile.Write(ref _timestamp, value);
	}

	internal Task<byte[]?> GetBytesAsync(IntPtr format, Func<IntPtr, IntPtr, CancellationToken, byte[]?> read, CancellationToken cancellationToken)
	{
		lock (_reads)
		{
			if (!_reads.TryGetValue(format, out var result))
			{
				var timestamp = Timestamp;
				_reads[format] = result = Task.Run(() => read(Owner, timestamp, cancellationToken), cancellationToken);
			}
			return result.WaitAsync(cancellationToken);
		}
	}
}
