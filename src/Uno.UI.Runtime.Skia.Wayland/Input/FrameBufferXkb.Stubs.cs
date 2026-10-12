// Licensed under the MIT License. See LICENSE in the project root.
#nullable enable
namespace Uno.UI.Runtime.Skia;

// Temporary dependency of the linked FrameBuffer xkb source. Delete after extracting shared bindings.
internal sealed class FramebufferHostBuilder
{
	internal readonly record struct XKBKeymapParams(string? model = null, string? rules = null, string? layout = null, string? variant = null, string? options = null);
}
