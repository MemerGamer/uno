# Consume the Wayland fork from LinuxFiles

This is a desktop-only development feed for `MemerGamer/uno`, initially based on
Uno 6.7.135 (`da217df231`). It preserves the upstream package IDs. It is not a
replacement for Uno's complete multi-platform release feed. The Wayland host
must be integrated before these packages can be built: this checkout currently
has no `src/Uno.UI.Runtime.Skia.Wayland` project. Runtime examples below describe
the intended `UseWayland()` builder contract and need confirmation against that
implementation.

## Build a local feed

From the fork root, with Bash, Python 3, and .NET SDK 10 installed:

```sh
build/wayland/build-local-packages.sh --dry-run
build/wayland/build-local-packages.sh \
  --version 6.7.135-wayland.1 --tfm net9.0 \
  --feed /absolute/path/to/wayland-feed
```

Use the app's desktop TFM: the default is `net9.0`; `--tfm net10.0` is available.
The repository currently supports both, despite the original 6.7 release base.
SDK 10 is needed for the current toolchain; SDK 9 is also installed in CI. No
Android, Apple, Windows, or WebAssembly workloads are installed. Source
generators/tasks still build for `netstandard2.0`, and logging builds for
`net9.0`, by design. Third-party dependencies are restored from the repository's
NuGet sources; their packages are not copied into this feed.

The script builds explicit project roots and their project references in Release.
It passes `UnoTargetFrameworkOverride` on the command line, without changing the
ignored `src/crosstargeting_override.props`. `--strict` enables CI checks; local
builds use `UnoFastDevBuild`. `UnoNugetOverrideVersion` is explicitly empty so the
build does not overwrite a released package in the global NuGet cache.

Expected packages, all at the chosen version:

- `Uno.WinUI`, `Uno.WinRT`, `Uno.Foundation`, `Uno.Foundation.Logging`.
- `Uno.UI.Adapter.Microsoft.Extensions.Logging`.
- `Uno.WinUI.Runtime.Skia`, `Uno.WinUI.Runtime.Skia.X11`,
  `Uno.WinUI.Runtime.Skia.Wayland`.

`Uno.WinUI` is **not** produced by packing `Uno.UI.Skia.csproj`. Upstream
[`build/Uno.UI.Build.csproj`](../../build/Uno.UI.Build.csproj) runs
`PrepareNuGetPackage` and `BuildNuGetPackage` over aggregate nuspecs in
[`build/nuget`](../../build/nuget). Those expect all platforms, including Windows
PRI assets. The local helper selects the chosen `lib/netX.0` reference assemblies,
`uno-runtime/netX.0/skia` implementations, themes/toolkit, analyzers, source
generators, and build tasks from those same manifests. It preserves matching
buildTransitive assets (retargeting the shared generic net9 imports for a net10
feed), pins rebuilt internal dependencies to the fork version,
and packs temporary manifests with the .NET SDK's NuGet pack task. It fails on
missing selected assets and handles upstream `Bin`/`bin` spelling on Linux.
Tracked nuspecs are never modified.

The upstream `build/filters/Uno.UI-packages-skia.slnf` is much wider: it includes
other desktop hosts, WASM Skia, media/webview add-ins and DevServer. This script
avoids that filter and the all-platform aggregate build. Optional add-ins are
not rebuilt; keep their released pins unless LinuxFiles needs modified APIs.
See also the [build guide](../articles/uno-development/building-uno-ui.md) and
[`crosstargeting_override.props.sample`](../../src/crosstargeting_override.props.sample).

### Time and space budget

These are planning estimates, **not measurements of this script**. The local
AGENTS.md reports about two minutes for a clean fast-mode Skia SamplesApp build
on a 32-core Windows machine with warm packages. This feed additionally builds
reference variants, themes and packaging tools; hosted Linux runners have fewer
cores and a cold restore can dominate. Allow roughly 10–30 minutes warm or
20–60 minutes cold on a 4–8-core machine, with 16 GB RAM preferable. Reserve
15–25 GB of free disk beyond the checkout for SDKs, NuGet cache and bin/obj;
allow 0.2–1 GB for the feed/artifact. These ranges must be replaced with results
from the first successful CI build. CI allows 90 minutes; do not cancel a build
because it exceeds the estimate. Measure with `/usr/bin/time -v` and `du -sh`
on `src`, the NuGet cache, and the feed. Repeated per-project builds are
incremental but still pay MSBuild evaluation costs.

