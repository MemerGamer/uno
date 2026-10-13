// Licensed under the MIT License.
#nullable enable
using System;
using System.Collections.Generic;
using SkiaSharp;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

namespace Uno.WinUI.Runtime.Skia.Wayland.Rendering;

internal sealed class WaylandSoftwareRenderer : IDisposable
{
	private readonly WlSurface _surface;
	private readonly WlShm _shm;
	private readonly List<Buffer> _buffers = new();
	private readonly Action<Action> _postToOwner;
	private bool _disposed;
	public event EventHandler? BufferAvailable;
	// Release events arrive on the dispatch thread and are posted to the render owner.
	public WaylandSoftwareRenderer(WlSurface surface, WlShm shm, Action<Action> postToOwner)
	{
		_surface = surface;
		_shm = shm;
		_postToOwner = postToOwner;
	}
	public bool Render(int width, int height, Func<SKSurface, bool> draw)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (width <= 0 || height <= 0)
		{
			return false;
		}
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
				return false;
			}
			available = new Buffer(_shm, width, height, buffer => _postToOwner(() => OnRelease(buffer)));
			_buffers.Add(available);
		}
		if (!draw(available.Surface))
		{
			return false;
		}
		available.Surface.Canvas.Flush();
		available.Busy = true;
		_surface.Attach(available.Proxy, 0, 0);
		if (_surface.Version >= 4)
		{
			_surface.DamageBuffer(0, 0, width, height);
		}
		else
		{
			_surface.Damage(0, 0, int.MaxValue, int.MaxValue);
		}
		_surface.Commit();
		return true;
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
	}
	internal sealed class Buffer : IDisposable
	{
		private readonly IntPtr _memory;
		private readonly nuint _length;
		private readonly Action<Buffer> _released;
		public int Width { get; }
		public int Height { get; }
		public bool Busy { get; set; }
		public bool Retired { get; set; }
		public WlBuffer Proxy { get; }
		public SKSurface Surface { get; }
		public Buffer(WlShm shm, int width, int height, Action<Buffer> released)
		{
			Width = width;
			Height = height;
			_released = released;
			var stride = checked(width * 4);
			var length = checked(stride * height);
			_length = (nuint)length;
			var fd = WaylandRenderNative.memfd_create("uno-wayland", 1);
			if (fd < 0)
			{
				throw new InvalidOperationException("memfd_create failed.");
			}
			WlBuffer? proxy = null;
			try
			{
				if (WaylandRenderNative.ftruncate(fd, length) != 0)
				{
					throw new InvalidOperationException("ftruncate failed.");
				}
				_memory = WaylandRenderNative.mmap(IntPtr.Zero, _length, 3, 1, fd, 0);
				if (_memory == new IntPtr(-1))
				{
					throw new InvalidOperationException("mmap failed.");
				}
				using var pool = shm.CreatePool(fd, length);
				proxy = pool.CreateBuffer(0, width, height, stride, 0); // WL_SHM_FORMAT_ARGB8888
				Surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), _memory, stride)
					?? throw new InvalidOperationException("Skia raster surface creation failed.");
				Proxy = proxy;
				Proxy.Release += OnRelease;
			}
			catch
			{
				proxy?.Dispose();
				if (_memory != IntPtr.Zero && _memory != new IntPtr(-1))
				{
					WaylandRenderNative.munmap(_memory, _length);
				}
				throw;
			}
			finally
			{
				WaylandRenderNative.close(fd);
			}
		}
		private void OnRelease(object? sender, EventArgs args) => _released(this);
		public void Dispose()
		{
			Proxy.Release -= OnRelease;
			Surface.Dispose();
			Proxy.Dispose();
			WaylandRenderNative.munmap(_memory, _length);
		}
	}
}
