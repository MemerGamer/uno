# Native Wayland input

## Status and files

The input implementation lives in `src/Uno.UI.Runtime.Skia.Wayland/Input`. It introduces no NuGet dependencies and does not use reflection. It is an integration component, not a runnable Wayland host.

| File | Responsibility |
| --- | --- |
| `WaylandInputConnection.cs` | Display-thread transport contract and `WaylandSeat` capability lifetime |
| `WaylandProtocolInputConnection.cs` | Proxy event subscriptions, surface filtering, version fallback, unsubscribe before disposal |
| `WaylandPointerInputSource.cs` | Uno pointer source, buttons, scrolling, basic touch |
| `WaylandKeyboardInputSource.cs` | Uno keyboard source, focus, modifiers, repeat |
| `WaylandXkb.cs` | FD ownership and Wayland-specific xkbcommon operations |
| `Wayland.Input.props` | Source link to the existing FrameBuffer xkbcommon binding |
| `WaylandKeyMap.cs`, `Keysyms.cs` | Licensed X11 keysym mapping, with the enum reduced to referenced symbols |
| `WaylandCursorSurface.cs` | Cursor requests and borrowed-buffer attachment |
| `WaylandCursor.cs` | Optional compositor shapes and libwayland-cursor theme fallback |
| `Protocols.Stubs.cs`, `FrameBufferXkb.Stubs.cs` | Temporary compile contracts; remove during integration |

## Host and protocol integration

Import `Input/Wayland.Input.props` from the new runtime project and enable unsafe compilation. No runtime project existed when this input work began; adding one belongs to the host owner. The runtime assembly needs `InternalsVisibleTo("Uno.UI.Runtime.Skia.Wayland")` in the applicable Uno.UWP/Uno.UI assembly-info files for internal input interfaces, constructors and setters. Those edits are outside input folder ownership.

Bind each advertised `wl_seat` through `WaylandDisplay.Registry`, at the lesser of advertised and generated supported versions. Create one connection and sources for each seat, install listeners before roundtrip/dispatch, and instantiate `WaylandSeat` with the seat proxy. The seat wrapper subscribes to capabilities. Loss of capabilities cancels contact state or releases held keys before destroying the associated proxies; global removal disposes the wrapper. The host owns and disposes the seat proxy separately. Multiple seats need host-level routing and unique pointer identities before being exposed simultaneously to one XamlRoot; the present source IDs are scoped to one seat.

`Protocols.Stubs.cs` supplies abstract `WlSeat`, `WlPointer`, `WlKeyboard`, and `WlTouch` proxy contracts in the anticipated Protocols namespace. It intentionally has no native implementation. Replace it with generated classes, then adjust event-argument access in `WaylandProtocolInputConnection` to the generator's concrete event types. `InputEventArgs` is a temporary common payload, not a requirement for the generator. `Surface` represents proxy identity, not a surface coordinate. Route only this XamlRoot host's surfaces via `ownsSurface`; generated fixed values must become doubles by dividing their wire representation by 256 exactly once.

Pass an ordered UI queue as `dispatch`. Pass the display thread's queue/wakeup operation as `Post`; it must wake the polling fd and execute queued requests without requiring another compositor event. Repeat timers only post to this queue and never touch native state directly. Run source disposal on the display thread before disconnecting. The generation token rejects queued repeat work after release, focus loss, keymap replacement and disposal. Proxy `Dispose` must use version-appropriate release/destroy requests, or local proxy destruction when release is unavailable. Native listener boundaries must catch exceptions and report a controlled host input failure rather than unwind through an unmanaged callback.

The activation callback runs on the UI queue. Integrate keyboard enter/leave with the host's window activation machinery; reconcile it with xdg-toplevel activation state rather than independently toggling contradictory window state.

## Pointer and touch

Pointer and touch coordinates are surface-local logical values. Keep the main surface's viewport destination in Uno logical units: **do not divide input coordinates by integer or fractional buffer scale**. Buffer density only affects rendering and cursor theme selection. Decoration/subsurface coordinates must be translated by the host if the supplied surfaces have different origins. See the [core protocol specification](https://wayland.freedesktop.org/docs/html/apa.html).

Linux button codes map left, right, middle, side/back and extra/forward into Uno button properties and update kinds. Unknown buttons are ignored. Mouse IDs use zero; touch IDs use protocol ID plus one. Touch down/motion/up preserve point snapshots until `wl_touch.frame`; cancel flushes pending events then cancels every active contact. Primary touch stays attached to the original contact for the sequence. Shape/orientation, pressure, tablet tools and gestures are not implemented.

