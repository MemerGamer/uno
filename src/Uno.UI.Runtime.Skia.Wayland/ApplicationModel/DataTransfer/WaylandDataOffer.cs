// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Uno.Foundation.Logging;
using Uno.WinUI.Runtime.Skia.Wayland.Native;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// A selection or drag-and-drop offer. Data is received through a pipe on a worker thread, once per MIME type.
internal sealed class WaylandDataOffer
{
	// A source that stops writing for this long is considered gone.
	private static readonly TimeSpan _readTimeout = TimeSpan.FromSeconds(5);

	private readonly WlDataOffer _offer;
	private readonly List<string> _mimeTypes = new();
	private readonly Dictionary<string, Task<byte[]?>> _reads = new();
	private bool _destroyed;

	// Created by wl_data_device.data_offer on the dispatch thread; the offer events that follow are subscribed here.
	internal WaylandDataOffer(WlDataOffer offer)
	{
		_offer = offer;
		offer.Offer += (_, e) =>
		{
			lock (_mimeTypes)
			{
				_mimeTypes.Add(e.MimeType);
			}
		};
		offer.SourceActions += (_, e) => SourceActions = e.SourceActions;
	}

	internal uint Version => _offer.Version;

	// wl_data_device_manager.dnd_action bits offered by a drag source (wl_data_offer v3).
	internal uint SourceActions { get; private set; }

	internal IReadOnlyList<string> MimeTypes
	{
		get
		{
			lock (_mimeTypes)
			{
				return _mimeTypes.ToArray();
			}
		}
	}

	/// <summary>Receives the offered data for <paramref name="mimeType"/>; null if the offer is gone or the source fails to send it.</summary>
	internal Task<byte[]?> ReadAsync(string mimeType)
	{
		lock (_reads)
		{
			if (!_reads.TryGetValue(mimeType, out var read))
			{
				_reads[mimeType] = read = Task.Run(() => Read(mimeType));
			}
			return read;
		}
	}

	/// <summary>Completes once every read started so far has finished.</summary>
	internal Task WhenReadsCompleted()
	{
		lock (_reads)
		{
			return Task.WhenAll(_reads.Values.ToArray());
		}
	}

	private byte[]? Read(string mimeType)
	{
		var (read, write) = WaylandPipe.Create();
		var destroyed = false;
		lock (_offer.Display.SyncRoot)
		{
			destroyed = _destroyed;
			if (!destroyed)
			{
				// libwayland duplicates the descriptor while marshalling, so our copy is closed right after.
				_offer.Receive(mimeType, write);
			}
		}
		Posix.close(write);
		if (destroyed)
		{
			Posix.close(read);
			return null;
		}
		var data = WaylandPipe.ReadToEnd(read, _readTimeout);
		if (data is null && this.Log().IsEnabled(LogLevel.Warning))
		{
			this.Log().Warn($"The Wayland data source did not send {mimeType} in time.");
		}
		return data;
	}

	// Drag-and-drop feedback: the accepted MIME type (null rejects the drop) and the accepted actions.
	internal void Accept(uint serial, string? mimeType, uint actions, uint preferredAction)
	{
		lock (_offer.Display.SyncRoot)
		{
			if (_destroyed)
			{
				return;
			}
			_offer.Accept(serial, mimeType);
			if (_offer.Version >= 3)
			{
				_offer.SetActions(actions, preferredAction);
			}
		}
	}

	// Ends a successful drop: the source may now delete moved data.
	internal void FinishAndDestroy()
	{
		lock (_offer.Display.SyncRoot)
		{
			if (!_destroyed && _offer.Version >= 3)
			{
				_offer.Finish();
			}
			Destroy();
		}
	}

	internal void Destroy()
	{
		lock (_offer.Display.SyncRoot)
		{
			if (!_destroyed)
			{
				_destroyed = true;
				_offer.Destroy();
			}
		}
	}
}
