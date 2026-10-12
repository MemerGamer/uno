**GitHub Issue:** closes #TODO_UPSTREAM_ENHANCEMENT_NUMBER

<!-- Proposed PR title: feat(wayland): Add native Linux Skia host -->
<!-- Draft follows .github/pull_request_template.md. Replace TODOs and narrow the scope to the actual submitted diff before posting. -->

## PR Type:

✨ Feature

## What changed? 🚀

### Draft status and intended scope

Proposed title: **feat(wayland): Add native Linux Skia host**.

This is a detailed draft for the combined native Wayland work, not a claim that the combined host is ready to merge. Development started in MemerGamer/uno from Uno 6.7.135 (`da217df231`). TODO: record the final upstream base, integrated commit SHA, actual changed-file list and agreed PR scope. Split PRs should each get a description of their own final behavior rather than submit this entire umbrella description unchanged.

The inspected branch evidence is:

| Branch | Inspected tip | What can currently be stated |
|---|---|---|
| `wayland/map` | TODO: capture final integration SHA | Design, X11 service inventory and Hosting/Builder skeleton; explicitly not a running window |
| `wayland/render` | `469eb83d94` | EGL/software renderer sources and limited scratch validation |
| `wayland/build` | `065f98cc2a` | Fork-local feed tooling/documentation; actual Wayland project is still missing in that branch |
| `wayland/proto` | `da217df231` | Committed tip remains the release baseline; in-progress implementation not assessed here |
| `wayland/input` | `da217df231` | Committed tip remains the release baseline; in-progress implementation not assessed here |

These are snapshots of separate branches, not proof of an integrated build. TODO: refresh this table when proto/input and integration land; replace provisional statements below with confirmed behavior.

### AI-assistance disclosure and author responsibility

> **AI-assistance disclosure:** This code was written with AI coding agents, **OpenAI Codex and Anthropic Claude**, under my direction. I reviewed the code and am responsible for the submitted implementation, its licensing, correctness, maintenance and responses to review. AI assistance is disclosed explicitly so reviewers can assess the contribution with that context.
>
> Verification currently documented consists of source/contract review, isolated compilation against real shared sources plus stubs, rendering scratch tests with fake protocol events and real software allocation/Skia, and packaging syntax/dry-run/synthetic-fixture checks. **These checks do not establish a working integrated Wayland host.** No real-compositor or EGL presentation result is claimed below.

The statement of author review above is supplied by the author for this draft; this documentation task cannot independently attest to its extent. TODO: add the author's reviewed commit range, review findings resolved, independent human reviews, exact commands/logs and any integrated results. Protocol-scanner-generated bindings are a separate deterministic generation concern: TODO pin XML sources, generator revision, licenses and reproduction commands. Neither generated output nor AI authorship waives ordinary contribution standards.

### Motivation and user-visible result

