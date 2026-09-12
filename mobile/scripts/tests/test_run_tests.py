import importlib.util
import io
import json
import os
import re
import shutil
import signal
import subprocess
import textwrap
import time
import unittest
import uuid
from collections import Counter
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

    def test_ci_matrix_selectors_name_existing_xcui_classes(self):
        mobile = SCRIPT.parents[1]
        workflow = (mobile.parent / ".github/workflows/ios-pr-ci.yml").read_text()
        matrix = workflow.split("  xcui-shards:\n", 1)[1].split("    defaults:", 1)[0]
        shards = re.findall(
            r"^          - key: (?P<key>\S+)\n"
            r"            family: (?P<family>iPhone|iPad)\n"
            r"(?:            .*\n)*?"
            r"            selectors: >-\n"
            r"(?P<selectors>(?:              -only-testing:PrintFarmerUITests/\S+\n)+)",
            matrix,
            re.MULTILINE,
        )
        self.assertEqual(len(shards), 8, "XCUI must run in four shards per device family")
        self.assertEqual(
            Counter(family for _, family, _ in shards),
            {"iPhone": 4, "iPad": 4},
        )
        self.assertEqual(len({key for key, _, _ in shards}), 8)

        selectors_by_family = {"iPhone": [], "iPad": []}
        selectors_by_shard = {}
        for key, family, selectors in shards:
            self.assertRegex(key, rf"^{family.lower()}-[1-4]$")
            shard_selectors = [
                line.strip().removeprefix("-only-testing:")
                for line in selectors.splitlines()
            ]
            selectors_by_shard[key] = shard_selectors
            selectors_by_family[family].extend(shard_selectors)

        shared = [
            "PrintFarmerUITests/AttentionActionsUITests",
            "PrintFarmerUITests/LoginFlowUITests",
            "PrintFarmerUITests/OperatorShellUITests",
            "PrintFarmerUITests/TwoModesOperatorShellUITests/testFloorModeShowsRequiredCompactDestinations",
            "PrintFarmerUITests/OperatorFeatureVisibilityUITests",
            "PrintFarmerUITests/ScanStationUITests",
            "PrintFarmerUITests/HarvestUITests",
            "PrintFarmerUITests/PartsInventoryUITests",
            "PrintFarmerUITests/PrinterListUITests",
            "PrintFarmerUITests/FilamentCoverageUITests",
            "PrintFarmerUITests/ColdOfflineShellUITests",
            "PrintFarmerUITests/TaskActionRoutingUITests",
            "PrintFarmerUITests/ShiftTasksUITests",
            "PrintFarmerUITests/UIWaitBudgetTests",
            "PrintFarmerUITests/ShiftTasksGroupedUITests",
        ]
        self.assertEqual(
            Counter(selectors_by_family["iPhone"]),
            Counter(shared + ["PrintFarmerUITests/ShiftTasksFailedRefreshUITests"]),
        )
        self.assertEqual(
            Counter(selectors_by_family["iPad"]),
            Counter(shared + ["PrintFarmerUITests/JobDetailIPadNavigationUITests"]),
        )
        login_step = workflow.split("      - name: Run login XCUI\n", 1)[1]
        login_step = login_step.split("\n      - name:", 1)[0]
        self.assertIn("if: matrix.shard == 1", login_step)
        self.assertIn(
            "-only-testing:PrintFarmerUITests/LoginFlowUITests",
            login_step,
        )
        self.assertIn("test-without-building", login_step)
        shard_step = workflow.split("      - name: Run XCUI shard\n", 1)[1]
        shard_step = shard_step.split("\n      - name:", 1)[0]
        self.assertIn(
            "if: ${{ !cancelled() && (success() || steps.login-xcui.conclusion == 'failure') }}",
            shard_step,
        )
        self.assertIn(
            'selectors=("${selectors[@]:1}")',
            shard_step,
        )
        for key in ("iphone-1", "ipad-1"):
            with self.subTest(key=key):
                self.assertEqual(
                    selectors_by_shard[key][0],
                    "PrintFarmerUITests/LoginFlowUITests",
                )
                self.assertEqual(
                    selectors_by_shard[key][1:],
                    [
                        "PrintFarmerUITests/AttentionActionsUITests",
                        "PrintFarmerUITests/OperatorShellUITests",
                        "PrintFarmerUITests/TwoModesOperatorShellUITests/testFloorModeShowsRequiredCompactDestinations",
                    ],
                )

        declarations = set()
        for source in (mobile / "PrintFarmerUITests").glob("*.swift"):
            declarations.update(re.findall(r"\bclass\s+(\w+)\s*:", source.read_text()))
        for selectors in selectors_by_family.values():
            for selector in selectors:
                suite = selector.split("/")[1]
                with self.subTest(suite=suite):
                    self.assertIn(suite, declarations, "A stale class selector executes zero XCTest cases")
        self.assertIn(
            "UICTContentSizeCategoryAccessibilityExtraExtraExtraLarge",
            (mobile / "PrintFarmerUITests/AttentionActionsUITests.swift").read_text(),
        )


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
            (
                "Run unit tests",
                None,
                "build/TestResults",
                "-only-testing:PrintFarmerTests",
                "other-failure",
                42,
            ),
            (
                "Run login XCUI",
                "iphone-1",
                "build-iphone-1/Login",
                "-only-testing:PrintFarmerUITests/LoginFlowUITests",
                "success",
                0,
            ),
            (
                "Run XCUI shard",
                "iphone-1",
                "build-iphone-1/XCUIShard",
                "-only-testing:PrintFarmerUITests/LoginFlowUITests "
                "-only-testing:PrintFarmerUITests/AttentionActionsUITests "
                "-only-testing:PrintFarmerUITests/OperatorShellUITests "
                "-only-testing:PrintFarmerUITests/TwoModesOperatorShellUITests/testFloorModeShowsRequiredCompactDestinations",
                "success",
                0,
            ),
            (
                "Run XCUI shard",
                "iphone-4",
                "build-iphone-4/XCUIShard",
                "-only-testing:PrintFarmerUITests/ShiftTasksUITests "
                "-only-testing:PrintFarmerUITests/UIWaitBudgetTests",
                "success",
                0,
            ),
            (
                "Run XCUI shard",
                "ipad-4",
                "build-ipad-4/XCUIShard",
                "-only-testing:PrintFarmerUITests/JobDetailIPadNavigationUITests",
                "failed",
                65,
            ),
        )
        for step, key, stem, selectors, mode, expected in cases:
            with self.subTest(step=step, key=key):
                block = workflow.split(f"      - name: {step}\n", 1)[1]
                block = block.split("\n      - name:", 1)[0]
                shell = textwrap.dedent(block.split("        run: |\n", 1)[1])
                if key is not None:
                    shell = shell.replace("${{ matrix.key }}", key)
                    shell = shell.replace("${{ matrix.shard }}", key.rsplit("-", 1)[1])
                    shell = shell.replace("$SELECTORS", selectors)
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
                self.assertIn("test-without-building", args)
                expected_selectors = selectors.split()
                if key is not None and key.endswith("-1") and step == "Run XCUI shard":
                    self.assertNotIn(
                        "-only-testing:PrintFarmerUITests/LoginFlowUITests",
                        args,
                    )
                    expected_selectors = expected_selectors[1:]
                for selector in expected_selectors:
                    self.assertIn(selector, args)
                for suffix in (".log", ".events.jsonl", ".timing.json"):
                    self.assertTrue((self.directory / f"{stem}{suffix}").exists())
                upload = workflow.split(f"      - name: {step}\n", 1)[1]
                upload = upload.split("uses: actions/upload-artifact@v7", 1)[1]
                upload = upload.split("retention-days:", 1)[0]
                if key is not None:
                    upload = upload.replace("${{ matrix.key }}", key)
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