At pointer frame completion each axis selects value120 over discrete detents over continuous values, avoiding duplicate wheel delivery. Vertical Wayland positive-down becomes negative Uno wheel delta; horizontal positive-right becomes negative, matching the existing X11 Uno source. Discrete detents use 120. Continuous scrolling uses a configurable-in-future policy of 12 wheel units per logical unit (10 logical units per detent), retaining fractional remainders. Axis stop clears the remainder after processing the frame. For pointer versions below 5, the adapter flushes on each axis event because frame is unavailable. Pointer timestamps convert milliseconds to microseconds with rollover tracking. Axis source and relative-direction events currently require no transformation: delivered axis direction is respected; source-specific acceleration/inertia remains future work.

Core Wayland supports implicit button grabs, not application-directed pointer capture. `HasCapture` remains false and capture methods do not send a global grab. Capability loss with pressed buttons raises cancellation and capture-lost notifications. Evaluate pointer constraints separately if explicit capture becomes necessary.

## Keyboard and shared xkbcommon

The keymap FD is consumed and closed on every format/validation path. Read the announced byte count with positional reads, so the sender's file offset cannot cause a truncated keymap. Enforce a 16 MiB allocation limit and trailing NUL, compile format 1, and replace the previous context/keymap/state only after the replacement succeeds. The binding owns and unrefs all three native objects.

Wayland keycodes are evdev codes; add eight only for xkbcommon lookups. Uno scan codes retain the original evdev value. Modifier events supply authoritative depressed/latched/locked masks and layout group; do not update XKB state a second time from key events. Shift/Control/Mod1/Mod4 map to Uno modifiers. Mod5 permits LevelThree printable input when modifier masks would otherwise suppress it. This convention needs testing on layouts with unusual modifier assignments.

VirtualKey mapping reuses X11's licensed keysym table, including modifier/navigation/keypad/media keys. Releases retain the VirtualKey from the press even if modifiers/layout change. Key enter initializes held-key state without generating new presses. Leave stops repeat, releases held keys, clears modifier state and queues deactivation. Repeat uses compositor rate/delay, XKB per-key eligibility, and `WasKeyDown`; rate zero disables client repeat. Protocol state 2 supports compositor repeat at rate zero on keyboard version 10. Like X11, basic BMP printable text travels in `KeyEventArgs.UnicodeKey`, avoiding duplicate CharacterReceived insertion. Dead-key and Compose sequences go through `xkb_compose` (locale from `LC_ALL`/`LC_CTYPE`/`LANG`): intermediate keys carry no text, the completing key carries the composed character, and focus loss resets the sequence. Supplementary Unicode and multi-character results are not delivered, since `UnicodeKey` is a single UTF-16 unit.

The existing FrameBuffer binding is internal and its `xkb_rule_names` constructor references `FramebufferHostBuilder.XKBKeymapParams`. To reuse it without editing another owner's files, this implementation source-links that file and supplies the temporary builder type. `WaylandXkb` wraps its common context/state/keysym operations and adds only missing Wayland operations with `LibraryImport` against `libxkbcommon.so.0`.

For upstream integration, move the binding into an existing shared Linux/native source location, decouple `xkb_rule_names` from the FrameBuffer builder, and compile it into both hosts. Convert existing DllImport declarations to LibraryImport where feasible and normalize resolution to the versioned SONAME: the linked existing binding currently requests `libxkbcommon.so`, which may require the development symlink on a distribution. Delete `FrameBufferXkb.Stubs.cs` and update the source link after extraction. Likewise, extract the licensed keysym mapping to shared source instead of retaining a second table. No shared files were changed by this task. Native AOT publishing and trimming still require validation after this extraction and host assembly integration.

## Cursor

Provide `WaylandCursor` with the native `wl_shm` proxy, a cursor surface adapter, and an optional shape device. The host should call `wp_cursor_shape_manager_v1.get_pointer` once per pointer capability lifetime, `WaylandCursor` then sends `wp_cursor_shape_device_v1.set_shape` using the latest pointer-enter serial. Pass null if the optional manager is unavailable. Destroy the shape device before releasing its pointer. The cursor source posts UI cursor changes to the display thread and reapplies the cursor on enter.

`IWaylandCursorSurface.Show` must send `wl_pointer.set_cursor`, set integer buffer scale, attach the borrowed wl_cursor image buffer, damage and commit. Dimensions are buffer pixels; hotspots are logical coordinates. It must not destroy borrowed buffers. `Hide` uses `set_cursor(serial, null, 0, 0)` and detaches/commits the previous cursor surface before a theme is replaced. Dispose the surface before destroying its theme. `WaylandCursorSurface` implements this request sequence against the temporary proxies. Translate its raw borrowed-buffer Attach argument to the generated buffer representation without assuming buffer ownership. The cursor surface needs core version 3 or later for set_buffer_scale.

