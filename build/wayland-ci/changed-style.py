#!/usr/bin/env python3
"""Run Uno's pinned doc tools and XamlStyler on changed, surviving files."""
from pathlib import Path
import subprocess
import sys

base = sys.argv[1]
raw = subprocess.check_output(['git', 'diff', '--name-only', '-z', '--diff-filter=ACMR',
                               base, 'HEAD', '--'])
paths = [Path(name.decode()) for name in raw.split(b'\0') if name]
paths = [path for path in paths if path.is_file()]
markdown = [str(path) for path in paths if path.suffix.lower() == '.md']
spelling = [str(path) for path in paths if path.suffix.lower() == '.md'
            or (str(path).startswith('doc/') and path.name == 'toc.yml')]
xaml = [str(path) for path in paths if path.suffix.lower() == '.xaml']
failed = False


def run(args):
    global failed
    result = subprocess.run(args)
    failed |= result.returncode != 0
    return result.returncode == 0


if markdown or spelling:
    if run(['npm', 'install', '--global', 'markdownlint-cli@0.38.0', 'cspell@8.3.2']):
        if markdown:
            run(['markdownlint', '-c', 'build/.markdownlint.json', '--', *markdown])
        if spelling:
            run(['cspell', '--config', 'build/cSpell.json', '--no-progress', '--', *spelling])
else:
    print('No changed documentation.')
if xaml:
    if run(['dotnet', 'tool', 'restore']):
        for path in xaml:
            # Formatting followed by a drift diff matches xaml-style-check.yml.
            args = ['dotnet', 'xstyler', '-f', path]
            if path.startswith('src/SamplesApp/'):
                args += ['-c', 'src/SamplesApp/Settings.XamlStyler']
            run(args)
        patch = subprocess.check_output(['git', 'diff', '--', *xaml])
        if patch:
            Path('artifacts').mkdir(exist_ok=True)
            Path('artifacts/xaml-style.patch').write_bytes(patch)
            print('XAML Styler drift detected; see the patch artifact.')
            failed = True
else:
    print('No changed XAML.')
sys.exit(int(failed))