The Linux desktop Skia transport in the inspected baseline is X11; it does not provide a native Wayland host. Under Wayland, an X11 application uses XWayland. On compositor setups that do not provide suitable XWayland scaling, compositor upscaling can make application text and UI blurry at fractional scale. The motivating configuration is **niri with xwayland-satellite**, and the downstream use case is [LinuxFiles issue #113](https://github.com/MemerGamer/LinuxFiles/issues/113).

This scenario and link were supplied by the author. The issue contents could not be fetched during drafting. TODO: confirm its exact reported symptom, distro, compositor/satellite versions, display scale and reproduction steps; do not generalize the report to every XWayland setup or claim that native Wayland sharpness has already been demonstrated.

The intended result is direct Wayland presentation with layout in logical coordinates and buffers rasterized at the compositor's preferred scale. Apps opt into the new host while retaining an explicitly registered X11 fallback. This adds a Linux **Skia** transport; “native Wayland” describes the window-system connection, not a change to Uno's Native renderer or WinUI control API.

TODO screenshots: capture the same LinuxFiles view/font/window dimensions on XWayland and native Wayland at 100%, 125%, 150% and 200%; record compositor, font, GPU, backend and settings. Attach unresampled crops and full screenshots, including the forced software path. Until attached, sharpness is a design goal rather than verified improvement.

### Included work and boundaries

The combined proposal covers `src/Uno.UI.Runtime.Skia.Wayland`, builder/host/window integration, reproducible native protocol bindings, Linux display/input services, EGL and shared-memory Skia rendering, internal friend access, package/build integration and tests/documentation. TODO: enumerate the actual included directories, samples and tests from the final diff.

The map inventories all 20 X11 host extension-registration sites, including fork additions. It is an integration inventory, not a promise to deliver all X11 services in the first PR. Clipboard/DnD, portal extraction and full IME are follow-up scope unless explicitly completed and tested before submission. Optional Vulkan, native WebView/media embedding, native overflow popups and advanced pointer/activation protocols require separate proposals. The fork's X11 Vulkan/D-Bus IME additions are not implicitly included in this upstream contribution.

### Threading and lifetime architecture

The design uses one `wl_display` connection per application host, a wakeable protocol dispatch thread, and a Uno UI thread. Application proxies belong to the protocol owner; callers enqueue work and wake an eventfd/pipe. Protocol callback data is copied into immutable managed snapshots before posting to `CoreDispatcher`; native strings/arrays never escape callback lifetime. Input preserves ordering; resize/render notifications may coalesce only when intermediate states are dispensable.

The dispatcher drains pending events and commands, prepares a read, flushes, polls display/wake descriptors and reads/dispatches. Wake-only readiness cancels a prepared read before running commands; `EAGAIN`, `EINTR`, disconnect and errors need explicit handling. Startup synchronization is bounded and roundtrips are startup-only. Required-global removal or display disconnect terminates the chosen session; live windows do not migrate to X11.

Uno owns application startup, layout, focus, window events, root registration and shutdown on UI. Dispatcher overrides must be installed before `Application.Start` without overwriting another host's dispatcher. The dispatch thread must not synchronously wait for UI or a rendering operation. GPU calls stay on the context-owning render thread; EGL may have its own event queue, so libwayland read ownership and outbound application commits need explicit coordination.

**Integration blocker:** the renderer currently requires host calls and its surface/proxy operations on a render owner, while the map assigns all application proxies to dispatch. TODO: implement/document the exact ownership bridge, queue/locking strategy and EGL queue coordination. Do not claim the two branch contracts already agree. Disposal must cancel queued work, retire windows/renderers, wake polling, join from an external thread and disconnect last.

Each window gets separate shell/surface objects, scale, configure generation, renderer and XamlRoot host over the shared display. Acknowledge `xdg_surface.configure` before the first buffer; resolve zero toplevel dimensions as client-chosen sizes and reject stale render generations. Initial commit is bufferless. Closing raises Uno's cancellable closing event; accepted close destroys objects in dependency order. TODO: verify Hide/unmap/remap, presenter hooks and generation cancellation in the integrated host.

### Protocols and versions

This table is a **proposed requirement/binding plan**, not an inventory of completed generated bindings. Bind at `min(advertised version, generated supported version)`; never assume a compositor implements the highest version. TODO: replace every unconfirmed binding maximum with generated metadata, pinned XML revision and exercised test result. Protocol family suffixes such as “v3” are names, not necessarily the advertised interface version.

| Protocol/interface | Baseline or version plan | Purpose and current status |
|---|---|---|
| Core `wl_compositor` / `wl_surface` | Require compositor v1; surface v3 enables buffer scale, v4 buffer damage, v6 preferred buffer scale; generated maximum TODO | Required surfaces; negotiate capabilities and gate newer requests |
| Core `wl_shm`, pool/buffer, callback | Require `wl_shm` v1; helper maxima TODO | Guaranteed software path, release and frame notifications |
| Stable `xdg_wm_base` / `xdg_surface` / `xdg_toplevel` | Require shell v1; generated maximum TODO | Configure/ack, title/app ID, window state, ping/pong |
| Core `wl_output` | v2 needed for scale/done; generated maximum TODO | Entered-output integer-scale fallback and output removal |
| Core `wl_seat`, pointer, keyboard, touch | Optional seat at probe; maxima and feature gates TODO | Dynamic capabilities, focus/serial routing, repeat, framed pointer/touch events |
| `wp_fractional_scale_manager_v1` / scale | Proposed interface v1, optional | Preferred scale in units of 1/120; proto integration pending |
| `wp_viewporter` / viewport | Proposed interface v1, optional | Logical destination for fractionally sized buffers |
| `zxdg_decoration_manager_v1` | Proposed interface v1, optional | SSD negotiation; managed CSD required when absent/selected |
| `wp_cursor_shape_manager_v1` | Optional; baseline interface v1 proposed, maximum TODO | Cursor shapes; `libwayland-cursor`/cursor surface fallback |
| `zwp_text_input_manager_v3` / text input | Optional text-input-v3 family, interface v1 proposed | IME integration planned, not verified |
| `wl_data_device_manager` / device/source/offer | Clipboard baseline v1 proposed; action negotiation requires v3; maximum TODO | Clipboard and file DnD follow-up |
| `xdg_activation_v1` | Optional interface v1 proposed | Token-mediated activation; implementation TODO |
| `zxdg_exporter_v2` (xdg-foreign-v2) | Optional interface v1 proposed; exact exporter choice TODO | Portal parent export follow-up |

`wl_registry` and display startup/error handling are core infrastructure; their generated versions and native ownership also need auditing. Primary selection, relative pointer/confinement, privileged data-control, toplevel icons and Vulkan are not baseline requirements. Absence of optional protocols must degrade specific features rather than reject the whole host.

### Rendering, frame pacing and fractional scale

The render branch attempts EGL/GLES first through `eglGetPlatformDisplay(EGL_PLATFORM_WAYLAND_KHR, ...)`, RGBA GLES2/stencil configuration and `libwayland-egl`'s `wl_egl_window`. Skia wraps the default framebuffer; flush precedes `eglSwapBuffers`. EGL display lifetime is reference-counted across windows. Its implementation requires the EGL 1.5 entry point; extension-only implementations take the software fallback. Expected initialization/native-load failures permit fallback, while errors after initialization propagate. `UNO_WAYLAND_FORCE_SOFTWARE=1` explicitly selects software in the renderer; TODO confirm its final supported contract.

Software uses real memfd/ftruncate/mmap storage, `wl_shm_pool` and ARGB8888 buffers with premultiplied Skia BGRA pixels on little-endian Linux. Checked allocation arithmetic rejects overflow. At most two buffers, including retired resize buffers, bound memory growth; committed buffers cannot be reused until `wl_buffer.release`. Frame callbacks coalesce invalidations and pause hidden-window rendering; frame completion is not buffer release. EGL performs its own attachments and must not receive duplicate software buffer commits.

Fractional rendering requires both fractional-scale and viewporter. Preferred scale is numerator/120, `buffer_scale = 1`, viewport destination is the logical size, and buffers/damage are in pixels. Without both, use negotiated preferred integer scale or entered-output policy; old surfaces without scale support stay at one. Scale updates must reach XamlRoot/content site/display notifications before painting; DPI is 96 × rasterization scale and text scaling remains separate.

**Integration blocker:** the map specifies nearest-integer rounding with halves up; render currently uses `ceil(logicalDimension × scale)`. TODO: resolve against the pinned protocol specification, implement one rounding/transform policy, and add odd-size tests before claiming protocol correctness. Also reconcile the map's surface-v6 preferred scale fallback with render's entered-output-only contract. Validate logical pointer positions and exact pixel-to-logical transforms at all scales.

### Keyboard, pointer, touch and cursor

The input plan consumes compositor-provided `wl_seat` capabilities, not `/dev/input` or libinput. Pointer coordinates remain logical; preserve Linux button mapping, wheel/axis source semantics, frame ordering and per-seat focus/serials. Managed capture does not imply global confinement. Touch IDs need stable Uno pointer identity and up/cancel/frame behavior.

Keyboard keymap FDs feed `libxkbcommon`; apply modifier masks, evdev-to-xkb offset, VirtualKey and Unicode mapping separately, compose/dead keys and compositor repeat settings. Cancel repeats on release/focus/capability loss. Cursor-shape is optional; themed `libwayland-cursor` surface fallback must handle pixel/hotspot scaling.

**Status:** input branch is in progress; no implementation/runtime coverage is established by its committed snapshot. TODO: report final keymap FD ownership, layout switching, repeat scheduling, cursor fallback, touch and input tests.

### IME, clipboard, DnD and Linux desktop services

**text-input-v3:** planned compositor-mediated preedit/commit, cursor rectangle, surrounding UTF-8 text and enable/disable on focus. Byte offsets need correct managed-text conversion, and keyboard-derived text must not duplicate IME commits. Without the protocol, xkb compose is the intended basic fallback, not full IME parity. TODO: report actual integration and IBus/Fcitx/compositor compatibility; do not claim support from the protocol's presence alone.

**Clipboard and DnD:** planned `wl_data_device` selection/drag with correct seat serials, MIME negotiation, async FD streaming and cancellation. File-manager interop needs `text/uri-list`, verified file URI parsing, copy/cut conventions and negotiated actions. No completed implementation or desktop interoperability test is claimed. TODO: record formats, transfer limits, focus constraints, cancellation and large-transfer tests.

**Portals/theme/text scale:** extract suitable existing Linux D-Bus adapters from their X11 coupling and export/unexport a Wayland parent handle for requests. No XID parenting is valid here. Reuse the existing Linux launcher where appropriate. TODO: list completed adapters, parent exporter, lifecycle and dependency references. Native element embedding/media and X11-specific add-ins remain separate work.

### Host selection and X11 fallback

Intended application API:

```csharp
using Uno.UI.Hosting;

UnoPlatformHostBuilder.Create()
    .App(() => new App())
    .UseWayland()
    .UseX11()
    .Build()
    .Run();
```

Automatic selection tries registered candidates in order. The map requires `WAYLAND_DISPLAY`, a bounded connection/registry probe and compositor/shell/shm globals. Missing display, normalized native-load/connect failure or missing required globals makes Wayland unsupported in automatic mode. Optional protocols or an absent seat at startup do not. Wayland must not register process-wide extensions just by probing. Preserve/dispose successful probes explicitly to avoid a probe/create race.

X11 fallback exists only if the app registers/references X11; `UseWayland()` must not load it implicitly. Missing EGL should use Wayland software, not force X11. Proposed `UNO_SKIA_HOST=auto|wayland|x11`: forced Wayland errors clearly, forced X11 skips Wayland, unknown values error. TODO: implement shared-selector override behavior for all candidates; the map skeleton has only the Wayland half. `WAYLAND_SOCKET` inheritance is not yet part of this first design. No fallback after a selected session disconnects.

TODO: reconcile the build branch's explicit either-Wayland-or-X11 example with this ordered registration policy. Test both display variables, dead socket, absent globals/native libraries, forced errors, candidate ordering and disposal of an unselected probe. Existing X11-only apps should retain their behavior.

### Public/internal surface and friend assemblies

The proposed additive app entry point is `UseWayland()` in `Uno.UI.Hosting`. Assembly name: `Uno.UI.Runtime.Skia.Wayland`; runtime namespace: `Uno.WinUI.Runtime.Skia.Wayland`. TODO: publish exact signatures/overloads, host types exposed, configuration/backend options and generated public API diff. Shared renderer/input/protocol lifecycle contracts should remain internal; no public WinUI expansion is justified merely for host integration.

The map identifies these potential `InternalsVisibleTo("Uno.UI.Runtime.Skia.Wayland")` access points; they are **proposed, not verified additions**:

| Assembly info file | Internal access to audit |
|---|---|
| `src/Uno.UI/AssemblyInfo.skia.cs` | Platform host builder, XamlRoot host/map, Skia application host, application/window/native factory internals |
| `src/Uno.UWP/AssemblyInfo.skia.cs` | EventLoop, dispatcher overrides, WinRT service extensions |
| `src/Uno.Foundation/AssemblyInfo.cs` | ApiExtensibility/helpers |
| `src/Uno.UI.Dispatching/AssemblyInfo.cs` | Dispatcher priority/hooks |
| `src/Uno.UI.Runtime.Skia/AssemblyInfo.cs` | Shared renderer helpers if used |
| `src/Uno.Foundation.Logging/AssemblyInfo.cs` | Logging internals only if actually needed |
| `src/Uno.UI.Composition/AssemblyInfo.skia.cs` | Conditional audit for actual renderer usage |

TODO: list the declarations actually added and member-level justification, inspect imported/evaluated friend items to avoid duplicates, and verify strong-name conventions. Audit shared Hide/Destroy hooks, which the map notes are currently nonvirtual; any shared change needs regression tests and API compatibility review.

### Packaging and dependencies

Propose **`Uno.WinUI.Runtime.Skia.Wayland`**, distinct from the assembly name, following Skia/X11 TFM, dependency and transitive build-asset conventions. TODO: add the runtime project, upstream nuspec/package manifests, solution/filter and CI inclusion; verify runtime asset selection, restore/publish graph and API diff on the final upstream base. Expected generic desktop targets are net9.0/net10.0 in this fork; final upstream targets remain TODO.

Reuse SkiaSharp and existing shared Linux dependencies. Expected system libraries include libwayland-client, libwayland-egl, EGL/GLES, libxkbcommon and libwayland-cursor where used. TODO: exact SONAMEs, supported architectures, optional-load behavior, distro installation docs and licenses. Software must work without an EGL context. No native system libraries should be bundled without an explicit distribution decision. Avoid copying X11 LibVLC dependencies or reflected add-in discovery.

`wayland/build` adds local feed scripts and a fork workflow expecting the new package alongside rebuilt core/Skia/X11 packages. It intentionally rejects the missing runtime project and has not built/published the real feed. These scripts are development tooling, not proof of upstream release integration; TODO agree which pieces belong upstream. Record real package artifacts, LinuxFiles restore assets, runtime DLL inclusion, clean-cache consumption and trim/NativeAOT results before marking packaging complete.

### Verification actually recorded

The following is evidence reported in branch documentation, not checks rerun during preparation of this PR text. TODO: attach logs, exact source SHAs and test commands; rerun against the integrated diff.

| Evidence | Recorded result | Limits |
|---|---|---|
| Code review — map | X11's 20 registrations inventoried; shared host contracts inspected | Does not prove compositor behavior |
| Compile — map | Scratch net10.0 build on SDK 10.0.112, zero errors; seven skeleton files plus selected real shared sources | Remaining UI/window types and protocol layer stubbed; not runtime assembly build |
| Code review — render | Compared X11/shared EGL behavior; ownership, release and frame invariants reviewed | Host/binding integration outstanding |
| Compile — render | Scratch net10.0 against SkiaSharp 3.119.2/proxy stubs, zero errors | Not the real runtime project; vulnerability lookup unavailable |
| Runtime — render scratch | Scheduler/coalescing/idle/retry/disposal, size rounding, software buffer saturation/reuse/resize retirement passed | Fake protocol events; real libc allocation and Skia painting; no compositor or EGL presentation |
| Build tooling | Shell syntax/ShellCheck, option rejection, dry-run plans, YAML parsing, synthetic aggregate package fixture passed | No real Uno package build, consumer restore or registry publication |

### Tests NOT done / required evidence before submission

No integrated Wayland assembly build, repository suite, real window, EGL compositor presentation, LinuxFiles session, trim/NativeAOT publication or real package-feed consumption is established by the inspected evidence. TODO: record test command, platform/TFM/RID, source SHA, date, pass/fail and logs for each gate:

- [ ] Analyzer-strict Release build and relevant Uno unit/runtime suites; shared/X11 regression tests and public API/package diff.
- [ ] Startup configure/ack-before-buffer, ping responsiveness, configure storms/stale generation rejection, close cancellation then acceptance, repeated create/destroy and compositor disconnect.
- [ ] Multi-window focus/routing and closing in either order; no callbacks after destruction, no leaked FDs/proxies/buffers, no UI/dispatch/EGL deadlocks.
- [ ] EGL and forced software first frame, redraw/idle/animation, hidden-window throttling, held-buffer saturation, live resize and initialization fallback without EGL.
- [ ] Integer-only and fractional scale, odd dimensions, scale 1/1.25/1.5/2, mixed-DPI output migration/removal, pointer hit-testing and independent text scaling.
- [ ] SSD/CSD negotiation including usable CSD-only environment, title bar extension and interactive move/resize with valid serials.
- [ ] Keyboard layouts, modifiers/dead keys/compose/repeat/focus loss; pointer/wheel/frame ordering, touch if included, cursor-shape and themed fallback.
- [ ] IME preedit/commit/selection/UTF-8 offsets and no duplicate text, if included; otherwise explicitly document lack of IME parity.
- [ ] Clipboard copy/cut/paste and file DnD with another app, large async transfers/cancellation, portal parenting, if included; otherwise retain limitations.
- [ ] Automatic/forced selection and X11 fallback, trimmed/NativeAOT sample, clean-cache package consumption and LinuxFiles publish.

### Compositor matrix and screenshots

All rows remain **TODO / not verified in the inspected evidence**. Protocol availability is to be measured on exact versions, not inferred from compositor names.

| Compositor | Version/distro/session | GPU / software | Scale/output setup | Decorations/input/IME/transfers | Result / logs / screenshots |
|---|---|---|---|---|---|
| GNOME / Mutter | TODO | TODO both | TODO integer/fractional/mixed | TODO CSD and available protocols | TODO, not tested |
| KDE / KWin | TODO | TODO both | TODO integer/fractional/mixed | TODO SSD/CSD and protocols | TODO, not tested |
| sway | TODO | TODO both | TODO integer/fractional/mixed | TODO protocols/input | TODO, not tested |
| Hyprland | TODO | TODO both | TODO integer/fractional/mixed | TODO protocols/input | TODO, not tested |
| niri | TODO; xwayland-satellite version for comparison | TODO both | TODO especially motivating fractional setup | TODO protocols/input/LinuxFiles | TODO, not tested |

TODO screenshots: LinuxFiles XWayland/native side-by-side, EGL/software equivalence, CSD/SSD, resize and multiple scales. TODO: link upstream `Screenshots Compare Test Run`, inspect differences and state disposition. Hand-captured Wayland screenshots do not replace that review.

### Known limitations and open decisions

The current pieces are not a usable integrated host. Core ownership/version/rounding/selection contracts above must be reconciled first. Wayland's compositor controls placement and activation: absolute global move, arbitrary raise/focus, guaranteed unminimize, exact decorated outer bounds and reliable minimized-state queries are unavailable in the baseline. Requests and observed configure state must not be conflated.

Ordinary Uno popups render inside the XamlRoot surface; native out-of-surface popups are not included. Global capture/confinement, icons, Vulkan, native WebView/media/airspace embedding, secondary assembly-load-context cleanup and inherited socket support need separate designs. No blanket X11 API/service parity is claimed. CSD, full presenters, input, IME and data transfer status remain TODO until integration evidence replaces the placeholders. Software's current format assumption is little-endian Linux. Renderer EGL requirements and missing optional protocols must be documented to consumers.

Compatibility goal: additive opt-in with existing X11 behavior preserved. TODO: verify binary/API/build compatibility before checking “NO breaking changes”; describe any unavoidable break with migration guidance and discuss it with maintainers.

### Proposed smaller PRs

1. **Public proposal and shared Linux contracts:** agree on scope, host selection and limitations; extract only necessary X11-coupled services, preserve X11 behavior and add regression tests. Establish packaging/API ownership without advertising an unusable runtime.
2. **Protocol tooling and dispatch:** pinned licensed XML, reproducible AOT-safe typed bindings, FD/version/registry tests, normalized startup errors and wakeable bounded display loop. Resolve ownership before building on it.
3. **First software host:** project/package integration, builder/friend access, dispatcher, configure/ack/close, multi-window and usable CSD; nested-compositor smoke tests. Software enables runtime review without GPU dependencies.
4. **Input and scale:** xkbcommon pointer/keyboard/touch/cursor as scoped, integer/fractional/output handling and frame pacing; compositor screenshots, hit-testing and LinuxFiles usability.
5. **EGL acceleration:** context/display lifetime, queue coordination, generation-safe resize, initialization fallback and GPU/software comparison tests.
6. **Desktop services and IME:** clipboard/file DnD, portal parent export/theme/text scale and text-input-v3, potentially separate PRs per service with interoperability evidence.
7. **Optional follow-ups:** Vulkan, native elements/media, advanced popups/pointer/activation/icons and ALC support, each with an independent proposal.

TODO: agree dependency order and exact scope with maintainers. Packaging must accompany the first functional host; fork feed automation can remain outside upstream. Each PR references the public enhancement issue and reports only its own completed behavior.

## PR Checklist ✅

- [ ] 🧪 Added [Runtime tests, UI tests, or a manual test sample](https://github.com/unoplatform/uno/blob/master/doc/articles/uno-development/working-with-the-samples-apps.md) (for bug fixes / features, if applicable)
- [ ] 📚 Docs have been added/updated following the [documentation template](https://github.com/unoplatform/uno/blob/master/doc/.feature-template.md) (for bug fixes / features)
- [ ] 🖼️ Validated PR `Screenshots Compare Test Run` results.
- [ ] ❗ Contains **NO** breaking changes
- [ ] 👀 Reviewed 2 other [open pull requests](https://github.com/unoplatform/uno/pulls) (optional but appreciated!)

All boxes are intentionally unchecked. TODO: add test/sample paths, a published Uno-only feature article, screenshot-run disposition and compatibility evidence before checking them. The last item is optional. If a breaking change is required, replace the compatibility goal with its impact/migration details and obtain maintainer agreement.
