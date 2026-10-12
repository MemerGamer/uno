#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Uno.WinUI.Runtime.Skia.Wayland.Native;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland;

public sealed unsafe class WaylandDisplay : IDisposable
{
	private readonly Dictionary<nint, WaylandProxy> _proxies = new();
	private readonly Dictionary<uint, WaylandGlobal> _globals = new();
	private nint _display;
	private Thread? _thread;
	private int _wakeRead = -1;
	private int _wakeWrite = -1;
	private bool _stopping;
	private bool _disposing;
	private Exception? _callbackError;
	public object SyncRoot { get; } = new();
	public WlRegistry Registry { get; private set; } = null!;
	public int FileDescriptor { get; }
	public bool IsDisconnected => _display == 0;
	// Native wl_display pointer for libwayland-egl and libwayland-cursor consumers.
	internal nint NativeHandle => _display;
	public Exception? DispatchError { get; private set; }
	public IReadOnlyList<WaylandGlobal> Globals
	{
		get
		{
			lock (SyncRoot)
			{
				return _globals.Values.OrderBy(global => global.Name).ToArray();
			}
		}
	}

	private WaylandDisplay(nint display)
	{
		_display = display;
		FileDescriptor = WaylandNative.wl_display_get_fd(display);
	}

	public static WaylandDisplay Connect(string? name = null)
	{
		var handle = WaylandNative.wl_display_connect(name);
		if (handle == 0)
		{
			throw new IOException("Unable to connect to the Wayland display. Check WAYLAND_DISPLAY and XDG_RUNTIME_DIR.");
		}
		return Initialize(handle);
	}

