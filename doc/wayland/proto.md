# Wayland protocol layer

The protocol layer is in `src/Uno.UI.Runtime.Skia.Wayland/Native` and `Protocols`, in namespace `Uno.WinUI.Runtime.Skia.Wayland` (generated types are in `.Protocols`). It has no external runtime NuGet dependencies. The runtime project follows the Skia project's target framework and packaging conventions and is included beside X11 in the solution and three solution filters.

## Generation

Run from the repository root:

```sh
dotnet run --project build/wayland-scanner/Wayland.Scanner.csproj -- \
  build/wayland-scanner/protocols src/Uno.UI.Runtime.Skia.Wayland/Protocols
```

The checked-in XMLs are copied verbatim from `/usr/share/wayland` and `/usr/share/wayland-protocols`. Each retains its copyright notice. `protocols/LICENSE.txt` collects those MIT notices, also shipped as a third-party notice in the runtime package. The tablet-v2 protocol is included because cursor-shape references `zwp_tablet_tool_v2`. There are eleven XML files and 55 generated interfaces, including core subcompositor/subsurface support.

The generator emits request opcodes, version checks, signatures, interface cross-references, enum values, typed `EventHandler<TEventArgs>` events, and native Cdecl listener tables. Dynamic `new_id` arguments (registry bind) expand into interface name, version, and object ID. Protocol event array payloads are copied before returning from the callback. Fixed point arguments use `WaylandFixed`, preserving their exact 24.8 representation. Wire enums remain `uint` arguments, with nested `*Values` enums available for constants.

Both interface tables and listener tables use process-lifetime unmanaged memory: libwayland retains their addresses. Per-request strings and arrays are freed immediately after synchronous marshalling. XML input must provide every referenced interface; generation rejects missing or duplicate interfaces before writing output.

## Display and lifetime contract

```csharp
using Uno.WinUI.Runtime.Skia.Wayland;
using Uno.WinUI.Runtime.Skia.Wayland.Protocols;

using var display = WaylandDisplay.Connect();
using var compositor = (WlCompositor)display.Bind(
    WlCompositor.ProtocolName, WlCompositor.MaximumVersion);
// Subscribe to protocol events and finish initial roundtrips before dispatch.
display.StartDispatch();
// The host runs its UI loop here.
display.StopDispatch();
```

`Connect()` discovers initial globals using a roundtrip. `Globals` returns a snapshot containing registry name, interface name, and advertised version. `GetGlobal()` returns a matching global; `Registry.Global` and `GlobalRemove` expose subsequent changes. Bind negotiates the minimum of advertised and requested versions; callers must request no more than the generated `MaximumVersion`.

`Roundtrip()` is available before starting the dispatch loop. `Sync()` supports asynchronous barriers while dispatch runs: subscribe to its `Done` event before releasing `display.SyncRoot` if dispatch is already running. The same lock can protect compound host operations and event subscription during object creation. Generated request methods serialize native marshalling and proxy construction against event dispatch.

Events run on the dedicated dispatch thread, under `SyncRoot`; the hosting/input layers must post UI work to their dispatcher. Do not block that thread waiting for UI work that acquires this lock. Stop and dispose the display outside its event callbacks. Event exceptions are caught at the unmanaged boundary; they stop dispatch and are exposed in `DispatchError` with the original exception as the inner exception.

The dispatch loop uses `prepare_read`, dispatches previously queued events, flushes requests, polls the connection and a nonblocking wake pipe, and either reads events or cancels the prepared read. Flush backpressure enables writable polling. Request marshalling wakes the loop; stopping wakes an idle poll and joins the thread. Connection and polling errors stop dispatch and populate `DispatchError`.

Dispose proxies deterministically. Protocol destructor requests are sent where supported by the bound version; otherwise disposal destroys the local proxy. Destructor events (callback `Done`) destroy the local proxy after notifying subscribers. Display disposal frees remaining local proxies and GCHandles before disconnecting. The display tracks live proxies, including objects introduced by protocol `new_id` events, so listeners stay alive until disposal.

Incoming event file descriptors transfer to event subscribers, which must close them, including on exceptions. If an fd-bearing event has no subscriber, its callback closes the descriptor. Descriptors passed into requests remain caller-owned. `ConnectToFileDescriptor()` transfers a connected Unix socket descriptor to libwayland, including failure paths.

The native interop includes explicit event-queue creation, destruction, assignment, dispatch, preparation, and roundtrip entry points. The public display loop owns the default queue; a public custom-queue API is not provided yet. Never attach a second native listener to a managed proxy or dispatch its events outside the display's synchronization contract.

## Validation

```sh
dotnet run --project build/wayland-scanner/tests/Wayland.Scanner.Tests.csproj
dotnet run --project build/wayland-scanner/smoke/Wayland.Smoke.csproj
# Optional dispatch startup/shutdown check; still creates no windows:
dotnet run --project build/wayland-scanner/smoke/Wayland.Smoke.csproj -- --dispatch
```

**Code review:** no reflection, varargs P/Invokes, or runtime-generated delegates are used. Native calls use `LibraryImport`; listeners use static `[UnmanagedCallersOnly]` function pointers. Version, null-argument, and cross-display checks are defensive hardening at the request boundary.

**Compile:** the scanner and the linked standalone smoke/test projects compile on .NET 10. The full runtime project build is unverified: its `--no-restore` build stops at NETSDK1004 because restore assets are absent. The local environment also emits existing duplicate global analyzer configuration warnings.

**Runtime:** seven MSTest tests pass. Tests cover deterministic generation, unresolved references, wire signatures, every core interface/message table compared against the installed libwayland ABI, and an in-process Unix socket peer exercising real libwayland registry discovery, proxy construction, version rejection, sync callbacks, callback exception containment, and dispatch stop/restart. Tests use libc writes because sandbox policy rejects `Socket.Send` even on an in-process socket pair.

**Unverified:** connection to the session compositor failed, so real compositor behavior and extension event traffic need downstream validation. NativeAOT publication was attempted, but MSBuild failed to create its ILLink task host (MSB4216/MSB4027); native publication and trimmed execution remain unverified. .NET 9 compilation is also unverified.

Rendering, input interpretation, host registration, and window construction belong to the other runtime layers and are not implemented here. Consumers should agree on UI dispatch handoff and protocol object ownership before integration.
