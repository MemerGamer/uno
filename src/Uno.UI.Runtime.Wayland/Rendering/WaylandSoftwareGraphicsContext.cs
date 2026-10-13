#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Uno.UI.Composition.Drawing;
using Uno.UI.Runtime.Wayland.Protocols;

namespace Uno.UI.Runtime.Wayland.Rendering;

/// <summary>
/// Software graphics context: the backend renders into a persistent CPU buffer, which <see cref="Present"/> copies
/// into a wl_shm buffer the compositor is not holding. All members run on the window's render thread.
/// </summary>
internal sealed class WaylandSoftwareGraphicsContext : IWaylandGraphicsContext
{
	private const uint ShmFormatArgb8888 = 0;

	private readonly WlSurface _surface;
	private readonly WlShm _shm;
	private readonly Action<Action> _postToOwner;
	private readonly Func<bool> _prepareCommit;
	private readonly List<Buffer> _buffers = new();
	private SoftwareRenderTarget? _target;
	private bool _frameAcquired;
	private bool _disposed;

	// Buffer release events arrive on the dispatch thread and are posted to the render thread.
	public WaylandSoftwareGraphicsContext(WlSurface surface, WlShm shm, Action<Action> postToOwner, Func<bool> prepareCommit)
	{
		_surface = surface;
		_shm = shm;
		_postToOwner = postToOwner;
		_prepareCommit = prepareCommit;
	}

	public event EventHandler? BufferAvailable;

	public GraphicsContextKind Kind => GraphicsContextKind.Software;

	// The render target is a persistent buffer, so the compositor can repaint only the damaged region.
	public bool PreservesContents => true;

	public bool Committed { get; private set; }

	public IRenderTarget AcquireRenderTarget(int width, int height)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		width = Math.Max(1, width);
		height = Math.Max(1, height);
		if (_target is null || _target.Width != width || _target.Height != height)
		{
			_target?.Free();
			_target = new SoftwareRenderTarget(width, height);
		}
		_frameAcquired = true;
		return _target;
	}

	public void Present()
	{
		Committed = false;
		if (!_frameAcquired || _target is not { } target || _disposed)
		{
			return;
		}
		_frameAcquired = false;
		if (!_prepareCommit() || GetAvailableBuffer(target.Width, target.Height) is not { } buffer)
		{
			return;
		}
		unsafe
		{
			NativeMemory.Copy((void*)target.Pixels, (void*)buffer.Memory, (nuint)buffer.Length);
		}
		buffer.Busy = true;
		_surface.Attach(buffer.Proxy, 0, 0);
		if (_surface.Version >= 4)
		{
			_surface.DamageBuffer(0, 0, target.Width, target.Height);
		}
		else
		{
			_surface.Damage(0, 0, int.MaxValue, int.MaxValue);
		}
		_surface.Commit();
		Committed = true;
	}

	private Buffer? GetAvailableBuffer(int width, int height)
	{
		Buffer? available = null;
		foreach (var buffer in _buffers.ToArray())
		{
			if (buffer.Width != width || buffer.Height != height)
			{
				buffer.Retired = true;
				if (!buffer.Busy)
				{
					Remove(buffer);
				}
			}
			else if (!buffer.Retired && !buffer.Busy)
			{
				available = buffer;
			}
		}
		if (available is null)
		{
			// Bound memory even when a compositor retains old-size buffers across resizes.
			if (_buffers.Count >= 2)
			{
				return null;
			}
			available = new Buffer(_shm, width, height, buffer => _postToOwner(() => OnRelease(buffer)));
			_buffers.Add(available);
		}
		return available;
	}

	private void OnRelease(Buffer buffer)
	{
		if (!_buffers.Contains(buffer))
		{
			return;
		}
		buffer.Busy = false;
		if (buffer.Retired || _disposed)
		{
			Remove(buffer);
		}
		if (!_disposed)
		{
			BufferAvailable?.Invoke(this, EventArgs.Empty);
		}
	}

	private void Remove(Buffer buffer)
	{
		_buffers.Remove(buffer);
		buffer.Dispose();
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		foreach (var buffer in _buffers.ToArray())
		{
			Remove(buffer);
		}
		_target?.Free();
		_target = null;
	}

	private sealed class SoftwareRenderTarget : ISoftwareRenderTarget
	{
		public SoftwareRenderTarget(int width, int height)
		{
			Width = width;
			Height = height;
			unsafe
			{
				Pixels = (nint)NativeMemory.AllocZeroed((nuint)checked(width * 4 * height));
			}
		}

		public nint Pixels { get; private set; }
		public int RowBytes => Width * 4;
		public int Width { get; }
		public int Height { get; }
		public GraphicsColorFormat ColorFormat => GraphicsColorFormat.Bgra8888;

		// The backend disposes the targets it is handed per frame; the context owns the memory.
		public void Dispose() { }

		public unsafe void Free()
		{
			NativeMemory.Free((void*)Pixels);
			Pixels = 0;
		}
	}

	private sealed class Buffer : IDisposable
	{
		private readonly Action<Buffer> _released;

		public Buffer(WlShm shm, int width, int height, Action<Buffer> released)
		{
			Width = width;
			Height = height;
			_released = released;
			var stride = checked(width * 4);
			Length = checked(stride * height);
			var fd = WaylandRenderNative.memfd_create("uno-wayland", 1 /* MFD_CLOEXEC */);
			if (fd < 0)
			{
				throw new InvalidOperationException("memfd_create failed.");
			}
			WlBuffer? proxy = null;
			try
			{
				if (WaylandRenderNative.ftruncate(fd, Length) != 0)
				{
					throw new InvalidOperationException("ftruncate failed.");
				}
				Memory = WaylandRenderNative.mmap(IntPtr.Zero, (nuint)Length, 3 /* PROT_READ | PROT_WRITE */, 1 /* MAP_SHARED */, fd, 0);
				if (Memory == new IntPtr(-1))
				{
					Memory = IntPtr.Zero;
					throw new InvalidOperationException("mmap failed.");
				}
				using var pool = shm.CreatePool(fd, Length);
				proxy = pool.CreateBuffer(0, width, height, stride, ShmFormatArgb8888);
				Proxy = proxy;
				Proxy.Release += OnRelease;
			}
			catch
			{
				proxy?.Dispose();
				if (Memory != IntPtr.Zero)
				{
					WaylandRenderNative.munmap(Memory, (nuint)Length);
				}
				throw;
			}
			finally
			{
				WaylandRenderNative.close(fd);
			}
		}

		public int Width { get; }
		public int Height { get; }
		public int Length { get; }
		public IntPtr Memory { get; }
		public bool Busy { get; set; }
		public bool Retired { get; set; }
		public WlBuffer Proxy { get; }

		private void OnRelease(object? sender, EventArgs args) => _released(this);

		public void Dispose()
		{
			Proxy.Release -= OnRelease;
			Proxy.Dispose();
			WaylandRenderNative.munmap(Memory, (nuint)Length);
		}
	}
}
