#!/usr/bin/env bash
set -euo pipefail
# Run from the source checkout; tooling is kept outside it so old commits work.
mode=${1:?Expected host, port or commit}
tfm=${2:-net10.0}
tool_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
results="$PWD/artifacts/wayland-unit-tests"
mkdir -p "$results"
props=(-c Release -p:UnoTargetFrameworkOverride="$tfm" -p:NetUnitTests="$tfm"
	-p:ContinuousIntegrationBuild=true -p:UnoFastDevBuild=false
	-p:TreatWarningsAsErrors=true -p:GeneratePackageOnBuild=false)
note() { echo "$*" | tee -a "${GITHUB_STEP_SUMMARY:-/dev/null}"; }
build() { dotnet build "$1" "${props[@]}" --verbosity minimal; }
test_project() {
	local project=$1
	shift
	local project_results
	project_results="$results/$(basename "$project" .csproj)"
	# .NET 10 global.json selects Microsoft.Testing.Platform, as in Uno's CI.
	dotnet test --project "$project" "${props[@]}" "$@" --no-progress \
		--report-trx --results-directory "$project_results"
	[[ -n $(find "$project_results" -name '*.trx' -print -quit) ]]
}
# The same SamplesApp desktop head and flags as the wayland-ci runtime-build job.
build_samples() {
	dotnet build src/SamplesApp/SamplesApp/SamplesApp.csproj -c Release \
		-p:UnoTargetFrameworkOverride="$tfm-desktop" -f "$tfm-desktop" \
		-p:UNO_DISABLE_ANALYZERS_IN_SAMPLES=true -p:GeneratePackageOnBuild=false \
		-p:ContinuousIntegrationBuild=true -p:UnoFastDevBuild=false --verbosity minimal \
		-p:NuGetAudit=false  # upstream dev-tool dependencies carry fresh advisories; not part of this PR
}
host_project=src/Uno.UI.Runtime.Skia.Wayland/Uno.UI.Runtime.Skia.Wayland.csproj
port_project=src/Uno.UI.Runtime.Wayland/Uno.UI.Runtime.Wayland.csproj
x11_project=src/Uno.UI.Runtime.X11/Uno.UI.Runtime.X11.csproj
# Builds whichever Wayland line the checkout has and runs its tests, if any.
wayland() {
	if [[ -f $host_project ]]; then
		build "$host_project"
		if [[ -f src/Uno.UI.Runtime.Skia.Wayland.Tests/Uno.UI.Runtime.Skia.Wayland.Tests.csproj ]]; then
			test_project src/Uno.UI.Runtime.Skia.Wayland.Tests/Uno.UI.Runtime.Skia.Wayland.Tests.csproj
		fi
		build build/wayland-scanner/Wayland.Scanner.csproj
		test_project build/wayland-scanner/tests/Wayland.Scanner.Tests.csproj \
			-p:DirectoryBuildTargetsPath="$tool_dir/host-tests.targets"
	elif [[ -f $port_project ]]; then
		build src/Uno.UI.Runtime.Wayland.Scanner/Uno.UI.Runtime.Wayland.Scanner.csproj
		build "$port_project"
		if [[ -f src/Uno.UI.Runtime.Wayland.Tests/Uno.UI.Runtime.Wayland.Tests.csproj ]]; then
			test_project src/Uno.UI.Runtime.Wayland.Tests/Uno.UI.Runtime.Wayland.Tests.csproj
		elif [[ $mode == commit ]]; then
			note 'Wayland tests do not exist at this commit; the strict build is the check.'
		else
			echo 'Wayland unit test project missing' >&2
			exit 1
		fi
	fi
}
has_wayland() { [[ -f $host_project || -f $port_project ]]; }
if [[ $mode != commit ]]; then
	has_wayland || { echo 'No Wayland project in selected ref' >&2; exit 1; }
	wayland
	exit 0
fi
# Commit mode proves one commit builds strictly: the Wayland line when it
# exists, plus the desktop projects its changed paths feed into. Uno runs
# Uno.UI.UnitTests on Windows only, so it is not an absolute gate here.
x11=false
samples=false
declare -A projects=()
while IFS= read -r path; do
	case $path in
		src/Uno.UI.Runtime.Wayland*/* | src/Uno.UI.Runtime.Skia.Wayland*/* | build/wayland-scanner/*) ;;
		src/Uno.UI.Runtime.X11/* | src/Uno.UI.Runtime.Linux.Shared/*) x11=true ;;
		src/SamplesApp/* | src/Uno.UI.RuntimeTests/*) samples=true ;;
		src/*/*)
			name=${path#src/}
			name=${name%%/*}
			# Shared Uno.UI / WinRT / Skia layers are proven through their desktop consumer.
			x11=true
			[[ $name == *Tests* || ! -f src/$name/$name.csproj ]] || projects[src/$name/$name.csproj]=1
			;;
	esac
done < <(git diff --name-only HEAD^ HEAD --)
if has_wayland; then
	wayland
else
	note 'This commit predates the Wayland project.'
fi
for project in "${!projects[@]}"; do
	note "Building changed project $project"
	build "$project"
done
if [[ $x11 == true ]]; then
	note 'Building the X11 desktop consumer of the changed sources'
	build "$x11_project"
fi
if [[ $samples == true ]]; then
	note 'Building the SamplesApp desktop head for changed samples / runtime tests'
	build_samples
fi
if [[ $x11 == false && $samples == false && ${#projects[@]} -eq 0 ]] && ! has_wayland; then
	note 'No buildable source changed in this commit.'
fi
