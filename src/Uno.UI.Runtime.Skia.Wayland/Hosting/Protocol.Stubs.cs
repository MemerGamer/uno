// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;

// Integration-only contract sketches. Delete when proto supplies the real binding types.
namespace Uno.WinUI.Runtime.Skia.Wayland.Native
{
	internal sealed class WaylandDisplay : IDisposable
	{
		internal static WaylandDisplay Connect() => throw new PlatformNotSupportedException("Wayland bindings are not integrated.");
		internal IReadOnlyDictionary<string, uint> Registry { get; } = new Dictionary<string, uint>();
		internal void Roundtrip() => throw new PlatformNotSupportedException("Wayland bindings are not integrated.");
		public void Dispose() { }
	}
}

namespace Uno.WinUI.Runtime.Skia.Wayland.Protocols
{
	internal sealed class WlSurface : IDisposable
	{
		internal void Commit() => throw new PlatformNotSupportedException("Wayland bindings are not integrated.");
		public void Dispose() { }
	}

	internal sealed class XdgSurface : IDisposable
	{
		public void Dispose() { }
	}

	internal sealed class XdgToplevel : IDisposable
	{
		internal void SetTitle(string title) => throw new PlatformNotSupportedException("Wayland bindings are not integrated.");
		public void Dispose() { }
	}
}
