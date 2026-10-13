#!/usr/bin/env python3
"""Validate LinuxFiles' eight-package release contract and write SHA256SUMS."""
import hashlib
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET
import zipfile

IDS = ('Uno.WinUI', 'Uno.WinRT', 'Uno.Foundation', 'Uno.Foundation.Logging',
       'Uno.UI.Adapter.Microsoft.Extensions.Logging', 'Uno.WinUI.Runtime.Skia',
       'Uno.WinUI.Runtime.Skia.X11', 'Uno.WinUI.Runtime.Skia.Wayland')


def verify(feed, version):
    if not re.fullmatch(r'6\.7\.135-wayland\.[0-9]+', version):
        raise ValueError('This host line releases 6.7.135-wayland.N only')
    expected = {f'{name}.{version}.nupkg' for name in IDS}
    actual = {path.name for path in feed.glob('*.nupkg')}
    if actual != expected:
        raise ValueError(f'Package set: missing={expected - actual}, extra={actual - expected}')
    for name in IDS:
        with zipfile.ZipFile(feed / f'{name}.{version}.nupkg') as package:
            nuspecs = [entry for entry in package.namelist() if entry.endswith('.nuspec')]
            if len(nuspecs) != 1:
                raise ValueError(f'{name}: expected one nuspec')
            root = ET.fromstring(package.read(nuspecs[0]))
            metadata = next(child for child in root if child.tag.rsplit('}', 1)[-1] == 'metadata')
            fields = {child.tag.rsplit('}', 1)[-1]: child.text for child in metadata}
            if fields.get('id') != name or fields.get('version') != version:
                raise ValueError(f'{name}: incorrect package identity/version')
            if not (fields.get('license') or fields.get('licenseUrl')):
                raise ValueError(f'{name}: missing upstream license metadata')
            for dependency in root.iter():
                if dependency.tag.rsplit('}', 1)[-1] == 'dependency' and dependency.get('id') in IDS:
                    if dependency.get('version') not in (version, f'[{version}]'):
                        raise ValueError(f'{name}: fork dependency has a different version')
            if name not in ('Uno.Foundation.Logging', 'Uno.UI.Adapter.Microsoft.Extensions.Logging'):
                if not any(entry.startswith(('lib/net10.0/', 'uno-runtime/net10.0/'))
                           and entry.endswith('.dll') for entry in package.namelist()):
                    raise ValueError(f'{name}: missing net10.0 desktop assemblies')
    lines = []
    for name in sorted(expected):
        with (feed / name).open('rb') as stream:
            lines.append(f'{hashlib.file_digest(stream, "sha256").hexdigest()}  {name}\n')
    manifest = ''.join(lines)
    (feed / 'SHA256SUMS').write_text(manifest, encoding='ascii', newline='\n')
    print(manifest, end='')


if __name__ == '__main__':
    verify(Path(sys.argv[1]), sys.argv[2])
