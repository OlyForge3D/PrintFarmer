import importlib.util
import io
import json
import os
import shutil
import signal
import subprocess
import textwrap
import time
import unittest
import uuid
from pathlib import Path
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / "run-tests.py"
spec = importlib.util.spec_from_file_location("run_tests", SCRIPT)
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


def event(name, identifier="Probe/test()", duration=0.05, status="Success"):
    payload = {}
    if name == "testStarted":
        payload = {"testIdentifier": {"identifier": {"_value": identifier}}}
    elif name == "testFinished":
        payload = {"test": {
            "identifier": {"_value": identifier},
            "duration": {"_value": str(duration)},
            "testStatus": {"_value": status},
        }}
    return json.dumps({"name": {"_value": name}, "structuredPayload": payload}) + "\n"


class EventTests(unittest.TestCase):
    def test_partial_line_is_retried_without_losing_event(self):
        events = runner.TestEvents()
        text = event("testStarted")
        stream = io.StringIO(text[:-1])
        events.read(stream, 1)
        self.assertEqual(events.active, set())
        self.assertEqual(stream.tell(), 0)
        events.read(io.StringIO(text), 2)
        self.assertEqual(events.active, {"Probe/test()"})

    def test_finished_duration_and_idle_deadline_ignore_log_chatter(self):
        events = runner.TestEvents()
        events.read(io.StringIO(event("testStarted")), 1)
        events.read(io.StringIO(event("testFinished", duration=60, status="Failure")), 61)
        events.read(io.StringIO(event("testSuiteFinished")), 100)
        self.assertEqual(events.tests[0]["reported_seconds"], 60)
        self.assertEqual(events.tests[0]["status"], "Failure")
        self.assertEqual(events.idle_since, 61)
        events.read(io.StringIO(event("testStarted", "Next/test()")), 101)
        self.assertIsNone(events.idle_since)


