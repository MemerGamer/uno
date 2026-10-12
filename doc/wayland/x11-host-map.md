# X11 host service map → Wayland

Scope: this worktree, fork based on Uno 6.7.135 (`da217df231`). Line numbers refer to the checked-out files, which include fork additions such as Vulkan and D-Bus IME. P0 = first interactive window; P1 = usable file manager; P2 = later parity. The rows below cover every `ApiExtensibility.Register` call in the X11 application host. P0 DPI support includes the sharp fractional-scale result required by this project.

## Extension registrations

| Service | Registration (file:line) | Implementation (file:line) | Wayland counterpart | Priority |
|---|---|---|---|---|
| Core application / exit | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:90` | `src/Uno.UI.Runtime.Skia.X11/ApplicationModel/Core/X11CoreApplicationExtension.cs:8` | Managed shutdown coordinator; wake the dispatch fd loop and finish the UI loop | P0 |
| Application view | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:91` | `src/Uno.UI.Runtime.Skia.X11/UI/ViewManagement/X11ApplicationViewExtension.cs:6` | xdg_toplevel title, fullscreen, min/max size; compositor owns placement | P0 |
| Display information | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:92` | `src/Uno.UI.Runtime.Skia.X11/Graphics/Display/X11DisplayInformationExtension.cs:260` | Per-surface fractional scale + viewporter, preferred_buffer_scale or entered wl_output scale; LogicalDpi = 96 × scale | P0 |
| Pointer input source | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:94` | `src/Uno.UI.Runtime.Skia.X11/Devices/Input/X11PointerInputSource.cs:12` | wl_seat / wl_pointer; preserve event frame ordering, button state and serials | P0 |
| Keyboard input source | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:95` | `src/Uno.UI.Runtime.Skia.X11/Devices/Input/X11KeyboardInputSource.cs:9` | wl_keyboard keymap fd + libxkbcommon, repeat_info and compositor modifiers | P0 |
| Native window factory | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:97` | `src/Uno.UI.Runtime.Skia.X11/UI/Xaml/Window/X11NativeWindowFactoryExtension.cs:8` | One wl_surface + xdg_surface + xdg_toplevel per Window; shared display | P0 |
| URI launcher | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:99` | `src/Uno.UWP/System/LinuxLauncherExtension.skia.cs:11` | Reuse LinuxLauncherExtension; portal OpenURI where appropriate | P1 |
| Clipboard | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:101` | `src/Uno.UI.Runtime.Skia.X11/ApplicationModel/DataTransfer/X11ClipboardExtension.cs:1` | wl_data_device_manager selection, MIME offers, pipe fds; optional primary-selection protocol | P1 |
| TextBox IME | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:103` | `src/Uno.UI.Runtime.Skia.X11/UI/Xaml/Controls/TextBox/X11ImeTextBoxExtension.cs:1` | zwp_text_input_manager_v3; compositor-mediated preedit/commit and surrounding text | P1 |
| TextBox notifications | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:104` | `src/Uno.UI.Runtime.Skia.X11/UI/Xaml/Controls/TextBox/X11TextBoxNotificationsProviderSingleton.cs:1` | Managed focus/edit notifications feeding text-input-v3 enable/disable and commit | P1 |
| File open picker | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:106` | `src/Uno.UI.Runtime.Skia.X11/Storage/Pickers/LinuxFilePickerExtension.cs:137` | D-Bus org.freedesktop.portal.FileChooser; xdg-foreign exported parent handle (wayland:handle) | P1 |
| Folder picker | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:107` | `src/Uno.UI.Runtime.Skia.X11/Storage/Pickers/LinuxFilePickerExtension.cs:137` | Same portal adapter with directory option | P1 |
| File save picker | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:108` | `src/Uno.UI.Runtime.Skia.X11/Storage/Pickers/LinuxFileSaverExtension.cs:124` | Same portal adapter with save options and Wayland parent | P1 |
| Native element hosting | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:110` | `src/Uno.UI.Runtime.Skia.X11/Native/X11NativeElementHostingExtension.cs:1` | No XID reparenting; renderer texture integration or cooperating wl_subsurface clients | P2 |
| Drag and drop | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:112` | `src/Uno.UI.Runtime.Skia.X11/ApplicationModel/DataTransfer/DragDrop/X11DragDropExtension.cs:1` | wl_data_device start_drag, enter/motion/drop, wl_data_offer actions and text/uri-list | P1 |
| Native OpenGL wrapper | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:114` | `src/Uno.UI.Runtime.Skia.X11/Graphics/X11NativeOpenGLWrapper.cs:1` | EGL Wayland platform / libwayland-egl; renderer-owned context and surfaces | P2 |
| System theme | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:116` | `src/Uno.UI.Runtime.Skia.X11/Helpers/Theming/LinuxSystemThemeHelper.cs:54` | Extract shared portal Settings adapter; observe color-scheme over D-Bus | P1 |
| Text scale factor | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:117` | `src/Uno.UI.Runtime.Skia.X11/UI/ViewManagement/X11TextScaleFactorExtension.cs:74` | Shared portal Settings adapter; independent of rasterization scale | P1 |
| Optional media presenter | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:126` | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:126` | Explicit optional Wayland media registration; texture output, no reflection or X11 window embedding | P2 |
| Optional media playback | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:127` | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs:127` | Explicit optional shared media backend; keep separate from window transport | P2 |

The two media registrations are conditional on reflected type discovery and `libvlc.so`. They must not be copied into the AOT-safe Wayland host. Pointer and keyboard registrations are scoped to `IXamlRootHost`; picker registrations are scoped to their picker type. The X11 host also sets `CompositionTarget.FrameRenderingOptions = (true, true)` at `Hosting/X11ApplicationHost.cs:138` (under the same project prefix).

