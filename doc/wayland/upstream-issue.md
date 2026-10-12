# Upstream enhancement issue / discussion draft

Proposed title: **Add a native Wayland host for Linux Skia desktop applications**.

Draft only. TODO: search existing upstream issues/discussions, choose an existing tracker if appropriate, confirm maintainer interest and record the resulting public issue URL/number in `upstream-pr.md`. Nothing has been posted. The issue body below follows [.github/ISSUE_TEMPLATE/enhancement.yml](../../.github/ISSUE_TEMPLATE/enhancement.yml); the description is its required field.

## What would you like to be added? 💡

A native Wayland desktop host for Uno's Skia renderer, packaged as `Uno.WinUI.Runtime.Skia.Wayland`, with an opt-in `UseWayland()` builder extension. This would connect directly to the compositor rather than use the X11 host through XWayland. It would retain Uno's WinUI-facing UI behavior and use the existing Skia rendering layer.

The initial target is a functional software/EGL window with correct configure/ack/close lifecycle, per-window scale, multiple windows, keyboard/pointer input, cursor fallback and usable client-side decorations where server-side decorations are unavailable. Existing X11 applications should retain their behavior. Ordered `.UseWayland().UseX11()` registration is proposed for automatic fallback; X11 must be explicitly available to the app. Startup fallback is distinct from migrating live windows after disconnect, which is not proposed.

Clipboard/file DnD, portal parenting and full compositor-mediated IME are important desktop follow-ups with explicit compatibility tests. Vulkan and native WebView/media embedding are separate proposals. TODO: agree the minimum supported subset and review order before treating this as a complete feature commitment.

## Why is this needed? 🤔

