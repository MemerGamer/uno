#!/usr/bin/env bash
set -euo pipefail
base=${1:?Expected resolved base SHA}
tool_dir="$RUNNER_TEMP/commitsar"
mkdir -p "$tool_dir"
curl --fail --location --retry 3 --output "$tool_dir/commitsar.tar.gz" \
	https://github.com/aevea/commitsar/releases/download/v0.20.2/commitsar_0.20.2_linux_amd64.tar.gz
tar -xzf "$tool_dir/commitsar.tar.gz" -C "$tool_dir"
# Uno's config uses origin/master. In this disposable clone, anchor it at the
# selected exclusive base so commitsar reviews exactly the dispatched range.
git update-ref refs/remotes/origin/master "$base"
# Keep Uno's rules, raising only the scan limit to cover the entire matrix.
python3 - "$tool_dir" <<'PY'
from pathlib import Path
import re
import sys
config = Path('build/.commitsar.yml').read_text()
config, count = re.subn(r'^  (?:# )?limit:.*$', '  limit: 256', config, flags=re.MULTILINE)
if not count:
    config = config.replace('commits:\n', 'commits:\n  limit: 256\n', 1)
(Path(sys.argv[1]) / '.commitsar.yml').write_text(config)
PY
export COMMITSAR_CONFIG_PATH="$tool_dir"
"$tool_dir/commitsar"
