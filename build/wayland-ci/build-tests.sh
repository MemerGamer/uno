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
build() { dotnet build "$1" "${props[@]}" --verbosity minimal; }
test_project() {
	local project=$1
	shift
	local project_results
	project_results="$results/$(basename "$project" .csproj)"
	# .NET 10 global.json selects Microsoft.Testing.Platform, as in Uno's CI.
	dotnet test --project "$project" "${props[@]}" "$@" --no-progress \
		--report-trx --results-directory "$project_results"
	find "$project_results" -name '*.trx' -print -quit | grep -q .
}
if [[ -f src/Uno.UI.Runtime.Skia.Wayland/Uno.UI.Runtime.Skia.Wayland.csproj ]]; then
	build src/Uno.UI.Runtime.Skia.Wayland/Uno.UI.Runtime.Skia.Wayland.csproj
	if [[ -f src/Uno.UI.Runtime.Skia.Wayland.Tests/Uno.UI.Runtime.Skia.Wayland.Tests.csproj ]]; then
		test_project src/Uno.UI.Runtime.Skia.Wayland.Tests/Uno.UI.Runtime.Skia.Wayland.Tests.csproj
	fi
	build build/wayland-scanner/Wayland.Scanner.csproj
	test_project build/wayland-scanner/tests/Wayland.Scanner.Tests.csproj \
		-p:DirectoryBuildTargetsPath="$tool_dir/host-tests.targets"
elif [[ -f src/Uno.UI.Runtime.Wayland/Uno.UI.Runtime.Wayland.csproj ]]; then
	build src/Uno.UI.Runtime.Wayland.Scanner/Uno.UI.Runtime.Wayland.Scanner.csproj
	build src/Uno.UI.Runtime.Wayland/Uno.UI.Runtime.Wayland.csproj
	if [[ -f src/Uno.UI.Runtime.Wayland.Tests/Uno.UI.Runtime.Wayland.Tests.csproj ]]; then
		test_project src/Uno.UI.Runtime.Wayland.Tests/Uno.UI.Runtime.Wayland.Tests.csproj
	elif [[ $mode == commit ]]; then
		echo 'Wayland tests do not exist at this commit; running Uno.UI.UnitTests.' | tee -a "${GITHUB_STEP_SUMMARY:-/dev/null}"
		test_project src/Uno.UI.UnitTests/Uno.UI.UnitTests.csproj
	else
		echo 'Wayland unit test project missing' >&2
		exit 1
	fi
elif [[ $mode == commit ]]; then
	echo 'This commit predates Wayland; building X11 and running Uno.UI.UnitTests.' | tee -a "${GITHUB_STEP_SUMMARY:-/dev/null}"
	build src/Uno.UI.Runtime.X11/Uno.UI.Runtime.X11.csproj
	test_project src/Uno.UI.UnitTests/Uno.UI.UnitTests.csproj
else
	echo 'No Wayland project in selected ref' >&2
	exit 1
fi