There is no native Wayland host in the inspected Uno 6.7.135-based fork baseline. Linux desktop applications therefore use X11/XWayland. On configurations without suitable XWayland scaling, compositor upscaling can blur application text and UI, especially at fractional display scales. The motivating setup is niri with xwayland-satellite and the downstream scenario is [LinuxFiles issue #113](https://github.com/MemerGamer/LinuxFiles/issues/113).

That scenario/link was supplied by the author; the issue contents could not be fetched while drafting. TODO: confirm its details and add a minimal reproduction, exact Uno/app version, distro, GPU, compositor/satellite versions and output scale. This is not a claim that all XWayland compositors are blurry.

Native Wayland would allow rasterization at the compositor's preferred scale while preserving logical layout/pointer coordinates. TODO: attach matching XWayland/native screenshots at 100%, 125%, 150% and 200% after a real host works; sharpness is currently a goal, not a measured result.

## Renderer 🎨

- [x] Skia
- [ ] Native

“Native Wayland” refers to the desktop transport; it does not mean Uno's Native UI renderer.

## For which platforms 📱💻🖥️

**Linux desktop, Wayland sessions.** The current form does not offer Wayland. TODO: select the closest Linux desktop option if the form requires a choice, and explicitly explain that Desktop (X11) describes the existing transport/fallback rather than the requested native Wayland target. Do not select all platforms.

## Anything else we need to know? 💬

### Architecture proposed for discussion

- One display per host with a wakeable dispatch owner and separate Uno UI scheduling; no synchronous dispatch wait on UI/rendering. Typed generated bindings from pinned licensed XML, bounded probing, negotiated versions and explicit disconnect/error cleanup.
- Required `wl_compositor`, `xdg_wm_base` and `wl_shm` v1 baseline. Optional viewporter/fractional-scale, decoration and cursor-shape protocols. Exact generated maximum versions and request gates remain TODO pending protocol integration.
- EGL/GLES over `wl_egl_window` plus a GPU-independent `wl_shm` Skia fallback. Buffer-release ownership and frame callbacks prevent unsafe reuse and idle spinning. Fractional scale uses preferred numerator/120 plus viewport; integer fallback remains available.
- Compositor `wl_seat` input with xkbcommon keyboard mapping/compose/repeat, logical pointer coordinates and per-seat serial/focus routing. No direct libinput or `/dev/input` consumption.
- text-input-v3 IME is planned, not established as supported. Clipboard/DnD and portal parent export are also unverified follow-ups; no X11 service parity is claimed.
- New additive builder/package API and minimal internal friend access; avoid promoting WinUI internals to public just to compile a host.

Wayland cannot offer baseline absolute desktop placement, arbitrary force-focus, reliable minimized state or X11 window reparenting. Ordinary Uno popups are in-surface; native overflow popups and native elements need separate work. TODO: resolve actual host contracts, rounding, render/dispatch proxy ownership and forced-selection behavior before proposing implementation for merge.

### Current development and verification status

Development is in the public MemerGamer/uno fork on `wayland/map`, `wayland/render`, `wayland/build`, and in-progress `wayland/proto`/`wayland/input`, originally based on 6.7.135. TODO: attach public design links with stable revisions when available. This issue is self-contained and does not depend on private tracking.

The map skeleton has limited scratch compilation; render has scratch compilation and fake-protocol scheduling/buffer tests using real libc/Skia software allocation; build tooling has syntax/dry-run/synthetic-package checks. Those records do **not** establish an integrated assembly, real compositor window, EGL presentation, LinuxFiles runtime, real package consumption, trimming or NativeAOT. No tested compositor is claimed yet.

TODO compositor matrix: **GNOME/Mutter, KDE/KWin, sway, Hyprland and niri**, each with exact versions, GPU/software results, scale/output setups, decorations/input/IME/transfer results, logs and screenshots. Include a compositor path without fractional-scale/viewporter and mixed-scale output migration. Core lifecycle, close cancellation, multi-window focus, disconnect, startup fallback and X11 regressions also need tests.

### AI-assistance disclosure

> The code was written with AI coding agents, **OpenAI Codex and Anthropic Claude**, under my direction, and I reviewed it. I take responsibility for the contribution and its verification. Current recorded checks are source review, limited stub-backed compilation, render scratch tests and packaging-tool checks; no integrated real-compositor success is claimed. TODO: add reviewed commit ranges and exact logs/results as integration proceeds.

### Suggested review split and maintainer decisions

Propose separate PRs for (1) agreed design/shared Linux contracts, (2) pinned bindings and dispatch, (3) functional software host/package/lifecycle/CSD, (4) input/scaling, (5) EGL, and (6) desktop services/IME. Optional advanced features would follow independently. Every implementation PR would use the upstream template and reference this enhancement issue.

Maintainer discussion points:

1. Is this host aligned with Uno's Linux roadmap, and what initial support/maintenance bar should it meet?
2. Is additive `UseWayland().UseX11()` appropriate, or should a separate Linux convenience builder own registration order? Should `UNO_SKIA_HOST=auto|wayland|x11` be a shared policy?
3. Which protocol versions/compositor versions, architectures and CSD behavior are the initial support contract? How should unavailable window operations report limitations?
4. Where should shared Linux portal/theme/input helpers and package/CI integration live? What minimum runtime, trim/NativeAOT and interoperability evidence is required?
5. Are there current CLA or AI-assisted contribution expectations beyond the checked-out contribution rules?

### Optional discussion introduction

If maintainers prefer discussion before an enhancement issue, use this introduction followed by the proposal above:

“I would like to discuss contributing a native Wayland Skia host for Linux desktop. The motivating case is fractional-scale sharpness in LinuxFiles on niri with xwayland-satellite. I have early design/render/build pieces in a 6.7.135-based fork, developed with disclosed Codex/Claude assistance and reviewed by me, but no integrated compositor validation yet. Could we agree on scope, ownership, host-selection policy and a staged review/test plan before I prepare implementation PRs? Clipboard/DnD and full IME would remain explicit follow-up scope unless completed and verified.”
