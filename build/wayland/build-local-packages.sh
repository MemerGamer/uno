#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
version=6.7.135-wayland.1
tfm=net9.0
feed="$root/artifacts/wayland/feed"
dry_run=false
strict=false
while (($#)); do
	case "$1" in
		--version|--tfm|--feed)
			(($# >= 2)) || { echo "Missing value for $1" >&2; exit 2; }
			case "$1" in
				--version) version=$2 ;;
				--tfm) tfm=$2 ;;
				--feed) feed=$2 ;;
			esac
			shift 2 ;;
		--dry-run) dry_run=true; shift ;;
		--strict) strict=true; shift ;;
		--help) echo "Usage: $0 [--version 6.7.135-wayland.N] [--tfm net9.0|net10.0] [--feed DIR] [--dry-run] [--strict]"; exit 0 ;;
		*) echo "Unknown option: $1" >&2; exit 2 ;;
	esac
done
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+-wayland\.[0-9]+$ ]] || { echo 'Expected X.Y.Z-wayland.N version' >&2; exit 2; }
[[ $tfm == net9.0 || $tfm == net10.0 ]] || { echo 'Only net9.0/net10.0 desktop TFMs are supported' >&2; exit 2; }
feed=$(realpath -m -- "$feed")
run() {
	printf '%q ' "$@"; printf '\n'
	if ! $dry_run; then "$@"; fi
}
# Build only the roots needed by the aggregate nuspec; project references build their dependencies.
projects=(
	Uno.UI.Toolkit/Uno.UI.Toolkit.Reference.csproj
	Uno.UI.FluentTheme/Uno.UI.FluentTheme.Reference.csproj
	Uno.UI.Toolkit/Uno.UI.Toolkit.Skia.csproj
	Uno.UI.FluentTheme/Uno.UI.FluentTheme.Skia.csproj
	Uno.UWP/Uno.Skia.csproj
	Uno.UI.Dispatching/Uno.UI.Dispatching.Skia.csproj
	Uno.Analyzers/Uno.Analyzers.csproj
	Uno.UI.Adapter.Microsoft.Extensions.Logging/Uno.UI.Adapter.Microsoft.Extensions.Logging.csproj
	Uno.UI.Runtime.Skia.X11/Uno.UI.Runtime.Skia.X11.csproj
	Uno.UI.Runtime.Skia.Wayland/Uno.UI.Runtime.Skia.Wayland.csproj
)
if ! $dry_run; then
	command -v dotnet >/dev/null
	command -v python3 >/dev/null
	for project in "${projects[@]}"; do
		[[ -f "$root/src/$project" ]] || { echo "Required project missing: src/$project (integrate the Wayland host first)" >&2; exit 1; }
	done
	mkdir -p -- "$feed"
	[[ ! -e "$feed/Uno.WinUI.$version.nupkg" ]] || { echo 'Use a fresh version/feed; NuGet caches versions immutably' >&2; exit 1; }
fi
# Preserve the assembly version used by released Uno libraries.
props=(-p:UnoTargetFrameworkOverride="$tfm" -p:Configuration=Release
	-p:PackageVersion="$version" -p:InformationalVersion="$version" -p:GeneratePackageOnBuild=false
	-p:UnoNugetOverrideVersion= -p:RepositoryUrl=https://github.com/MemerGamer/uno)
if $strict; then
	props+=(-p:ContinuousIntegrationBuild=true)
else
	props+=(-p:UnoFastDevBuild=true)
fi
# Extra MSBuild switches for constrained machines, e.g. WAYLAND_BUILD_ARGS="-m:2 -nodeReuse:false".
read -ra extra_build_args <<< "${WAYLAND_BUILD_ARGS:-}"
for project in "${projects[@]}"; do
	run dotnet build "$root/src/$project" "${props[@]}" "${extra_build_args[@]}" --verbosity minimal
done
# These projects have normal SDK pack targets, unlike Uno.WinUI/Foundation/WinRT.
for project in Uno.Foundation.Logging/Uno.Foundation.Logging.csproj \
	Uno.UI.Adapter.Microsoft.Extensions.Logging/Uno.UI.Adapter.Microsoft.Extensions.Logging.csproj \
	Uno.UI.Runtime.Skia/Uno.UI.Runtime.Skia.csproj \
	Uno.UI.Runtime.Skia.X11/Uno.UI.Runtime.Skia.X11.csproj \
	Uno.UI.Runtime.Skia.Wayland/Uno.UI.Runtime.Skia.Wayland.csproj; do
	run dotnet pack "$root/src/$project" "${props[@]}" --no-build --no-restore --output "$feed"
done
run python3 "$root/build/wayland/prepare-desktop-nuspecs.py" "$root" "$tfm" "$version" "$feed" 
