---
uid: Uno.Skia.Linux.Wayland
---

# Using the Wayland host

Uno Platform can run Skia Desktop apps as native [Wayland](https://wayland.freedesktop.org/) clients, without going through XWayland. A native Wayland window gets the scale the compositor picks for it, including fractional scales such as 125% or 150%, so text and images stay sharp on HiDPI displays where an XWayland window would be rendered at 100% and upscaled.

The Wayland host is opt-in. An app that doesn't register it keeps running through X11, including under XWayland.

## How to use the Wayland host

In the `Platforms/Desktop/Program.cs` file, add `UseWayland()` to the `UnoPlatformHostBuilder`, **before** `UseX11()`:

```csharp
var host = UnoPlatformHostBuilder.Create()
    .App(() => new App())
    .UseWayland()
    .UseX11()
    .UseLinuxFrameBuffer()
    .UseMacOS()
    .UseWin32()
    .Build();

host.Run();
```

The builder evaluates the hosts in the order they were registered. The Wayland host is selected when all of the following are true:

- The app runs on Linux and the `WAYLAND_DISPLAY` environment variable is set.
- The host can connect to the compositor.
- The compositor advertises `wl_compositor`, `wl_shm` and `xdg_wm_base`.

Otherwise, the next registered host (typically X11) is used. To run an app through XWayland in a Wayland session, unset `WAYLAND_DISPLAY` for that app, or don't register `UseWayland()`.

The app id of the window (used by the compositor to match the app's `.desktop` file) is the package name, as `WM_CLASS` is on X11.

### Rendering backend

The host renders with OpenGL ES through EGL when it can create a context for the window, and falls back to software rendering into shared-memory buffers otherwise. You can force one backend, or disable some, from the host builder:

```csharp
    .UseWayland(b => b.ForceRenderingBackend(WaylandRenderingBackend.Software))
```

For more information, see [Rendering Backend Selection](xref:Uno.Skia.Desktop#rendering-backend-selection).

### Native dependencies

The host loads the following libraries at runtime: `libwayland-client.so.0`, `libwayland-cursor.so.0`, `libxkbcommon.so.0`, and, for OpenGL ES rendering, `libwayland-egl.so.1` and `libEGL.so.1`. They are installed by default on desktops that run a Wayland session.

### Supported protocols

| Protocol | Required | Used for |
|----------|----------|----------|
| `wl_compositor`, `wl_shm`, `xdg_wm_base` | Yes | Windows and software rendering |
| `wl_seat` | No | Pointer, keyboard and touch input |
| `wl_output` | No | Screen size and physical size |
| `wp_viewporter`, `wp_fractional_scale_v1` | No | Fractional scaling. Without them, the integer scale from `wl_surface.preferred_buffer_scale` is used. |
| `zxdg_decoration_manager_v1` | No | Server-side window decorations |
| `wp_cursor_shape_manager_v1` | No | Pointer cursors drawn by the compositor. Without it, cursors come from the XCursor theme. |

### Limitations

- The host doesn't draw client-side decorations yet. On compositors that don't support server-side decorations through `xdg-decoration` (for example GNOME Mutter), windows have no title bar and can't be moved or resized with the mouse.
- Clipboard, drag and drop, text input methods (IME), file pickers and system theme detection are not available yet.
- `WebView2` and `MediaPlayerElement` embed X11 windows, so they are not supported by the Wayland host.
- Only the first `wl_seat` and `wl_output` advertised by the compositor are used.

## Supported features by platform

| Feature | Windows | Android | iOS | Web (WASM) | macOS | Linux (Skia) | Win 7 (Skia) |
|---------|---------|---------|-----|------------|-------|--------------|--------------|
| Wayland host | ✖ | ✖ | ✖ | ✖ | ✖ | ✔ | ✖ |

## See the Wayland host in action

The SamplesApp desktop head registers `UseWayland()` before `UseX11()`. Run it from a Wayland session to use the Wayland host. To force a rendering backend, set the `UNO_WAYLAND_RENDERER` environment variable to `OpenGLES` or `Software`.
