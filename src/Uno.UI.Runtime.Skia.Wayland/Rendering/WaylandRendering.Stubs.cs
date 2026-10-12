// Licensed under the MIT License.
#nullable enable
#if WAYLAND_RENDERING_STUBS
using System;
namespace Uno.WinUI.Runtime.Skia.Wayland.Protocols;
// Compile-only contract placeholders. Never enabled in the runtime project.
internal class WlSurface
{
	public IntPtr Handle => default;
	public WlCallback Frame() => new();
	public void SetBufferScale(int scale)
	{
	}
	public void Attach(WlBuffer buffer, int x, int y)
	{
	}
	public void Damage(int x, int y, int width, int height)
	{
	}
	public void Commit()
	{
	}
}
internal class WlCallback : IDisposable
{
	public event EventHandler? Done;
	public void Dispose()
	{
	}
}
internal class WlBuffer : IDisposable
{
	public event EventHandler? Release;
	public void Dispose()
	{
	}
}
internal class WlShm
{
	public WlShmPool CreatePool(int fd, int size) => new();
}
internal class WlShmPool : IDisposable
{
	public WlBuffer CreateBuffer(int offset, int width, int height, int stride, uint format) => new();
	public void Dispose()
	{
	}
}
internal class WpViewport
{
	public void SetDestination(int width, int height)
	{
	}
}
#endif
