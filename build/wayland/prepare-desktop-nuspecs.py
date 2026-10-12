#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
"""Select upstream aggregate package assets, then use the SDK's NuGet pack task.

No tracked nuspec is mutated. Resolve Windows-style paths on case-sensitive Linux.
Fail on missing selected assets rather than publishing an incomplete package.
"""
import glob
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

NS = 'http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd'
ET.register_namespace('', NS)


def tag(name):
    return f'{{{NS}}}{name}'


def selected(target, tfm):
    parts = target.replace('\\', '/').strip('/').split('/')
    if parts[0] == 'uno-runtime':
        return parts[1:] == [tfm, 'skia']
    if parts[0] == 'lib':
        return parts[1:] == [tfm]
    if parts[0] == 'buildTransitive' and len(parts) > 1 and parts[1].startswith(('net', 'uap')):
        return parts[1] == tfm
    return parts[0] not in ('lib', 'uno-runtime')


def resolve_pattern(base, source):
    # Bin/bin is inconsistent in upstream nuspecs. Match each literal component
    # case-insensitively while leaving glob patterns for the NuGet pack task.
    path = base
    for part in source.replace('\\', '/').split('/'):
        if part == '..':
            path = path.parent
        elif any(c in part for c in '*?['):
            path = path / part
        else:
            matches = [p for p in path.iterdir() if p.name.lower() == part.lower()] if path.is_dir() else []
            path = matches[0] if len(matches) == 1 else path / part
    return str(path)


def prepare(root, tfm, version, output):
    base = root / 'build/nuget'
    for name in ('Uno.WinUI', 'Uno.Foundation', 'Uno.WinRT'):
        tree = ET.parse(base / f'{name}.nuspec')
        metadata = tree.getroot().find(tag('metadata'))
        metadata.find(tag('version')).text = version
        repository = metadata.find(tag('repository'))
        repository.set('url', 'https://github.com/MemerGamer/uno')
        repository.set('branch', subprocess.check_output(['git', 'rev-parse', '--abbrev-ref', 'HEAD'], cwd=root, text=True).strip())
        repository.set('commit', subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip())
        for container in metadata:
            for group in list(container):
                if group.tag == tag('group') and group.get('targetFramework') != tfm:
                    container.remove(group)
        for dependency in metadata.iter(tag('dependency')):
            if dependency.get('id') in ('Uno.WinUI', 'Uno.Foundation', 'Uno.WinRT', 'Uno.Foundation.Logging'):
                dependency.set('version', f'[{version}]')
        files = tree.getroot().find(tag('files'))
        for item in list(files):
            target = item.get('target').replace('\\', '/')
            # Upstream shares the generic net9 imports with net10 consumers.
            if tfm == 'net10.0' and target == 'buildTransitive/net9.0':
                target = f'buildTransitive/{tfm}'
            if not selected(target, tfm):
                files.remove(item)
                continue
            # The Windows-specific generator is not needed for a Linux head.
            if 'Uno.UI.SourceGenerators.WinAppSDK' in item.get('src'):
                files.remove(item)
                continue
            source = resolve_pattern(base, item.get('src'))
            if not glob.glob(source, recursive=True):
                raise RuntimeError(f'Missing package asset: {source}')
            item.set('src', source)
            item.set('target', target)
        tree.write(output / f'{name}.nuspec', encoding='utf-8', xml_declaration=True)


def main():
    root, tfm, version, feed = sys.argv[1:]
    with tempfile.TemporaryDirectory(prefix='uno-wayland-pack-') as scratch:
        output = Path(scratch)
        prepare(Path(root), tfm, version, output)
        # Outside the repo so its Directory.Build.* imports cannot affect this pack-only project.
        project = output / 'Pack.csproj'
        project.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>{tfm}</TargetFramework><EnableDefaultItems>false</EnableDefaultItems><IncludeBuildOutput>false</IncludeBuildOutput><NuGetAudit>false</NuGetAudit><EnableTargetingPackDownload>false</EnableTargetingPackDownload></PropertyGroup></Project>')
        subprocess.run(['dotnet', 'restore', str(project)], check=True)
        for name in ('Uno.WinUI', 'Uno.Foundation', 'Uno.WinRT'):
            subprocess.run(['dotnet', 'pack', str(project), '--no-build', '--no-restore',
                            f'-p:NuspecFile={output / (name + ".nuspec")}',
                            '--output', feed], check=True)


if __name__ == '__main__':
    main()
