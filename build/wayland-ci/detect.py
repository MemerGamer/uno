#!/usr/bin/env python3
"""Detect the checked-out source line, independently of the workflow branch."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

host = Path('src/Uno.UI.Runtime.Skia.Wayland/Uno.UI.Runtime.Skia.Wayland.csproj').is_file()
port = Path('src/Uno.UI.Runtime.Wayland/Uno.UI.Runtime.Wayland.csproj').is_file()
if host == port:
    raise SystemExit('Expected exactly one Wayland project in the selected ref')
tfm = 'net10.0'
if host:
    tfm = ET.parse('Directory.Build.props').getroot().findtext('.//NetCurrent')
if not tfm or not re.fullmatch(r'net(?:9|10)\.0', tfm):
    raise SystemExit(f'Unsupported desktop framework: {tfm!r}')
print(f'line={"host" if host else "port"}')
print(f'tfm={tfm}')