Theme fallback uses libwayland-cursor, `XCURSOR_THEME`, a base size of 24 pixels and ceil(render scale), bounded to 1–8. It shows the first theme image; animation and XCURSOR_SIZE are follow-up work. Arrow, crosshair, hand, help, text, move, resize, unavailable and wait map to protocol shapes/theme names; custom/person/pin fall back to arrow and up-arrow uses a theme name where available. Null hides the cursor. Verify the shape values against the generated enum on integration; the protocol object is `wp_cursor_shape_device_v1`, rather than `wp_cursor_shape_v1`. [Protocol XML](https://gitlab.freedesktop.org/wayland/wayland-protocols/-/blob/main/staging/cursor-shape/cursor-shape-v1.xml).

## Text-input-unstable-v3 plan

IME is deliberately not wired in this component. The existing X11 `IImeTextBoxExtension` demonstrates Uno's CompositionStarted/Updated/Completed/Ended events and caret/focus hooks, but text-input-v3 also needs transactional surrounding-text changes and byte-offset deletion. Reusing the D-Bus IME singleton would introduce the wrong lifecycle for Wayland seats and windows.

1. Bind optional `zwp_text_input_manager_v3`; create one text input per seat. Keep keyboard shortcuts operational without it.
2. Implement a per-host `IImeTextBoxExtension` and register it through the host's ApiExtensibility setup. Start only when both TextBox focus and text-input enter match the host surface. Enable, set content hints/purpose, surrounding text, change cause and cursor rectangle, then commit. End/leave disables and commits, clearing pending composition without inventing committed text.
3. Convert Uno UTF-16 selection offsets to UTF-8 byte offsets and back. Surrounding text must fit 4000 bytes and be clipped on code-point boundaries with selection offsets adjusted. Password fields need sensitive hints and a deliberate policy for withholding surrounding text. Caret rectangle is surface-local logical geometry.
4. Buffer preedit_string, commit_string and delete_surrounding_text until done. Use the protocol-defined ordering: remove previous preedit, delete surrounding text, insert committed text, restore selection and establish new preedit. Validate done serials against committed state and discard stale state after a focus/session change.
5. While an enabled text-input session supplies text, suppress the XKB printable fallback to avoid double insertion, while preserving shortcut KeyDown/KeyUp behavior. Deliver full committed strings through the composition hooks so supplementary Unicode is preserved.
6. Test selection replacement, UTF-8 deletion around emoji, preedit cursor ranges, focus changes with pending done events, virtual keyboard visibility, and manager absence against a compositor and LinuxFiles.

The state is double-buffered and surrounding offsets are byte offsets; see the [upstream text-input-v3 XML](https://chromium.googlesource.com/external/anongit.freedesktop.org/git/wayland/wayland-protocols/+/refs/heads/master/unstable/text-input/text-input-unstable-v3.xml).

## Validation evidence and remaining checks

**Code review:** inspected X11 pointer/keyboard mapping and IME hooks, FrameBuffer pointer/touch and xkbcommon code, plus the core protocol. Reviewed logical-coordinate handling, FD ownership, native-object teardown, listener removal and repeat generation checks.

**Compile:** a scratch .NET 10 project compiles all Input C# files, the source-linked FrameBuffer binding, real repo key/modifier/cursor enums and physical-key status, against small Uno API stubs. This validates C# and interop generation, not Uno assembly integration or the future generated proxy API. The harness is at `/tmp/wayland-input-check` for this workspace; run `dotnet build /tmp/wayland-input-check/check.csproj --no-restore` and its output DLL. Scratch build warnings include inherited duplicate analyzer configurations and legacy linked-binding nullability.

**Runtime:** the scratch executable passes seat capability acquisition/removal/reacquisition and listener cleanup, cursor shape selection/hiding/idempotent disposal, logical coordinates, scrolling precedence/sign and fractional accumulation, touch-frame buffering/cancellation, native xkbcommon keymap compilation, VirtualKey mapping, modifiers, client repeat and repeat stop on release. Tests use the installed native xkbcommon library; pointer events use Uno API stubs. This is not a compositor or Uno runtime test.

**Unverified:** full runtime build, native AOT/trim publish, generated event signatures, seat hotplug against a compositor, fractional cursor rendering, window activation, supplementary/dead-key text and IME. Once integrated, run LinuxFiles on Sway/Weston and GNOME/KDE with 100%, 125%, 150% and 200% scales, then test scale transitions, mouse/trackpad scrolling, held keys during focus loss, capability removal and host shutdown. Add actual Uno runtime tests before proposing upstream merge.

Open integration decisions: generated proxy event payload/namespace, display queue wakeup API, multi-seat routing, shared Linux xkb source location and SONAME resolver ownership, cursor surface wrapping for borrowed wl_buffer pointers, and transactional TextBox surrounding-text access.
