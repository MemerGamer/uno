"""Synthetic artifact checks; no .NET builds or runtime app required."""
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET


SCRIPT = Path(__file__).with_name('compare-results.py')


class CompareResultsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def artifact(self, host, group):
        return self.root / f'wayland-runtime-{host}-group-{group}'

    def xml_path(self, host, group):
        return self.artifact(host, group) / f'skia-linux-{host}-group-{group}-runtime-tests-results.xml'

    def write_xml(self, host, group, cases):
        path = self.xml_path(host, group)
        path.parent.mkdir(parents=True, exist_ok=True)
        root = ET.Element('test-run')
        for name, result in cases:
            ET.SubElement(root, 'test-case', fullname=name, result=result)
        ET.ElementTree(root).write(path)
        return path

    def run_cli(self, *args):
        return subprocess.run([sys.executable, str(SCRIPT), *map(str, args)],
                              capture_output=True, text=True)

    def compare(self, groups=1):
        return self.run_cli('--results-dir', self.root, '--group-count', groups)

    def test_merges_six_groups_and_preserves_duplicate_failures(self):
        for host in ('wayland', 'x11'):
            for group in range(6):
                self.write_xml(host, group, [(f'case-{group}', 'Passed'), ('duplicate', 'Passed')])
        self.write_xml('wayland', 0, [('case-0', 'Failed'), ('duplicate', 'Failed')])
        self.write_xml('x11', 5, [('case-5', 'Failed'), ('duplicate', 'Failed')])
        run = self.compare(6)
        self.assertEqual(1, run.returncode, run.stderr)
        self.assertIn('Wayland: 7 cases, 2 failures. X11: 7 cases, 2 failures.', run.stdout)
        self.assertIn('### Wayland-only failures (1)\n\n- `case-0` — X11: Passed', run.stdout)
        self.assertIn('### X11-only failures (1)\n\n- `case-5`', run.stdout)
        self.assertIn('### Failures on both hosts (1)\n\n- `duplicate`', run.stdout)

    def test_filtered_default_one_group(self):
        for host in ('wayland', 'x11'):
            self.write_xml(host, 0, [('filtered', 'Passed')])
        run = self.compare()
        self.assertEqual(0, run.returncode, run.stderr)
        self.assertIn('Wayland: 1 cases, 0 failures. X11: 1 cases, 0 failures.', run.stdout)
        self.assertNotIn('incomplete', run.stdout)

    def test_missing_wayland_group_uses_live_log_and_keeps_other_results(self):
        for host in ('wayland', 'x11'):
            self.write_xml(host, 0, [('first', 'Passed')])
        self.write_xml('x11', 1, [('second', 'Failed')])
        logs = self.artifact('wayland', 1) / 'uitests-failure-results'
        logs.mkdir(parents=True)
        (logs / 'runtime-wayland-group-1.log').write_text('shell setup\n')
        (logs / 'wayland-runtime-wayland-group-1.log').write_text(
            'Running test Earlier\n[INFO] Running test CrashingCase\nRunning tests (2 fixtures)...\n')
        run = self.compare(2)
        self.assertEqual(2, run.returncode, run.stderr)
        self.assertIn('**wayland group 1**: Missing XML', run.stdout)
        self.assertIn('Last Running test: `[INFO] Running test CrashingCase', run.stdout)
        self.assertIn('**Partial comparison:**', run.stdout)
        self.assertIn('X11-only failures (1)', run.stdout)
        self.assertNotIn('Traceback', run.stderr)

    def test_missing_x11_group_uses_captured_harness_log(self):
        self.write_xml('wayland', 0, [('test', 'Passed')])
        logs = self.artifact('x11', 0) / 'uitests-failure-results'
        logs.mkdir(parents=True)
        (logs / 'runtime-x11-group-0.log').write_text('Running test First\nRunning test Last\n')
        run = self.compare()
        self.assertEqual(2, run.returncode, run.stderr)
        self.assertIn('**x11 group 0**: Missing XML', run.stdout)
        self.assertIn('Last Running test: `Running test Last', run.stdout)

    def test_no_artifacts_reports_every_expected_group(self):
        run = self.compare(6)
        self.assertEqual(2, run.returncode, run.stderr)
        self.assertIn('Crashed / incomplete groups (12)', run.stdout)
        self.assertIn('**wayland group 5**', run.stdout)
        self.assertIn('**x11 group 5**', run.stdout)
        self.assertIn('Unavailable (no Running test line', run.stdout)

    def test_nested_artifact_layout(self):
        for host in ('wayland', 'x11'):
            path = self.write_xml(host, 0, [('test', 'Passed')])
            nested = path.parent / 'build'
            nested.mkdir()
            path.rename(nested / path.name)
        run = self.compare()
        self.assertEqual(0, run.returncode, run.stderr)
        self.assertIn('Wayland: 1 cases, 0 failures. X11: 1 cases, 0 failures.', run.stdout)

    def test_invalid_xml_and_no_executed_cases_report_groups(self):
        path = self.write_xml('wayland', 0, [])
        path.write_text('<truncated')
        self.write_xml('x11', 0, [('skipped', 'Skipped')])
        run = self.compare()
        self.assertEqual(2, run.returncode, run.stderr)
        self.assertIn('Crashed / incomplete groups (2)', run.stdout)
        self.assertIn('**wayland group 0**', run.stdout)
        self.assertIn('no executed test cases', run.stdout)

    def test_check_pass_fail_and_missing_with_log(self):
        path = self.write_xml('x11', 0, [('case', 'Passed')])
        self.assertEqual(0, self.run_cli('--check', path).returncode)
        self.write_xml('x11', 0, [('case', 'Failed')])
        self.assertEqual(1, self.run_cli('--check', path).returncode)
        path.unlink()
        logs = self.artifact('x11', 0)
        (logs / 'runtime-x11-group-0.log').write_text('Running test Crashed\n')
        run = self.run_cli('--check', path, '--logs-dir', logs)
        self.assertEqual(2, run.returncode, run.stderr)
        self.assertIn('Runtime group crashed / incomplete', run.stdout)
        self.assertIn('Last Running test: `Running test Crashed', run.stdout)

    def test_legacy_two_files_and_skipped_baseline(self):
        wayland = self.write_xml('wayland', 0, [('case', 'Failed')])
        x11 = self.write_xml('x11', 0, [('case', 'Skipped'), ('other', 'Passed')])
        run = self.run_cli(wayland, x11)
        self.assertEqual(1, run.returncode, run.stderr)
        self.assertIn('`case` — X11: Skipped', run.stdout)


if __name__ == '__main__':
    unittest.main()
