#!/usr/bin/env python3
"""Run Uno's doc validations and XAML Styler check with Uno CI's exact scope."""
from pathlib import Path
import re
import subprocess
import sys

sys.stdout.reconfigure(line_buffering=True)
base = sys.argv[1]
changed = subprocess.check_output(['git', 'diff', '--name-only', base, 'HEAD', '--'],
                                  text=True).splitlines()
# build/ci/setup/.azure-devops-setup-doc-validations.yml's DocChangesDetected.
doc_pattern = re.compile(r'^(doc/|README|README\.md|README\.rst|.*\.md$|.*\.mdx$|.*\.markdown$'
                         r'|doc/.*\.yml$|doc/.*\.yaml$)')
docs = [path for path in changed if doc_pattern.match(path)]
# .github/workflows/xaml-style-check.yml's pull_request paths.
xaml_pattern = re.compile(r'^(src/SamplesApp/.*\.xaml|src/SamplesApp/Settings\.XamlStyler'
                          r'|\.config/dotnet-tools\.json|\.github/workflows/xaml-style-check\.yml)$')
xaml = [path for path in changed if xaml_pattern.match(path)]
failed = False


def run(args):
    global failed
    result = subprocess.run(args)
    failed |= result.returncode != 0
    return result.returncode == 0


if docs:
    print('Documentation-related changes detected:\n' + '\n'.join(docs))
    # Uno validates the whole doc tree, not only changed files.
    if run(['npm', 'install', '--global', 'markdownlint-cli@0.38.0', 'cspell@8.3.2']):
        run(['cspell', '--config', './build/cSpell.json', 'doc/**/*.md', 'doc/**/toc.yml',
             '--no-progress'])
        run(['markdownlint', '-c', 'build/.markdownlint.json', 'doc/**/*.md'])
else:
    print('No documentation changes detected; skipping doc validations.')
if xaml:
    print('SamplesApp XAML changes detected:\n' + '\n'.join(xaml))
    if run(['dotnet', 'tool', 'restore']) and run(['dotnet', 'xstyler', '-d', 'src/SamplesApp', '-r']):
        if subprocess.check_output(['git', 'status', '--porcelain', '--', 'src/SamplesApp']):
            patch = subprocess.check_output(['git', 'diff', '--', 'src/SamplesApp'])
            Path('artifacts').mkdir(exist_ok=True)
            Path('artifacts/xaml-style.patch').write_bytes(patch)
            print('::warning::XAML Styler drift detected; see the patch artifact.')
            failed = True
        else:
            print('No drift detected.')
else:
    print('No SamplesApp XAML changes; skipping XAML Styler.')
sys.exit(int(failed))