## Switch LinuxFiles to the feed

Keep `global.json`'s `msbuild-sdks` entry `Uno.Sdk: 6.7.30`. A new runtime host
with an explicit PackageReference does not require rebuilding Uno.Sdk or
changing SDK discovery. Do **not** set `UnoVersion` to the prerelease: the SDK
validates its own expected value. Override the actual package pins instead.
This path assumes the existing 6.7.30 SDK can consume the fork's unchanged
package/build layout; app restore/build remains a required integration check.

Add the feed to LinuxFiles' existing `NuGet.Config`, retaining its other sources:

```xml
<configuration>
  <packageSources>
    <add key="wayland-local" value="/absolute/path/to/wayland-feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

If package source mapping is enabled, add the eight package IDs above to the
`wayland-local` mapping. Do not map `Uno.*` exclusively to this feed: Uno.Sdk,
add-ins and other Uno libraries still come from the existing sources.

In `Directory.Packages.props`, replace the existing `Uno.WinUI` and X11 versions
and add the Wayland version. The two core transitive packages are pinned too so
an existing central transitive pin cannot pull a released implementation:

```xml
<PropertyGroup>
  <WaylandUnoVersion>6.7.135-wayland.1</WaylandUnoVersion>
</PropertyGroup>
<ItemGroup>
  <PackageVersion Include="Uno.WinUI" Version="$(WaylandUnoVersion)" />
  <PackageVersion Include="Uno.WinRT" Version="$(WaylandUnoVersion)" />
  <PackageVersion Include="Uno.Foundation" Version="$(WaylandUnoVersion)" />
  <PackageVersion Include="Uno.Foundation.Logging" Version="$(WaylandUnoVersion)" />
  <PackageVersion Include="Uno.WinUI.Runtime.Skia" Version="$(WaylandUnoVersion)" />
  <PackageVersion Include="Uno.WinUI.Runtime.Skia.X11" Version="$(WaylandUnoVersion)" />
  <PackageVersion Include="Uno.WinUI.Runtime.Skia.Wayland" Version="$(WaylandUnoVersion)" />
  <PackageVersion Include="Uno.UI.Adapter.Microsoft.Extensions.Logging" Version="$(WaylandUnoVersion)" />
</ItemGroup>
```

Edit existing entries rather than adding duplicate `Include` entries. Restrict
these fork pins to the Linux desktop project/TFM if LinuxFiles also builds mobile
or WASM. This feed provides only one generic desktop TFM and Skia assets.

In the Linux desktop head, explicitly reference the runtime packages so the
SDK's implicit package resolver respects the supplied central versions:

```xml
<ItemGroup Condition="'$(TargetFramework)' == 'net9.0-desktop'">
  <PackageReference Include="Uno.WinUI.Runtime.Skia.Wayland" />
  <PackageReference Include="Uno.WinUI.Runtime.Skia.X11" />
  <PackageReference Include="Uno.WinUI.Runtime.Skia" />
</ItemGroup>
```

Use LinuxFiles' actual declared desktop TFM in this condition (`net10.0-desktop`
for a .NET 10 single-project app, or `net9.0` in a dedicated generic head). The
package itself uses `net9.0`, not the SDK's synthetic `-desktop` suffix. Existing
SDK-injected MacOS/Win32/FrameBuffer packages can remain released and unused on
Linux. Keep the existing explicit Uno.WinUI reference. If SDK 6.7.30 still
replaces a pin, supply `VersionOverride="$(WaylandUnoVersion)"` on that explicit
PackageReference and inspect restore output before proceeding; broad disabling
of implicit packages requires manually restoring all feature dependencies.

Restore with `dotnet restore --force-evaluate` (update any lock file), then build
the Linux head in Release. Inspect `obj/project.assets.json` to confirm all eight
core/runtime packages resolve to the prerelease and contain `uno-runtime/.../skia`.
Publish for `linux-x64` or `linux-arm64` with LinuxFiles' normal command. Verify the
Wayland runtime DLL reaches the publish directory. Give every rebuild a fresh
`.N`; never replace the contents of an already-consumed version. If rebuilding
both TFMs, use different versions or feeds: two single-TFM packages with the
same ID/version cannot coexist in a NuGet feed/cache.

## Opt in at runtime

Proposed API, pending host integration (namespace for builder extensions follows
X11's `Uno.UI.Hosting`; runtime types belong to `Uno.WinUI.Runtime.Skia.Wayland`):

```csharp
using Uno.UI.Hosting;

