#nullable enable

using Uno.UI.Composition.Drawing;

namespace Uno.UI.Runtime.Wayland.Rendering;

/// <summary>
/// A swapchain presenting to a wl_surface. Before committing a frame it asks its owner to prepare the surface
/// state that must land in the same commit (buffer scale and viewport), or to hold the frame back while the
/// surface is not configured yet.
/// </summary>
internal interface IWaylandGraphicsContext : ISwapChain
{
	/// <summary>Whether the last <see cref="ISwapChain.Present"/> committed a buffer to the surface.</summary>
	bool Committed { get; }
}

/// <summary>
/// Implemented by contexts owning a GL/EGL context. The backend factory's GPU resources can only be released
/// while that context is current, so teardown makes it current first.
/// </summary>
internal interface IWaylandGpuTeardownContext
{
	void MakeCurrentForTeardown();
}
