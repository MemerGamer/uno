# Wayland fork CI

The fork's default branch is `wayland/host`; `master` remains an upstream mirror.
All jobs use public `ubuntu-latest` runners. Only the release job needs a writable
`GITHUB_TOKEN`; there are no custom secrets or GitHub Packages credentials.
Actions are SHA-pinned with version comments. NuGet caches include the source
lock files, props, targets, project files and SDK version. Jobs check out a frozen
source SHA separately from helpers at the workflow revision.

| Workflow | Source | Checks / output |
| --- | --- | --- |
| `wayland-ci.yml` | Pushes / PRs targeting `wayland/**`, or dispatch `ref` | Strict Release Wayland build, unit test TRX; port also runs Weston and Xvfb, uploads NUnit XML / logs, and compares failures |
| `wayland-pr-commits.yml` | Dispatch `base..head` (exclusive base) | Independent strict Release build per commit (Wayland + the desktop projects its paths feed) and Wayland tests, commitsar 0.20.2, Uno's `doc/` cspell 8.3.2 / markdownlint 0.38.0, `src/SamplesApp` XAML Styler drift patch |
| `wayland-packages.yml` | `uno-wayland-6.7.135-wayland.N` tag, or dispatch `ref` / `version` | Eight net10.0 host packages, eight-line `SHA256SUMS`, artifact and draft GitHub prerelease |

Host / port detection uses project paths, not branch names. The host currently
sets `NetCurrent=net10.0`; CI follows that property (net9.0 is also supported).
Its tests live in `build/wayland-scanner/tests`; CI additionally runs
`Uno.UI.Runtime.Skia.Wayland.Tests` if present. Port builds use
`UnoTargetFrameworkOverride=net10.0` and `NetUnitTests=net10.0`.
Per-commit jobs build the Wayland line when the commit has it and run its tests
when the test project exists. Changed paths select further strict builds:
`Uno.UI.Runtime.X11` for X11 / `Linux.Shared` and for any other `src/` project
(shared Uno.UI, WinRT and Skia layers are proven through that desktop consumer,
and a same-named `src/<Name>/<Name>.csproj` is built too), and the SamplesApp
desktop head for `src/SamplesApp` / `Uno.UI.RuntimeTests`. `Uno.UI.UnitTests`
is not run: Uno's CI runs it on Windows only, and on Linux it fails on missing
`libHarfBuzzSharp` regardless of the commit. No later source is injected.
The matrix covers at most 256 commits and runs two concurrently.

The style job mirrors Uno's own scopes rather than linting every changed file:
commit messages via commitsar; when a change matches Uno's doc-change detection
(`doc/`, any `*.md`, `doc/*.yml`), cspell `doc/**/*.md doc/**/toc.yml` and
markdownlint `doc/**/*.md`; when `src/SamplesApp` XAML, its `Settings.XamlStyler`
or `.config/dotnet-tools.json` change, `dotnet xstyler -d src/SamplesApp -r` with
a drift patch. Root files such as `THIRD-PARTY-NOTICES.md` are outside both scopes.

The runtime jobs use Uno's desktop SamplesApp head and invoke
`build/test-scripts/linux-skia-runtime-tests.sh false true` (Weston) and
`true false` (Xvfb), matching the port's Azure desktop Linux template. Weston
uses `UNO_WAYLAND_RENDERER=Software` and includes the harness's lifecycle smoke.
Both hosts receive the same base64-encoded filter. The default covers Wayland,
windowing, text input, clipboard and drag-view classes. Dispatch `filter` accepts
class / method substrings separated by `|`; `full_suite=true` clears the filter.
Full-suite dispatches run a host × group matrix: six groups per host (0–5), twelve
runtime jobs total. Filtered dispatches and automatic push / PR runs keep one
group per host (0 of 1). Both paths pass `UITEST_RUNTIME_TEST_GROUP` and
`UITEST_RUNTIME_TEST_GROUP_COUNT` to Uno's runner. Groups use the same frozen
source SHA and SamplesApp artifact, with `fail-fast: false` so one failure does
not cancel the other groups. Each job has a 300-minute budget; the harness step
has 270 minutes, leaving time for result gating and artifact upload.

