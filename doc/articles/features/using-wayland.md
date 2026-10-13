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

The host loads the following libraries at runtime: `libwayland-client.so.0`, `libwayland-cursor.so.0`, `libxkbcommon.so.0`, and, for OpenGL ES rendering, `libwayland-egl.so.1` and `libEGL.so.1`. Install these dependencies using your distribution's package manager. An EGL implementation (such as Mesa) is needed for accelerated rendering.

### Supported protocols

| Protocol | Required | Used for |
|----------|----------|----------|
| `wl_compositor`, `wl_shm`, `xdg_wm_base` | Yes | Windows and software rendering |
| `wl_seat` | No | Pointer, keyboard and touch input |
| `wl_output` | No | Output information, integer scaling and changes as windows move between outputs |
| `wp_viewporter`, `wp_fractional_scale_v1` | No | Fractional scaling. Without them, the integer scale from `wl_surface.preferred_buffer_scale` is used. |
| `zxdg_decoration_manager_v1` | No | Negotiating server-side decorations; client-side decorations are used otherwise |
| `wl_subcompositor` | No | Client-side decoration surfaces |
| `wl_data_device_manager` | No | Clipboard and incoming drag and drop |
| `zwp_text_input_manager_v3` | No | Compositor IME sessions |
| `zxdg_exporter_v2` | No | Exporting a parent window for desktop portals |
| `xdg_activation_v1` | No | Compositor-mediated activation |
| `xdg_toplevel_icon_manager_v1` | No | Window icons |
| `wp_cursor_shape_manager_v1` | No | Pointer cursors drawn by the compositor. Without it, cursors come from the XCursor theme. |

### Window management and title bars

The host supports multiple windows, resizing, minimize/maximize/restore, full-screen presenters and window titles. The compositor controls placement and activation: apps cannot choose a global screen position or assume that an activation request will receive focus.

When the compositor grants server-side decorations, it draws the window frame. Otherwise, Uno Platform draws client-side decorations with move, resize and caption buttons. `Window.ExtendsContentIntoTitleBar`, `Window.SetTitleBar` and `InputNonClientPointerSource` caption and passthrough regions support custom title bars. Non-client region rectangles use physical pixels; update them when layout or `XamlRoot.RasterizationScale` changes.

### Clipboard, drag and drop, and text input

Clipboard and incoming drag and drop support text, HTML, images and local file lists through the core data-device protocol. Copy/move negotiation follows the operations supported by both applications. Wayland ties clipboard ownership to a focused input serial, so set clipboard content in response to user input in the focused window. A background client cannot take the clipboard arbitrarily.

Keyboard input supports XKB layouts, dead keys and compose sequences. Compositors that advertise `text-input-v3` also provide IME preedit, commit, surrounding text and deletion for `TextBox` and `RichEditBox`. Configure the desktop's input-method service for its Wayland session; without the protocol, keyboard and compose input remain available but compositor IME composition is unavailable.

### Desktop portals and settings

Open, save and folder pickers use the XDG Desktop Portal over the session D-Bus. Install `xdg-desktop-portal` and a backend appropriate to your desktop, and run the app in a session with a working D-Bus connection. When the compositor supports `xdg-foreign-v2`, the picker is parented to the exported Wayland window for the duration of the request. Without it, the portal can show an unparented dialog.

System theme and text scale use the desktop settings portal. Window activation and icons use the compositor's optional activation and toplevel-icon protocols.

### Limitations

- `WebView2` and `MediaPlayerElement` embed X11 windows, so they are not supported by the Wayland host.
- Input uses the first seat advertised by the compositor. Output tracking follows the outputs entered by the window; the compositor determines its scale.
- Wayland does not expose global window coordinates. Window placement and focus remain under compositor control.
- Portal dialogs and IME require the corresponding desktop services; a headless compositor alone does not provide them.

## Supported features by platform

| Feature | Windows | Android | iOS | Web (WASM) | macOS | Linux (Skia) | Win 7 (Skia) |
|---------|---------|---------|-----|------------|-------|--------------|--------------|
| Wayland host | ✖ | ✖ | ✖ | ✖ | ✖ | ✔ | ✖ |

## See the Wayland host in action

The SamplesApp desktop head registers `UseWayland()` before `UseX11()`. Run it from a Wayland session to use the Wayland host. To force a rendering backend, set the `UNO_WAYLAND_RENDERER` environment variable to `OpenGLES` or `Software`.

Search the SamplesApp for `Window_DesktopScenarios` to exercise resizing, presenters, multiple windows and custom title bars; `Clipboard_DesktopTransfers` for clipboard formats, file portals and drop negotiation; and `TextBox_DesktopIme` for Unicode composition and focus transfer between editors. The existing `TextBox_IME_Composition` page displays composition events.

The CI headless Weston run uses software rendering at scale 1. It checks host selection, window lifecycle and runtime tests. Accelerated EGL presentation, physical input, IME services and portal backends require a desktop session for validation.