	/// <summary>Connects using a connected Unix socket, transferring ownership of the descriptor to libwayland.</summary>
	public static WaylandDisplay ConnectToFileDescriptor(int fd)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(fd);
		var handle = WaylandNative.wl_display_connect_to_fd(fd);
		if (handle == 0)
		{
			throw new IOException("Unable to initialize the Wayland connection.");
		}
		return Initialize(handle);
	}

	private static WaylandDisplay Initialize(nint handle)
	{
		var display = new WaylandDisplay(handle);
		try
		{
			using var args = new RequestArguments(1);
			var registry = WaylandNative.wl_proxy_marshal_array_flags(handle, 1, ProtocolMetadata.Get(WlRegistry.ProtocolName), 1, 0, args.Pointer);
			display.Registry = new WlRegistry(display, registry);
			display.Registry.Global += (_, evt) => display._globals[evt.Name] = new(evt.Name, evt.Interface, evt.Version);
			display.Registry.GlobalRemove += (_, evt) => display._globals.Remove(evt.Name);
			display.Roundtrip();
			return display;
		}
		catch
		{
			display.Dispose();
			throw;
		}
	}

	public WaylandGlobal? GetGlobal(string interfaceName)
	{
		lock (SyncRoot)
		{
			return _globals.Values.FirstOrDefault(global => global.Interface == interfaceName);
		}
	}

	public WaylandProxy Bind(string interfaceName, uint maximumVersion)
	{
		lock (SyncRoot)
		{
			var global = GetGlobal(interfaceName) ?? throw new NotSupportedException($"The compositor does not advertise {interfaceName}.");
			return Registry.Bind(global.Name, interfaceName, Math.Min(maximumVersion, global.Version));
		}
	}

	public WlCallback Sync()
	{
		lock (SyncRoot)
		{
			ThrowIfUnavailable();
			using var args = new RequestArguments(1);
			var handle = WaylandNative.wl_proxy_marshal_array_flags(_display, 0, ProtocolMetadata.Get(WlCallback.ProtocolName), 1, 0, args.Pointer);
			var callback = new WlCallback(this, handle);
			WakeDispatch();
			return callback;
		}
	}

	public void Roundtrip()
	{
		lock (SyncRoot)
		{
			ThrowIfUnavailable();
			if (_thread is not null)
			{
				throw new InvalidOperationException("Roundtrip is only allowed before starting the dispatch thread.");
			}
			Check(WaylandNative.wl_display_roundtrip(_display));
			ThrowCallbackError();
		}
	}

	public void Flush()
	{
		lock (SyncRoot)
		{
			ThrowIfUnavailable();
			FlushCore();
			WakeDispatch();
		}
	}

	public void StartDispatch()
	{
		lock (SyncRoot)
		{
			ThrowIfUnavailable();
			if (_thread is not null)
			{
				throw new InvalidOperationException("The dispatch loop is already running.");
			}
			int* fds = stackalloc int[2];
			if (Posix.pipe2(fds, 0x800 | 0x80000) != 0)
			{
				throw new IOException($"pipe2 failed: {Marshal.GetLastPInvokeError()}");
			}
			_wakeRead = fds[0];
			_wakeWrite = fds[1];
			_stopping = false;
			_thread = new Thread(DispatchLoop) { IsBackground = true, Name = "Uno Wayland dispatch" };
			_thread.Start();
		}
	}

	public void StopDispatch()
	{
		Thread? thread;
		lock (SyncRoot)
		{
			thread = _thread;
			if (thread == Thread.CurrentThread)
			{
				throw new InvalidOperationException("Stop or dispose the display outside its event callbacks.");
			}
			_stopping = true;
			WakeDispatch();
		}
		thread?.Join();
		lock (SyncRoot)
		{
			if (_thread != thread)
			{
				return;
			}
			if (_wakeRead >= 0)
			{
				Posix.close(_wakeRead);
				Posix.close(_wakeWrite);
				_wakeRead = _wakeWrite = -1;
			}
			_thread = null;
		}
	}

	private void DispatchLoop()
	{
		var prepared = false;
		try
		{
			Posix.PollFd* fds = stackalloc Posix.PollFd[2];
			byte* bytes = stackalloc byte[64];
			while (true)
			{
				lock (SyncRoot)
				{
					if (_stopping)
					{
						return;
					}
					while (WaylandNative.wl_display_prepare_read(_display) != 0)
					{
						Check(WaylandNative.wl_display_dispatch_pending(_display));
						ThrowCallbackError();
					}
					prepared = true;
					var writable = FlushCore();
					fds[0] = new() { Fd = FileDescriptor, Events = (short)(1 | (writable ? 4 : 0)) };
					fds[1] = new() { Fd = _wakeRead, Events = 1 };
				}
				var result = Posix.poll(fds, 2, -1);
				var error = Marshal.GetLastPInvokeError();
				lock (SyncRoot)
				{
					if (result > 0 && (fds[0].Revents & 1) != 0)
					{
						prepared = false;
						Check(WaylandNative.wl_display_read_events(_display));
					}
					else
					{
						WaylandNative.wl_display_cancel_read(_display);
						prepared = false;
					}
					if (result < 0 && error != 4)
					{
						throw new IOException($"Wayland poll failed: {error}");
					}
					if ((fds[1].Revents & 1) != 0)
					{
						while (Posix.read(_wakeRead, bytes, 64) > 0) { }
					}
					if ((fds[0].Revents & (8 | 16 | 32)) != 0)
					{
						throw new IOException("The Wayland connection closed.");
					}
					Check(WaylandNative.wl_display_dispatch_pending(_display));
					ThrowCallbackError();
				}
			}
		}
		catch (Exception error)
		{
			lock (SyncRoot)
			{
				DispatchError = error;
			}
		}
		finally
		{
			if (prepared)
			{
				WaylandNative.wl_display_cancel_read(_display);
			}
		}
	}

	private bool FlushCore()
	{
		if (WaylandNative.wl_display_flush(_display) >= 0)
		{
			return false;
		}
		if (Marshal.GetLastPInvokeError() == 11)
		{
			return true;
		}
		Check(-1);
		return false;
	}

	internal void WakeDispatch()
	{
		if (_wakeWrite >= 0)
		{
			byte value = 1;
			Posix.write(_wakeWrite, &value, 1);
		}
	}
	internal void Register(WaylandProxy proxy)
	{
		lock (SyncRoot)
		{
			_proxies.Add(proxy.Handle, proxy);
		}
	}
	internal void Unregister(nint handle) => _proxies.Remove(handle);
	internal WaylandProxy? FindProxy(nint handle) => handle == 0 ? null : _proxies.GetValueOrDefault(handle);
	internal void CaptureCallbackError(Exception error) => _callbackError ??= error;
	private void ThrowCallbackError()
	{
		if (_callbackError is { } error)
		{
			_callbackError = null;
			throw new InvalidOperationException("A Wayland event handler failed.", error);
		}
	}
	private void Check(int result)
	{
		if (result < 0)
		{
			throw new IOException($"Wayland connection error: {WaylandNative.wl_display_get_error(_display)}");
		}
	}
	internal void ThrowIfUnavailable()
	{
		ObjectDisposedException.ThrowIf(IsDisconnected || _disposing, this);
		if (DispatchError is { } error)
		{
			throw new IOException("The Wayland dispatch loop failed.", error);
		}
	}
	public void Dispose()
	{
		lock (SyncRoot)
		{
			if (_thread == Thread.CurrentThread)
			{
				throw new InvalidOperationException("Dispose the display outside its event callbacks.");
			}
			_disposing = true;
		}
		StopDispatch();
		lock (SyncRoot)
		{
			if (_display != 0)
			{
				foreach (var proxy in _proxies.Values.ToArray())
				{
					proxy.DisposeLocal();
				}
				WaylandNative.wl_display_disconnect(_display);
				_display = 0;
			}
		}
	}
}

public sealed record WaylandGlobal(uint Name, string Interface, uint Version);