var builder = UnoPlatformHostBuilder.Create().App(() => new App());
if (OperatingSystem.IsLinux()
    && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
{
    builder.UseWayland();
}
else
{
    builder.UseX11();
}
builder.Build().Run();
```

Replace the existing unconditional `.UseX11()` call. Explicit branching avoids
an X11 host winning selection when both `DISPLAY` and `WAYLAND_DISPLAY` exist.
`WAYLAND_DISPLAY` is an opt-in signal, not proof that connection succeeds. The
host must define its connection-failure behavior; this example does not silently
fall back to XWayland. Keep an app-specific switch to force X11 for diagnosis.
Environment variables alone cannot switch a compiled app that never registers
the Wayland builder. The package does not start a compositor.

Run within a Wayland desktop session with `XDG_RUNTIME_DIR` set. Runtime native
library requirements must be confirmed by the input/render owners: expect
libwayland-client, libxkbcommon, and EGL/GLES for accelerated rendering; wl_shm
software rendering must not require an EGL context. Do not bundle system native
libraries into NuGet without a separate distribution decision.

Before calling this usable, test LinuxFiles on an actual compositor: connection,
first frame, resize, 100%/fractional scaling, keyboard/pointer, clipboard/file
pickers as implemented, shutdown and X11 fallback. Also publish with trimming
and NativeAOT if LinuxFiles uses them. Packaging and compilation alone do not
prove native Wayland behavior. Existing X11 WebView/media integration may remain
X11-specific; the new package does not automatically port those add-ins.

## CI artifacts and optional GitHub Packages

The fork workflow runs on tags such as `wayland-v6.7.135-wayland.2` or manual
dispatch, building `net9.0` by default and uploading the eight `.nupkg` files as
an Actions artifact. Download/extract the artifact into the local feed directory.
The workflow currently fails early until the Wayland project is integrated.

Set the repository variable `WAYLAND_PUBLISH_PACKAGES=true` to also publish tag
builds to `https://nuget.pkg.github.com/MemerGamer/index.json`. It uses the
workflow's `GITHUB_TOKEN` with `packages:write`; no nuget.org publication occurs.
Packages must be associated with `MemerGamer/uno` and grant this repository write
access if they already exist. Consumers authenticate according to
[GitHub's NuGet registry instructions](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry),
using a classic PAT with `read:packages` where required, supplied via user-level
NuGet configuration or CI secrets. Never commit credentials. Prefer the Actions
artifact for a credential-free local feed. Package publishing has not been run
from this sandbox.

## Attribution and upstream maintenance

Preserve Uno's [Apache-2.0 license](../../License.md), upstream copyright notices,
package license metadata and attribution. Keep MIT/Microsoft headers on ported
WinUI code. Retain the actual copyright/license text of each imported Wayland
protocol XML and generated binding input; scanner output does not erase its
source attribution. Record protocol source revisions with the generator. Review
any redistributed third-party binaries and include their required notices.
The build scripts add no application NuGet dependencies and retain upstream
package IDs/authors while marking the repository URL and prerelease version as
the fork.

Keep Wayland commits isolated from unrelated fork changes. Add the public remote
with `git remote add upstream https://github.com/unoplatform/uno.git` and fetch
its tags/release branches. Select the actual 6.7 release branch from
`git branch -r --list 'upstream/release/*'`; do not assume master matches 6.7.
Rebase the unpublished development branch onto the selected release tip, or
replay the Wayland commits onto a fresh branch based on the new release tag.
Coordinate before rewriting a shared branch; retain published `wayland-v*` tags.
After each update, review nuspec/TFM changes and the dependency graph, rerun the
package build and LinuxFiles compositor tests, and issue a new prerelease
version whose base matches the new upstream release. Keep public specs
self-contained and free of private tracker links.

## Validation recorded for this change

Shell syntax, ShellCheck, option rejection, both desktop dry-run plans, and YAML
parsing were checked. A synthetic asset fixture exercised manifest selection
and produced all three aggregate nupkgs with the SDK pack task, checking the
Skia paths inside the archives. The normal build preflight correctly rejects the
missing Wayland project before starting any build. No real Uno package build,
LinuxFiles restore, compositor runtime test, or registry publication was run.
