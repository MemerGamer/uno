#!/usr/bin/env python3
"""Pin the disposable checkout to the installed .NET 10 SDK, preserving MTP/SDKs."""
import json
from pathlib import Path
import re
import subprocess

versions = re.findall(r'^(10\.0\.\d+) \[', subprocess.check_output(
    ['dotnet', '--list-sdks'], text=True), re.MULTILINE)
if not versions:
    raise SystemExit('setup-dotnet must install a .NET 10 SDK first')
version = max(versions, key=lambda value: tuple(map(int, value.split('.'))))
path = Path('global.json')
config = json.loads(path.read_text(encoding='utf-8-sig'))
config.setdefault('sdk', {}).update(version=version, rollForward='disable', allowPrerelease=False)
path.write_text(json.dumps(config, indent=2) + '\n')
print(f'Using .NET SDK {version}')
