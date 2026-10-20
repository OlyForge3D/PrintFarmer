import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import subprocess


SCRIPT = Path(__file__).parents[1] / "capture-sidebar-diagnostic.py"
SPEC = importlib.util.spec_from_file_location("sidebar_capture", SCRIPT)
capture = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(capture)


class CaptureContracts(unittest.TestCase):
    def test_app_identity_is_executable_and_simulator_specific(self):
        executable = "/Users/runner/Library/Developer/CoreSimulator/Devices/approved/data/Containers/Bundle/Application/id/PrintFarmer.app/PrintFarmer"
        self.assertTrue(capture.is_app(executable, "approved"))
        self.assertFalse(capture.is_app(executable, "other"))
        self.assertFalse(capture.is_app(executable + "UITests-Runner", "approved"))
        self.assertFalse(capture.is_app("/bin/sleep " + executable, "approved"))

    def test_process_table_preserves_paths_with_spaces(self):
        self.assertEqual(capture.processes(" 42 /path with space/PrintFarmer\n"),
                         {42: "/path with space/PrintFarmer"})

    def test_verification_rejects_reused_pid(self):
        with tempfile.TemporaryDirectory() as folder:
            observer = capture.Capture("approved", Path(folder))
            with patch.object(capture.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "/bin/sleep\n")):
                with self.assertRaises(ValueError):
                    observer.verify(42)

    def test_no_pid_is_explicit_capture_failure(self):
        with tempfile.TemporaryDirectory() as folder:
            observer = capture.Capture("approved", Path(folder))
            self.assertEqual(observer.finish(0), 2)
            self.assertIn("No verified app PID", observer.errors[0])

    def test_test_failure_is_not_masked(self):
        with tempfile.TemporaryDirectory() as folder:
            observer = capture.Capture("approved", Path(folder))
            self.assertEqual(observer.finish(65), 65)

    def test_observer_error_is_not_success_when_test_passes(self):
        with tempfile.TemporaryDirectory() as folder:
            observer = capture.Capture("approved", Path(folder))
            observer.seen.add(42)
            observer.errors.append("receiver descriptor collection failed")
            self.assertEqual(observer.finish(0), 2)

    def test_failed_observer_command_retains_output_and_exit(self):
        with tempfile.TemporaryDirectory() as folder:
            observer = capture.Capture("approved", Path(folder))
            with patch.object(capture.subprocess, "run", return_value=subprocess.CompletedProcess([], 1)):
                with self.assertRaises(RuntimeError):
                    observer.command(["/usr/bin/sample", "42", "1", "10"], Path(folder) / "sample.txt")
            event = json.loads((Path(folder) / "capture.jsonl").read_text())
            self.assertEqual(event["exitCode"], 1)
            self.assertTrue((Path(folder) / "sample.txt").exists())

    def test_sample_budget_stops_observation_without_repeating_test(self):
        with tempfile.TemporaryDirectory() as folder:
            observer = capture.Capture("approved", Path(folder))
            executable = "/Users/runner/Library/Developer/CoreSimulator/Devices/approved/data/Containers/Bundle/Application/id/PrintFarmer.app/PrintFarmer"
            with patch.object(observer, "table", return_value={42: executable}), \
                    patch.object(observer.stop, "wait"), \
                    patch.object(observer, "window", side_effect=lambda *_: setattr(observer, "windows", observer.windows + 1)):
                observer.monitor()
            self.assertEqual(observer.windows, 24)
            self.assertTrue(observer.saturated)

    def test_observer_failure_is_persisted(self):
        with tempfile.TemporaryDirectory() as folder:
            observer = capture.Capture("approved", Path(folder))
            with patch.object(observer, "table", side_effect=subprocess.TimeoutExpired("ps", 3)):
                observer.monitor()
            events = [json.loads(line) for line in (Path(folder) / "capture.jsonl").read_text().splitlines()]
            self.assertEqual(events[-1]["kind"], "capture-error")
            self.assertTrue(observer.errors)

    def test_local_execution_is_rejected_before_starting_processes(self):
        with patch.dict(capture.os.environ, {}, clear=True), \
                patch("sys.argv", [str(SCRIPT), "--udid", "approved", "--output", "/unused"]), \
                patch.object(capture.subprocess, "run") as run:
            with self.assertRaises(SystemExit):
                capture.main()
            run.assert_not_called()

    def test_broad_selector_is_rejected_without_invoking_runner(self):
        argv = [str(SCRIPT), "--udid", "approved", "--output", "/unused", "--",
                "python3", "scripts/run-tests.py", "--", "test-without-building",
                "-only-testing:PrintFarmerUITests"]
        with patch.dict(capture.os.environ, {"GITHUB_ACTIONS": "true", "RUNNER_OS": "macOS"}), \
                patch("sys.argv", argv), patch.object(capture.subprocess, "run") as run:
            with self.assertRaises(SystemExit):
                capture.main()
            run.assert_not_called()

    def test_workflow_is_manual_single_test_and_pins_original_source(self):
        workflow = (SCRIPT.parents[2] / ".github/workflows/ci.yml").read_text()
        self.assertIn("workflow_dispatch:", workflow)
        self.assertNotIn("pull_request:", workflow)
        self.assertNotIn("push:", workflow)
        self.assertNotIn("matrix:", workflow)
        self.assertIn("github.run_attempt == 1", workflow)
        self.assertIn("SOURCE_SHA: 98cd84fc1fc685933b67522ca1ed97e5128c9e26", workflow)
        self.assertEqual(workflow.count("-only-testing:"), 1)
        self.assertIn("-only-testing:PrintFarmerUITests/FilamentCoverageUITests/testDuplicateDisplayNamePrintersRemainDistinctWithoutCoverageBadges", workflow)
        self.assertIn("timeout-minutes: 25", workflow)
        self.assertNotIn("PFARM_STALL_CAPTURE", workflow)


if __name__ == "__main__":
    unittest.main()
