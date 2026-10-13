#!/usr/bin/env python3
"""Gate NUnit runtime results and compare Wayland failures with the X11 baseline."""
import argparse
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


def read_results(path):
    results = {}
    for case in ET.parse(path).getroot().iter('test-case'):
        name = case.get('fullname') or case.get('name')
        outcome = case.get('result')
        if not name or outcome not in ('Passed', 'Failed', 'Skipped', 'Inconclusive'):
            raise ValueError(f'{path}: invalid test-case: {case.attrib}')
        # Retries and cleanup may emit duplicates. Preserve any failure.
        previous = results.get(name)
        if previous != 'Failed' and (outcome != 'Skipped' or previous is None):
            results[name] = outcome
    if not results or not any(value in ('Passed', 'Failed') for value in results.values()):
        raise ValueError(f'{path}: no executed test cases')
    return results


def failures(results):
    return {name for name, outcome in results.items() if outcome == 'Failed'}


def section(title, names, baseline=None):
    print(f'\n### {title} ({len(names)})\n')
    for name in sorted(names):
        status = f' — X11: {baseline.get(name, "missing")}' if baseline is not None else ''
        print(f'- `{name.replace("`", "&#96;")}`{status}')
    if not names:
        print('None.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', type=Path, help='Fail on missing, empty or failed results')
    parser.add_argument('wayland', type=Path, nargs='?')
    parser.add_argument('x11', type=Path, nargs='?')
    args = parser.parse_args()
    try:
        if args.check:
            results = read_results(args.check)
            section('Runtime failures', failures(results))
            return int(bool(failures(results)))
        if not args.wayland or not args.x11:
            parser.error('Supply both result files or --check')
        wayland, x11 = read_results(args.wayland), read_results(args.x11)
        wl_failed, x_failed = failures(wayland), failures(x11)
        print('# Wayland / X11 runtime comparison\n')
        print(f'Wayland: {len(wayland)} cases, {len(wl_failed)} failures. '
              f'X11: {len(x11)} cases, {len(x_failed)} failures.')
        section('Wayland-only failures', wl_failed - x_failed, x11)
        section('Failures on both hosts', wl_failed & x_failed)
        section('X11-only failures', x_failed - wl_failed)
        section('Cases absent from X11', wayland.keys() - x11.keys())
        section('Cases absent from Wayland', x11.keys() - wayland.keys())
        # Per-host checks fail their jobs for shared failures too.
        return int(bool(wl_failed - x_failed))
    except (OSError, ET.ParseError, ValueError) as error:
        print(f'\nComparison unavailable: {error}', file=sys.stdout)
        return 2


if __name__ == '__main__':
    sys.exit(main())