## Host, window and non-registration infrastructure

| Capability | X11 source (file:line; under src/Uno.UI.Runtime.Skia.X11/) | Wayland counterpart | Priority |
|---|---|---|---|
| UseX11 builder entry | `Builder/HostBuilder.cs:13`, `Builder/HostBuilder.cs:19` | `UseWayland()` candidate before `UseX11()` fallback | P0 |
| Host selection and rendering option | `Builder/X11HostBuilder.cs:58`, `Builder/X11HostBuilder.cs:30` | Probe real connection and required globals; independent Wayland renderer options | P0 |
| UI dispatcher | `Hosting/X11ApplicationHost.cs:268` | Uno UI event loop plus separate wakeable Wayland dispatch thread | P0 |
| IXamlRootHost and root map | `Hosting/X11XamlRootHost.cs:25`, `Hosting/X11XamlRootHost.cs:126`, `Hosting/X11XamlRootHost.cs:662` | Managed host per XamlRoot; register/unregister exclusively on UI thread | P0 |
| Event fd loop | `Hosting/X11XamlRootHost.x11events.cs:91` | libwayland prepare_read / poll / read_events / dispatch_pending, wake fd for commands | P0 |
| Title, show, close cancellation | `UI/Xaml/Window/X11WindowWrapper.cs:41`, `UI/Xaml/Window/X11WindowWrapper.cs:91`, `UI/Xaml/Window/X11WindowWrapper.cs:107` | xdg_toplevel.set_title, initial empty commit/configure/ack, close forwarded through RaiseClosing | P0 |
| Title bar / decorations | `Hosting/X11XamlRootHost.cs:175`, `UI/Xaml/Window/X11WindowWrapper.cs:85` | xdg-decoration-v1 negotiation; managed CSD when SSD absent or CSD selected | P0 |
| App identity / icon | `Hosting/X11XamlRootHost.cs:190`, `Hosting/X11XamlRootHost.cs:319` | xdg_toplevel.set_app_id matching desktop entry; later xdg-toplevel-icon-v1 | P1 / P2 icon |
| Fullscreen | `UI/Xaml/Window/X11WindowWrapper.cs:120` | xdg_toplevel.set_fullscreen / unset_fullscreen; state from configure | P1 |
| Minimize, maximize, restore, constraints | `UI/Windowing/X11NativeOverlappedPresenter.cs:44`, `UI/Windowing/X11NativeOverlappedPresenter.cs:54`, `UI/Windowing/X11NativeOverlappedPresenter.cs:67` | xdg_toplevel set_minimized, set/unset_maximized, min/max size; no reliable minimized query or force restore | P1 |
| Move, resize, activation | `UI/Xaml/Window/X11WindowWrapper.cs:145`, `UI/Xaml/Window/X11WindowWrapper.cs:160`, `UI/Xaml/Window/X11WindowWrapper.cs:56` | No absolute desktop position; interactive move/resize needs seat serial; activation-v1 needs token | P1 |
| Cursor shapes | `Devices/Input/X11PointerInputSource.cs:79` | cursor-shape-v1 when present; libwayland-cursor + cursor surface fallback, scaled hotspot | P0 |
| Native XIM / D-Bus IME | `IME/X11InputMethodDetector.cs:47`, `IME/IBusInputMethod.cs:53`, `IME/FcitxInputMethod.cs:49`, `Devices/Input/X11KeyboardInputSource.cs:34` | Prefer text-input-v3; xkb compose fallback where unavailable; no XIM on Wayland | P1 |
| D-Bus binding generation | `Uno.UI.Runtime.Skia.X11.csproj:25`, `Uno.UI.Runtime.Skia.X11.csproj:30` | Extract existing Tmds bindings/adapters to shared Linux service layer; no new dependency for P0 | P1 |
| GL/Vulkan/software selection | `Hosting/X11XamlRootHost.cs:446`, `Hosting/X11XamlRootHost.cs:465`, `Builder/X11RenderingBackend.cs:8` | EGL/GLES over wl_egl_window, fallback wl_shm CPU renderer; optional Vulkan Wayland surface later | P0 / P2 Vulkan |
| Vulkan surface | `Vulkan/X11VulkanSurfaceFactory.cs:44` | VK_KHR_wayland_surface, shared VulkanContext with Wayland surface factory | P2 |
| Frame pacing | `Rendering/X11XamlRootHost.Rendering.cs:1` | wl_surface.frame callbacks coalesce invalidations; buffer release gates reuse | P0 |
| Multi-window / ALC cleanup | `Hosting/X11ApplicationHost.cs:88`, `Hosting/X11XamlRootHost.cs:88` | Shared connection, per-window object map and cancellation generations; explicit lifecycle cleanup without reflection | P0 multi-window / P2 ALC |

## Differences that affect LinuxFiles

Clipboard copy/cut and cross-application file dragging require MIME interoperability (`text/uri-list` and the desktop cut/copy conventions), asynchronous pipe transfer, and a valid input serial for selection/drag initiation. Core data-device selection is seat/focus mediated; privileged data-control protocols are not a baseline requirement. Portal dialogs must export a Wayland parent rather than reuse the X11 hexadecimal XID. Native Gtk/WebView embedding cannot reuse X11 airspace/reparenting and is separate P2 work. Framebuffer libinput is not used by a desktop Wayland client: the compositor supplies input through wl_seat. Only xkbcommon mapping and managed event construction are candidates for reuse.

Ordinary Uno popups are in-surface: `src/Uno.UI/UI/Xaml/Controls/Popup/Popup.Base.cs:96` registers with `XamlRoot.VisualTree.PopupRoot`; `src/Uno.UI/UI/Xaml/Controls/Popup/PopupRoot.cs:18` derives from Canvas. See the design for native popup boundaries.
