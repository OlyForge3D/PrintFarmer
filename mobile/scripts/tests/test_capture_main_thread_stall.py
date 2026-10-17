import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / "capture-main-thread-stall.py"
SPEC = importlib.util.spec_from_file_location("capture_stall", SCRIPT)
capture = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(capture)

UDID = "A2011F14-88A1-46AA-999A-FD6FEDCD870B"
COMMAND = f"/devices/{UDID}/data/Containers/Bundle/Application/id/PrintFarmer.app/PrintFarmer"


def marker(**changes):
    return dict(pid=37055, unixTime=100, uptime=80, stalled=2, beat=12, **changes)


class CaptureTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.path = self.root / "marker.json"
        self.path.write_text(json.dumps(marker()))

    def test_marker_requires_current_process_and_finite_timing(self):
        self.assertEqual(capture.read_marker(self.path, 101)["pid"], 37055)
        for key, value in [("pid", -1), ("pid", True), ("beat", 0),
                           ("stalled", float("nan")), ("uptime", float("inf"))]:
            with self.subTest(key=key, value=value):
                record = marker()
                record[key] = value
                self.path.write_text(json.dumps(record))
                with self.assertRaises(ValueError):
                    capture.read_marker(self.path, 101)

    def test_stale_future_missing_and_oversized_markers_never_authorize_sampling(self):
        self.assertIsNone(capture.read_marker(self.path, 104))
        self.assertIsNone(capture.read_marker(self.path, 99))
        self.assertIsNone(capture.read_marker(self.root / "missing", 101))
        self.path.write_text(" " * 4097)
        with self.assertRaisesRegex(ValueError, "Oversized"):
            capture.read_marker(self.path, 101)

    def test_pid_must_belong_to_exact_app_on_selected_simulator(self):
        def run(command, **kwargs):
            self.assertEqual(command, ["/bin/ps", "-p", "37055", "-o", "command="])
            return subprocess.CompletedProcess(command, 0, COMMAND + "\n", "")
        self.assertEqual(capture.verified_process(37055, UDID, run), COMMAND)
        for wrong in [COMMAND.replace(UDID, "another-simulator"), COMMAND + "-Runner"]:
            with self.subTest(command=wrong):
                with self.assertRaises(ValueError):
                    capture.verified_process(37055, UDID, lambda *a, **k:
                                             subprocess.CompletedProcess(a, 0, wrong, ""))

    def test_live_capture_records_two_bounded_windows_and_process_identity(self):
        monitor = capture.Capture(UDID, self.root)
        calls = []
        def run(command, **kwargs):
            calls.append((command, kwargs))
            Path(command[-1]).write_text("main-thread stack")
            return subprocess.CompletedProcess(command, 0, "sampling complete", "")
        with patch.object(capture, "verified_process", return_value=COMMAND), \
                patch.object(capture.subprocess, "run", side_effect=run):
            monitor.sample(marker())
        self.assertEqual(monitor.samples, 2)
        self.assertEqual(monitor.sampled, {(37055, 12)})
        self.assertEqual(len(calls), 2)
        for command, options in calls:
            self.assertEqual(command[:5], ["/usr/bin/sample", "37055", "5", "10", "-file"])
            self.assertEqual(options["timeout"], 8)
        statuses = list(self.root.glob("*.status.json"))
        self.assertEqual(len(statuses), 2)
        for path in statuses:
            status = json.loads(path.read_text())
            self.assertEqual(status["simulator"], UDID)
            self.assertEqual(status["command"], COMMAND)
            self.assertEqual(status["exitCode"], 0)

    def test_sample_failure_and_exhausted_limits_are_explicit(self):
        monitor = capture.Capture(UDID, self.root)
        with patch.object(capture, "verified_process", return_value=COMMAND), \
                patch.object(capture.subprocess, "run", return_value=
                             subprocess.CompletedProcess([], 1, "", "permission denied")):
            with self.assertRaisesRegex(RuntimeError, "sample failed"):
                monitor.sample(marker())
        monitor.samples = capture.MAX_SAMPLES
        with patch.object(capture, "verified_process", return_value=COMMAND):
            with self.assertRaisesRegex(RuntimeError, "count limit"):
                monitor.sample(marker())
        monitor.samples = 0
        with patch.object(capture, "verified_process", return_value=COMMAND), \
                patch.object(capture, "MAX_STORAGE", 1):
            with self.assertRaisesRegex(RuntimeError, "storage limit"):
                monitor.sample(marker())

    def test_no_markers_is_capture_failure_not_a_successful_experiment(self):
        monitor = capture.Capture(UDID, self.root)
        self.assertEqual(monitor.finish(), 2)
        self.assertIn("No fresh app markers", monitor.error)

    def test_wrapper_preserves_test_failure_and_never_repeats_command(self):
        for test_status, expected in [(65, 65), (0, 2)]:
            with self.subTest(test_status=test_status):
                output = self.root / f"capture-{test_status}"
                args = [str(SCRIPT), "--udid", UDID, "--output", str(output),
                        "--", "python3", "scripts/run-tests.py", "--", "test"]
                with patch.object(sys, "argv", args), \
                        patch.object(capture.Capture, "monitor"), \
                        patch.object(capture.subprocess, "run", return_value=
                                     subprocess.CompletedProcess([], test_status)) as run:
                    self.assertEqual(capture.main(), expected)
                    run.assert_called_once_with(
                        ["python3", "scripts/run-tests.py", "--", "test"], check=False)

    def test_diagnostic_workflow_is_single_open_event_and_retains_original_shard(self):
        workflow = (SCRIPT.parents[2] / ".github/workflows/ios-pr-ci.yml").read_text()
        self.assertIn("types: [opened]", workflow)
        self.assertNotIn("synchronize", workflow)
        self.assertIn("17F113", workflow)
        self.assertIn('buildversion == "23F77"', workflow)
        selectors = [line.strip() for line in workflow.splitlines()
                     if line.strip().startswith("-only-testing:")]
        self.assertEqual(selectors, [
            "-only-testing:PrintFarmerUITests/ColdOfflineShellUITests \\",
            "-only-testing:PrintFarmerUITests/UIWaitBudgetTests \\",
            "-only-testing:PrintFarmerUITests/JobDetailIPadNavigationUITests \\",
        ])
        self.assertNotIn("-skip-testing:", workflow)
        self.assertNotIn("-retry-tests", workflow)
        self.assertIn("if: always()", workflow)
        self.assertIn("mobile/build-capture/stacks/", workflow)


if __name__ == "__main__":
    unittest.main()
