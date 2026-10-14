#!/usr/bin/env python3
"""Gate NUnit runtime results and compare Wayland failures with the X11 baseline."""
import argparse
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET


def merge_results(results, incoming):
    for name, outcome in incoming.items():
        # Retries, cleanup and merged groups may emit duplicates. Preserve any failure.
        previous = results.get(name)
        if previous != 'Failed' and (outcome != 'Skipped' or previous is None):
            results[name] = outcome


def read_results(path):
    if not path.is_file():
        raise ValueError(f'{path}: Missing XML: group crashed, timed out, or did not reach result publication.')
    results = {}
    for case in ET.parse(path).getroot().iter('test-case'):
        name = case.get('fullname') or case.get('name')
        outcome = case.get('result')
        if not name or outcome not in ('Passed', 'Failed', 'Skipped', 'Inconclusive'):
            raise ValueError(f'{path}: invalid test-case: {case.attrib}')
        merge_results(results, {name: outcome})
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


def last_running_test(logs_dir):
    # Weston redirects app output until exit. Its live log is more useful than
    # the captured shell output when the app crashes or times out before cat.
    logs = sorted(logs_dir.rglob('*.log'),
                  key=lambda path: (path.name.startswith('wayland-runtime'), str(path)))
    for path in reversed(logs):
        try:
            last = None
            with path.open(errors='replace') as log:
                for line in log:
                    if re.search(r'\bRunning test\b', line):
                        last = line.strip()
            if last:
                return f'{last} (log: {path})'
        except OSError:
            continue
    return 'Unavailable (no Running test line in the downloaded logs).'


def report_unavailable(label, error, logs_dir):
    print(f'\n- **{label}**: {str(error).replace("`", "&#96;")}')
    print(f'  - Last Running test: `{last_running_test(logs_dir).replace("`", "&#96;")}`')


def read_groups(results_dir, host, group_count):
    merged, unavailable = {}, []
    for group in range(group_count):
        artifact = results_dir / f'wayland-runtime-{host}-group-{group}'
        filename = f'skia-linux-{host}-group-{group}-runtime-tests-results.xml'
        paths = list(artifact.rglob(filename))
        try:
            if not paths:
                raise ValueError('Missing XML: group crashed, timed out, or did not reach result publication.')
            if len(paths) != 1:
                raise ValueError(f'Expected one XML, found {len(paths)}: {filename}')
            results = read_results(paths[0])
            merge_results(merged, results)
            print(f'| {host} | {group} | {len(results)} | {len(failures(results))} |')
        except (OSError, ET.ParseError, ValueError) as error:
            print(f'| {host} | {group} | unavailable | unavailable |')
            unavailable.append((f'{host} group {group}', error, artifact))
    return merged, unavailable


def compare(wayland, x11):
    wl_failed, x_failed = failures(wayland), failures(x11)
    print(f'\nWayland: {len(wayland)} cases, {len(wl_failed)} failures. '
          f'X11: {len(x11)} cases, {len(x_failed)} failures.')
    section('Wayland-only failures', wl_failed - x_failed, x11)
    section('Failures on both hosts', wl_failed & x_failed)
    section('X11-only failures', x_failed - wl_failed)
    section('Cases absent from X11', wayland.keys() - x11.keys())
    section('Cases absent from Wayland', x11.keys() - wayland.keys())
    # Per-group checks fail their jobs for shared and X11 failures too.
    return int(bool(wl_failed - x_failed))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', type=Path, help='Fail on missing, empty or failed results')
    parser.add_argument('--logs-dir', type=Path, help='Harness logs for --check diagnostics')
    parser.add_argument('--results-dir', type=Path, help='Downloaded per-host, per-group artifacts')
    parser.add_argument('--group-count', type=int, default=1, help='Expected groups per host (default: 1)')
    parser.add_argument('wayland', type=Path, nargs='?')
    parser.add_argument('x11', type=Path, nargs='?')
    args = parser.parse_args()
    if args.group_count < 1:
        parser.error('--group-count must be positive')
    if args.results_dir and (args.check or args.wayland or args.x11):
        parser.error('--results-dir cannot be combined with --check or result files')
    try:
        if args.check:
            results = read_results(args.check)
            section('Runtime failures', failures(results))
            return int(bool(failures(results)))
        if args.results_dir:
            print('# Wayland / X11 runtime comparison\n')
            print('| Host | Group | Cases | Failures |\n| --- | --- | --- | --- |')
            wayland, wl_unavailable = read_groups(args.results_dir, 'wayland', args.group_count)
            x11, x_unavailable = read_groups(args.results_dir, 'x11', args.group_count)
            unavailable = wl_unavailable + x_unavailable
            if unavailable:
                print(f'\n### Crashed / incomplete groups ({len(unavailable)})\n')
                for label, error, logs_dir in unavailable:
                    report_unavailable(label, error, logs_dir)
                print('\n**Partial comparison:** only groups with valid XML are included. '
                      'Host-only failures and coverage gaps are provisional until all groups finish.')
            status = compare(wayland, x11)
            return 2 if unavailable else status
        if not args.wayland or not args.x11:
            parser.error('Supply --results-dir, both result files or --check')
        print('# Wayland / X11 runtime comparison\n')
        return compare(read_results(args.wayland), read_results(args.x11))
    except (OSError, ET.ParseError, ValueError) as error:
        if args.check:
            print('\n### Runtime group crashed / incomplete\n')
            report_unavailable(str(args.check), error, args.logs_dir or args.check.parent)
        else:
            print(f'\nComparison unavailable: {error}')
        return 2


if __name__ == '__main__':
    sys.exit(main())