Every group fails on missing / empty results or failed tests and uploads
`wayland-runtime-HOST-group-N`, containing its group-labelled NUnit XML and
`uitests-failure-results/` logs. Shell stdout / stderr is captured for both hosts;
Wayland also retains the app's live log, including when it never reaches the
harness's final log dump. The summary merges all expected groups per host before
listing Wayland-only failures with their X11 outcome, shared failures, X11-only
failures and coverage gaps. Duplicate cases preserve any failure. X11 skips /
missing cases are not presented as passing baseline evidence.

Missing XML is reported by host and group as crashed / incomplete, with the last
`Running test` line from that group's logs. Empty or malformed XML is reported
alongside missing XML. If a group never reaches the harness or its artifact was
not uploaded, the summary explicitly says no last-test line is available. It
still compares available results but marks host-only failures and coverage gaps
as provisional and fails the summary for incomplete groups. A missing XML alone
does not distinguish an app crash from a timeout or an earlier setup failure.

## Dispatch

Run workflows from `wayland/host`, choosing the **input ref** for source:

```sh
gh workflow run wayland-ci.yml --ref wayland/host -f ref=wayland/port
gh workflow run wayland-ci.yml --ref wayland/host -f ref=wayland/pr \
  -f filter='Given_Wayland|Given_AppWindow|Given_Clipboard'
gh workflow run wayland-ci.yml --ref wayland/host -f ref=wayland/port -F full_suite=true
gh workflow run wayland-pr-commits.yml --ref wayland/host \
  -f base=56e430f5c5 -f head=wayland/pr
gh workflow run wayland-packages.yml --ref wayland/host \
  -f ref=wayland/host -f version=6.7.135-wayland.5
```

For automatic push / PR runs, GitHub reads workflows from the pushed branch / PR
merge tree. Carry `wayland-ci.yml` and `build/wayland-ci/` on the maintained lines;
putting them only on the default branch does not activate other branches' push
runs. Dispatch works from the default branch against old refs without CI files.
Package tag runs likewise require the workflow / helpers in the tagged source.

## Package publication

Only source reachable from `wayland/host` may produce these packages. Packaging
uses the existing `build/wayland/build-local-packages.sh --strict --tfm net10.0`,
checks IDs, versions, fork dependencies, license metadata and desktop assemblies,
and creates `uno-wayland-VERSION` with the eight case-sensitive nupkg filenames
and `SHA256SUMS`. Existing releases (including drafts) are rejected, and an
existing tag must point at the selected source commit. Releases never use NuGet
or a rolling tag.

The draft preserves the LinuxFiles contract's order: build once, review the
artifact and hashes, commit the eight matching LinuxFiles pins, then **publish
the existing draft**. Its nine assets become anonymously downloadable at
`https://github.com/MemerGamer/uno/releases/download/uno-wayland-VERSION/ASSET`.
Do not rerun packaging between pinning and publication or replace published
assets. Revision `.4` already has local-build pins; publish that original build
for `.4`, or use a fresh revision and review new pins. CI defaults to `.5`.

## First run

Set the fork's default branch to `wayland/host`, enable Actions on the fork, and
allow the pinned GitHub actions and the release job's `contents: write` token.
Land these files on the maintained branches as described above; leave `master`
unchanged. Check the hosted Linux restore / strict-build behavior, TRX reporter,
SamplesApp output directory, Weston lifecycle smoke, filter coverage and Xvfb
comparison on the first run. For a full-suite dispatch, verify twelve runtime
jobs, groups 0–5 of 6 in the app logs on each host, twelve separately named
artifacts, and merged case counts consistent with the full suite. Grouping is
Uno's deterministic method partition, so duration may be uneven; check the
slowest group fits the 270-minute harness / 300-minute job budgets. For a filtered
run, verify two runtime jobs and unchanged filter coverage with group 0 of 1.
On any crashed / timed-out group, verify the always-run upload preserves the
app / harness log and the summary names the group and its last running test.
After reviewing packages, commit LinuxFiles pins and publish the draft; verify
an anonymous fetch and clean LinuxFiles restore against those pins.
