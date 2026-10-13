#!/usr/bin/env python3
"""Freeze a dispatch range and emit a GitHub Actions matrix for base..head."""
import json
import os
import subprocess


def git(*args):
    return subprocess.check_output(['git', *args], text=True).strip()


def resolve(ref):
    for candidate in (ref, f'refs/remotes/origin/{ref}'):
        result = subprocess.run(['git', 'rev-parse', '--verify', '--end-of-options',
                                 f'{candidate}^{{commit}}'], text=True, capture_output=True)
        if result.returncode == 0:
            return result.stdout.strip()
    raise SystemExit(f'Cannot resolve base {ref!r}')


base = resolve(os.environ['BASE_REF'])
head = git('rev-parse', 'HEAD')  # checkout has already resolved the requested head.
subprocess.run(['git', 'merge-base', '--is-ancestor', base, head], check=True)
commits = git('rev-list', '--reverse', f'{base}..{head}').splitlines()
if not 1 <= len(commits) <= 256:
    raise SystemExit('Select 1–256 commits (the GitHub matrix limit); split larger ranges')
print(f'base={base}')
print(f'head={head}')
print('matrix=' + json.dumps({'include': [{'sha': sha} for sha in commits]}, separators=(',', ':')))
