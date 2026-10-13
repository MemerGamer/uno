# Native Wayland Skia rendering

The implementation lives entirely in `src/Uno.UI.Runtime.Skia.Wayland/Rendering`.
It uses the existing SkiaSharp dependency and source-generated `LibraryImport`
interop; it introduces no runtime NuGet dependency, reflection, or dynamic code.
The Wayland runtime project and host are integration work owned by other components.

## Host integration contract

Create `WaylandRenderer` with the native `wl_display*`, the window's `WlSurface`,
`WlShm`, an optional `WpViewport`, an `Action<SKSurface, GRContext?>` that paints a
complete frame (the context is null on wl_shm), and an `Action` that produces a UI
frame without presenting it (see [Frame pacing](#frame-pacing)). The namespace for protocol proxies is assumed to be
`Uno.WinUI.Runtime.Skia.Wayland.Protocols`; proxies expose `Handle`, request
methods, and `EventHandler` events. `WaylandRendering.Stubs.cs` records the precise
required signatures behind `WAYLAND_RENDERING_STUBS`. That symbol is only for
standalone compilation; never define it in the actual runtime. Delete the file
when integrating real bindings. Event payloads for callback time and buffer release
are not consumed; typed generated event arguments can be handled by adapter methods.

The host must marshal configuration, invalidations, scale changes, protocol event
handlers, drawing, and disposal onto one render owner thread. The dedicated display
dispatch thread must post events to this owner without waiting synchronously for
rendering: `eglSwapBuffers` may wait for compositor progress. Flush outgoing display
requests after rendering and after disposal, including on the software path. Do not
hold a dispatch lock across an EGL swap. Coordinate the display's prepare-read /
read-events / cancel-read protocol with the EGL driver's event queue.

Acknowledge `xdg_surface.configure` before calling `Configure(logicalWidth,
logicalHeight)`. Resolve zero dimensions in `xdg_toplevel.configure` to the host's
last/requested logical size first. No content is committed before configuration.
Create and initially commit the empty xdg surface in the host to obtain that first
configure. Dispose the renderer before destroying the viewport, fractional-scale
object, surface, or display.

For Uno integration, call `CompositionTarget.OnNativePlatformFrameRequested` with a
persistent layer canvas: a GPU `RetainedLayer` created with the supplied `GRContext`,
or a raster surface on wl_shm. The drawing callback clears the target to the host
background and draws the layer into it. Recreate the layer, passing a null prior
canvas, when the `GRContext` changes (context loss or wl_shm fallback). The configured
logical size and `XamlRoot.RasterizationScale` must be updated before this call so
CompositionTarget's pixel dimensions match the supplied surface. Route
`IXamlRootHost.InvalidateRender` to `Invalidate()` on the owner thread. Report
`IsSoftware` to the shared compositor, initially and on `BackendChanged`, and log
`EglInitializationError` when falling back. Native-child airspace clipping is not
implemented in this component.

## Backends

EGL obtains the display through
`eglGetPlatformDisplay(EGL_PLATFORM_WAYLAND_KHR, wl_display, NULL)`, selects a
window-capable RGBA GLES2 configuration with stencil support, creates a
`wl_egl_window`, and wraps framebuffer zero with `GRBackendRenderTarget` and a
bottom-left RGBA `SKSurface`, like X11's OpenGL renderer. Uno draws into a GPU
`RetainedLayer` (CompositionTarget clips later frames to their damage), which is
presented into framebuffer zero: no CPU copy is involved. The swap interval is zero
because `wl_surface.frame` already paces presentation; a non-zero interval would make
`eglSwapBuffers` block while the surface is hidden. Configure changes resize the
native EGL window and recreate Skia framebuffer wrappers before drawing. Context
flush precedes `eglSwapBuffers`, which performs the surface commit. EGL display
initialization is reference counted across renderer instances, preventing one closed
window from terminating another window's display. All windows must use this lease
mechanism; an unrelated EGL user on the same native display requires a shared
lifetime policy.

`EGL_CONTEXT_LOST` from `eglMakeCurrent`/`eglSwapBuffers`, or an abandoned
`GRContext`, recreates the EGL context and surface on the same `wl_egl_window`; the
frame is dropped and redrawn in full because the retained layer belonged to the old
context. If recreation fails the window switches to wl_shm and reports the software
renderer to the compositor.

Like X11's default selection, initialization attempts GPU rendering and falls back
to software on expected EGL/native-library initialization failures. Explicit
`forceSoftware` or `UNO_WAYLAND_FORCE_SOFTWARE=1` selects raster rendering. This
variable is Wayland-specific: the inspected X11 implementation uses builder backend
selection rather than a software-force environment variable. Rendering failures
after initialization propagate to the host; they do not silently replace an active
backend. The EGL 1.5 entry point is required; extension-only EGL implementations
fall back to software.

Software rendering uses `memfd_create(MFD_CLOEXEC)`, `ftruncate`, and a shared
read/write `mmap`. A `wl_shm_pool` creates an ARGB8888 `wl_buffer`; the pool proxy
and file descriptor can then be closed. Skia uses BGRA8888 premultiplied pixels,
matching Wayland ARGB8888 on Linux little-endian targets. Big-endian targets require
an explicit format conversion. Each of up to two buffers owns its mapping and
raster surface. A committed buffer stays busy until its release event. Resize
retires mismatched buffers and frees them after release; retired buffers count
against the two-buffer memory bound, so rapid resize waits instead of growing memory.
Renderer disposal destroys proxies and unmaps client mappings. The compositor's
independent shared-memory mapping remains valid after client unmapping. Checked
stride and byte-count arithmetic rejects overflow before allocation.

## Frame pacing

The first configured invalidation bootstraps a frame without a previous callback.
Every submitted frame requests `wl_surface.frame` before its commit. While that
callback is outstanding, invalidations only set a dirty bit. Callback completion
allows one more dirty frame, and an idle completion submits nothing. A compositor
may withhold callbacks for hidden windows; rendering then naturally pauses.

If both software buffers are busy, the attempted frame cancels its uncommitted
callback and retains the dirty bit. A release event retries it. Drawing failures
also cancel the callback and preserve dirty state, then propagate.

A compositor may withhold callbacks (and buffer releases) for hidden windows. Uno
schedules its next UI frame from the native frame request, so presentation stalls
would also stall the UI, unlike X11 where a minimized window keeps rendering. When
a dirty frame makes no progress for 100 ms, the scheduler lets the host produce the
frame into its retained layer without presenting it. This repeats only on further
invalidations, so an idle hidden window does no work and an animated one runs at
about 10 frames per second. The next frame callback presents the latest content.

## Scale

Forward `wp_fractional_scale_v1.preferred_scale` to
`Scale.SetPreferredScale(uint)`. Use fractional scaling only when the host has
bound both fractional-scale and viewporter extensions and created a viewport.
The effective scale is the preferred numerator divided by 120. Buffer dimensions
are `ceil(logicalDimension * effectiveScale)`, buffer scale is one, and viewport
destination is the logical size. Ceiling avoids undersized buffers at odd sizes.

Without both extensions, omit the viewport and forward `wl_surface.preferred_buffer_scale`
(surface v6) or, on older surfaces, the maximum integer scale of entered outputs to
`Scale.SetIntegerScale(int)`. The buffer scale is that integer
and dimensions are logical size times scale. Recompute the output maximum on enter,
leave, output-scale updates, and output removal. The host subscribes to
`Scale.Changed` to update `XamlRoot.RasterizationScale`, DPI/display-information
notifications, and layout before painting. The host must call `Invalidate()` after applying those scale/layout changes;
this ordering prevents a synchronous render from observing the old XamlRoot scale.
No additional canvas scaling should be applied if CompositionTarget already uses
RasterizationScale.

## Validation and remaining integration

**Code review:** reviewed against X11Renderer, X11EGLRenderer,
X11SoftwareRenderer, X11XamlRootHost.Rendering, and shared EglHelper behavior.
Explicit lifecycle invariants are single-thread ownership, at most one outstanding
committed frame callback, no reuse before buffer release, and EGL display termination
only after the last renderer closes.

**Compile:** a scratch net10.0 project compiled every rendering source against
SkiaSharp 3.119.2 and the conditional proxy stubs, with zero errors. Cached packages
were used; NuGet vulnerability lookup was unavailable. The new runtime csproj and
real protocol bindings do not exist in this worktree, so full Uno integration is
not compile-validated. `dotnet format` was blocked by sandbox named-pipe permissions;
formatting was applied directly using repository tab/Allman conventions.

**Runtime:** a scratch executable passed callback bootstrap/coalescing/idle,
release retry, scheduler disposal, fractional size rounding, integer fallback,
double-buffer saturation/reuse, and resize retirement. Protocol events were faked;
software allocation and painting used real libc memfd/mmap and native Skia.
No EGL presentation or compositor protocol behavior was tested.

After integration, run LinuxFiles with both default EGL and
`UNO_WAYLAND_FORCE_SOFTWARE=1`, at scales 1, 1.25, 1.5, and 2. Check live resize,
output migration, minimize/restore, continuous animation, compositor-held buffers,
and two windows closing in either order. Verify sharp text, logical pointer
coordinates, XamlRoot scale notifications, and clean close. Include a compositor
without fractional-scale/viewporter and validate integer fallback. Validate NativeAOT
and trimming in the integrated runtime project (`AllowUnsafeBlocks` is required
for LibraryImport generated code).