class RunnerTests(unittest.TestCase):
    def setUp(self):
        self.directory = Path("build/runner-tests") / uuid.uuid4().hex
        self.directory.mkdir(parents=True)
        self.bundle = self.directory / "Results.xcresult"
        self.arguments = [
            "test", "-destination", "platform=iOS Simulator,id=test-device",
            "-resultBundlePath", str(self.bundle),
        ]
        self.stub = self.directory / "xcodebuild"
        # A real subprocess exercises signals and artifact retention, not just mocks.
        self.stub.write_text(
            "#!/usr/bin/env python3\n"
            "import json, os, signal, subprocess, sys, time\n"
            "from pathlib import Path\n"
            "args=sys.argv[1:]\n"
            "for flag in ('-parallel-testing-enabled', '-test-timeouts-enabled', "
            "'-disable-concurrent-destination-testing'):\n"
            "    if args.count(flag)>1: sys.exit(64)\n"
            "bundle=Path(args[args.index('-resultBundlePath')+1])\n"
            "bundle.mkdir()\n"
            "(bundle/'arguments.json').write_text(json.dumps(args))\n"
            "stream=Path(args[args.index('-resultStreamPath')+1])\n"
            "mode=os.environ['RUNNER_TEST_MODE']\n"
            "def interrupt(sig, frame):\n"
            "    (bundle/'flushed').write_text('interrupted')\n"
            "    sys.exit(0)\n"
            "signal.signal(signal.SIGINT, interrupt)\n"
            "def emit(name):\n"
            "    with stream.open('a') as f: f.write(os.environ[name])\n"
            "if mode=='no-events': time.sleep(10)\n"
            "if mode=='bad-stream':\n"
            "    stream.write_text('bad json\\n'); time.sleep(10)\n"
            "emit('RUNNER_TEST_START')\n"
            "time.sleep(0.35 if mode=='long-body' else 0.06)\n"
            "emit('RUNNER_TEST_FINISH')\n"
            "if mode=='hang': time.sleep(10)\n"
            "if mode=='cancel':\n"
            "    os.kill(os.getppid(), signal.SIGTERM); time.sleep(10)\n"
            "if mode=='ignore-interrupt':\n"
            "    signal.signal(signal.SIGINT, signal.SIG_IGN)\n"
            "    child=subprocess.Popen([sys.executable, '-c', "
            "'import signal,time; signal.signal(signal.SIGINT, signal.SIG_IGN); time.sleep(10)'])\n"
            "    (bundle/'child.pid').write_text(str(child.pid))\n"
            "    time.sleep(10)\n"
            "if mode!='incomplete': emit('RUNNER_TEST_END')\n"
            "sys.exit(65 if mode=='failed' else 42 if mode=='other-failure' else 0)\n"
        )
        self.stub.chmod(0o755)

    def tearDown(self):
        shutil.rmtree(self.directory)

    def invoke(self, mode, status="Success"):
        environment = {
            "RUNNER_TEST_MODE": mode,
            "RUNNER_TEST_START": event("testStarted"),
            "RUNNER_TEST_FINISH": event("testFinished", status=status),
            "RUNNER_TEST_END": event("invocationFinished"),
        }
        with patch.dict(os.environ, environment), patch("sys.stdout", new=io.StringIO()):
            code = runner.run(
                self.arguments, invocation_timeout=1.5, finalization_timeout=0.3,
                flush_seconds=0.1, poll_seconds=0.01, executable=str(self.stub),
            )
        summary = json.loads(self.bundle.with_suffix(".timing.json").read_text())
        self.assertEqual(summary["exit_code"], code)
        self.assertTrue(self.bundle.with_suffix(".events.jsonl").exists())
        return code, summary

    def test_success_retains_selection_watchdog_and_artifacts(self):
        self.arguments += [
            "-only-testing:PrintFarmerUITests/UIWaitBudgetTests",
            "-parallel-testing-enabled", "NO", "-test-timeouts-enabled", "YES",
            "-disable-concurrent-destination-testing",
        ]
        code, summary = self.invoke("success")
        self.assertEqual(code, 0)
        args = json.loads((self.bundle / "arguments.json").read_text())
        self.assertIn("-only-testing:PrintFarmerUITests/UIWaitBudgetTests", args)
        self.assertEqual(args[args.index("-collect-test-diagnostics") + 1], "never")
        self.assertEqual(args[args.index("-test-timeouts-enabled") + 1], "YES")
        for option in ("-parallel-testing-enabled", "-test-timeouts-enabled",
                       "-disable-concurrent-destination-testing"):
            self.assertEqual(args.count(option), 1)
        self.assertEqual(summary["reported_test_seconds"], 0.05)
        self.assertAlmostEqual(summary["invocation_seconds"],
                               summary["reported_test_seconds"] +
                               summary["non_test_overhead_seconds"])

    def test_native_failures_are_not_hidden(self):
        code, summary = self.invoke("failed", status="Failure")
        self.assertEqual(code, 65)
        self.assertIsNone(summary["termination_reason"])

    def test_other_native_exit_is_preserved(self):
        self.assertEqual(self.invoke("other-failure")[0], 42)

    def test_failed_event_cannot_be_reported_as_success(self):
        self.assertEqual(self.invoke("success", status="Failure")[0], 65)

    def test_post_test_timeout_flushes_artifacts_but_stays_failed(self):
        code, summary = self.invoke("hang")
        self.assertEqual(code, 124)
        self.assertEqual(summary["xcodebuild_exit_code"], 0)
        self.assertEqual(summary["termination_reason"], "post-test-timeout")
        self.assertTrue((self.bundle / "flushed").exists())
        self.assertLess(summary["post_last_test_seconds"], 0.5)

    def test_hard_stop_bounds_unresponsive_process_and_descendant(self):
        code, summary = self.invoke("ignore-interrupt")
        self.assertEqual(code, 124)
        self.assertEqual(summary["xcodebuild_exit_code"], -signal.SIGKILL)
        self.assertLess(summary["post_last_test_seconds"], 0.5)
        child = (self.bundle / "child.pid").read_text()
        # Reaping is OS-owned; a zombie has exited and no longer holds stdout.
        for _ in range(50):
            state = runner.subprocess.run(
                ["ps", "-p", child, "-o", "stat="], capture_output=True, text=True,
            ).stdout.strip()
            if not state or state.startswith("Z"):
                break
            time.sleep(0.01)
        self.assertTrue(not state or state.startswith("Z"), state)

    def test_active_body_longer_than_finalization_budget_is_not_interrupted(self):
        self.assertEqual(self.invoke("long-body")[0], 0)
        self.assertFalse((self.bundle / "flushed").exists())

    def test_missing_events_hit_separate_invocation_bound(self):
        code, summary = self.invoke("no-events")
        self.assertEqual(code, 124)
        self.assertEqual(summary["termination_reason"], "invocation-timeout")
        self.assertIsNone(summary["post_last_test_seconds"])

    def test_malformed_stream_fails_closed(self):
        self.assertEqual(self.invoke("bad-stream")[0], 70)

    def test_incomplete_successful_stream_fails_closed(self):
        self.assertEqual(self.invoke("incomplete")[0], 70)

    def test_cancellation_stays_nonzero_after_graceful_flush(self):
        code, summary = self.invoke("cancel")
        self.assertEqual(code, 143)
        self.assertEqual(summary["termination_reason"], "cancelled")
        self.assertTrue((self.bundle / "flushed").exists())

    def test_ci_shell_commands_keep_selectors_artifacts_and_pipeline_exit(self):
        mobile = SCRIPT.parents[1]
        workflow = (mobile.parent / ".github/workflows/ios-pr-ci.yml").read_text()
        (self.directory / "scripts").symlink_to(mobile / "scripts", target_is_directory=True)
        (self.directory / "PrintFarmerUITests").symlink_to(
            mobile / "PrintFarmerUITests", target_is_directory=True,
        )
        cases = (
            ("Run unit tests", None, "build/TestResults", "PrintFarmerTests", "success", 0),
            ("Run Attention actions XCUI at accessibility XXXL", None,
             "build-iphone/AttentionActions", "PrintFarmerUITests/AttentionActionsUITests",
             "other-failure", 42),
            ("Run ${{ matrix.suite }} XCUI", "ShiftTasksUITests",
             "build-iphone-ShiftTasksUITests/ShiftTasksUITests",
             "PrintFarmerUITests/ShiftTasksUITests", "success", 0),
            ("Run ${{ matrix.suite }} XCUI", "OperatorShellUITests",
             "build-iphone-OperatorShellUITests/OperatorShellUITests",
             "PrintFarmerUITests/OperatorShellUITests", "failed", 65),
        )
        for step, suite, stem, selector, mode, expected in cases:
            with self.subTest(step=step, suite=suite):
                block = workflow.split(f"      - name: {step}\n", 1)[1]
                block = block.split("\n      - name:", 1)[0]
                shell = textwrap.dedent(block.split("        run: |\n", 1)[1])
                shell = shell.replace("${{ matrix.key }}", "iphone")
                shell = shell.replace("${{ matrix.suite }}", suite or "")
                (self.directory / stem).parent.mkdir(parents=True, exist_ok=True)
                environment = {
                    **os.environ,
                    "PATH": str(self.stub.parent.resolve()) + os.pathsep + os.environ["PATH"],
                    "SIMULATOR_UDID": "test-device",
                    "RUNNER_TEST_MODE": mode,
                    "RUNNER_TEST_START": event("testStarted"),
                    "RUNNER_TEST_FINISH": event("testFinished",
                                              status="Failure" if mode == "failed" else "Success"),
                    "RUNNER_TEST_END": event("invocationFinished"),
                }
                result = subprocess.run(
                    ["bash", "-c", shell], cwd=self.directory, env=environment,
                    capture_output=True, text=True, timeout=10,
                )
                self.assertEqual(result.returncode, expected, result.stdout + result.stderr)
                bundle = self.directory / f"{stem}.xcresult"
                args = json.loads((bundle / "arguments.json").read_text())
                self.assertIn(f"-only-testing:{selector}", args)
                self.assertEqual("-only-testing:PrintFarmerUITests/UIWaitBudgetTests" in args,
                                 suite == "ShiftTasksUITests")
                for suffix in (".log", ".events.jsonl", ".timing.json"):
                    self.assertTrue((self.directory / f"{stem}{suffix}").exists())
                upload = workflow.split(f"      - name: {step}\n", 1)[1]
                upload = upload.split("uses: actions/upload-artifact@v7", 1)[1]
                upload = upload.split("retention-days:", 1)[0]
                upload = upload.replace("${{ matrix.key }}", "iphone")
                upload = upload.replace("${{ matrix.suite }}", suite or "")
                for suffix in (".xcresult", ".log", ".events.jsonl", ".timing.json"):
                    self.assertIn(f"mobile/{stem}{suffix}", upload)

    def test_rejects_retries_parallelism_and_watchdog_disabling(self):
        for override in (
            ["-retry-tests-on-failure"], ["-test-iterations", "2"],
            ["-collect-test-diagnostics", "on-failure"],
            ["-resultStreamPath", "somewhere"],
            ["-parallel-testing-enabled", "YES"], ["-test-timeouts-enabled", "NO"],
            ["-destination", "platform=iOS Simulator,id=other"],
        ):
            with self.subTest(override=override), self.assertRaises(ValueError):
                runner.validate_arguments(self.arguments + override)

    def test_does_not_overwrite_evidence(self):
        self.bundle.mkdir()
        with self.assertRaises(ValueError):
            runner.validate_arguments(self.arguments)

    def test_invalid_budgets_do_not_launch_xcodebuild(self):
        for budget in (0, 10, float("nan"), float("inf")):
            with self.subTest(budget=budget), self.assertRaises(ValueError):
                runner.run(self.arguments, finalization_timeout=budget)
        self.assertFalse(self.bundle.exists())


if __name__ == "__main__":
    unittest.main()
