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


def event(name, identifier="Probe/test()", duration=0.05, status="Success",
          severity="testFailure"):
    payload = {}
    if name == "testStarted":
        payload = {"testIdentifier": {"identifier": {"_value": identifier}}}
    elif name == "testFinished":
        payload = {"test": {
            "identifier": {"_value": identifier},
            "duration": {"_value": str(duration)},
            "testStatus": {"_value": status},
        }}
    elif name == "issueEmitted":
        test_class, method = identifier.split("/")
        payload = {
            "issue": {"testCaseName": {"_value": f"{test_class}.{method}"}},
            "severity": {"_value": severity},
        }
    return json.dumps({"name": {"_value": name}, "structuredPayload": payload}) + "\n"


class EventTests(unittest.TestCase):
    def test_failed_test_may_retry_once_and_retains_both_attempts(self):
        tests = [
            {"identifier": "Probe/test()", "status": "Failure", "reported_seconds": 1},
            {"identifier": "Other/test()", "status": "Success", "reported_seconds": 2},
            {"identifier": "Probe/test()", "status": "Success", "reported_seconds": 3},
        ]
        attempts, flaky, failed, errors = runner.summarize_attempts(tests, True)
        self.assertEqual(len(attempts["Probe/test()"]), 2)
        self.assertEqual(len(attempts["Other/test()"]), 1)
        self.assertEqual(flaky, ["Probe/test()"])
        self.assertEqual(failed, [])
        self.assertEqual(errors, [])

    def test_unexpected_repetitions_fail_closed(self):
        for statuses, enabled in (
            (["Failure", "Success"], False),
            (["Success", "Success"], True),
            (["Skipped", "Success"], True),
            (["Failure", "Failure", "Success"], True),
        ):
            with self.subTest(statuses=statuses, enabled=enabled):
                attempts, flaky, failed, errors = runner.summarize_attempts([
                    {"identifier": "Probe/test()", "status": status}
                    for status in statuses
                ], enabled)
                self.assertEqual(len(attempts["Probe/test()"]), len(statuses))
                self.assertEqual(flaky, [])
                self.assertEqual(failed, ["Probe/test()"])
                self.assertEqual(len(errors), 1)

    def test_retry_failure_or_skip_does_not_clear_initial_failure(self):
        for status in ("Failure", "Failed", "Skipped", "Unknown"):
            with self.subTest(status=status):
                _, flaky, failed, _ = runner.summarize_attempts([
                    {"identifier": "Probe/test()", "status": "Failure"},
                    {"identifier": "Probe/test()", "status": status},
                ], True)
                self.assertEqual(flaky, [])
                self.assertEqual(failed, ["Probe/test()"])

    def test_observed_full_shard_repetition_is_rejected(self):
        initial = [
            {"identifier": f"HarvestUITests/testCase{index}()",
             "status": "Failure" if index == 0 else "Success"}
            for index in range(12)
        ]
        retry = [
            {"identifier": f"HarvestUITests/testCase{index}()", "status": "Success"}
            for index in range(12)
        ]
        attempts, flaky, failed, errors = runner.summarize_attempts(
            initial + retry, True,
        )
        self.assertEqual(len(attempts), 12)
        self.assertEqual(flaky, ["HarvestUITests/testCase0()"])
        self.assertEqual(len(failed), 11)
        self.assertEqual(len(errors), 11)
        self.assertTrue(all(
            len(attempts[f"HarvestUITests/testCase{index}()"]) == 2
            for index in range(12)
        ))

    def test_retry_selection_uses_only_proven_failed_identifiers(self):
        events = runner.TestEvents()
        events.invocation_started = 1
        events.invocation_finished = 1
        events.action_started = 1
        events.action_finished = 1
        events.tests = [
            {"identifier": f"HarvestUITests/testCase{index}()",
             "status": "Failure" if index == 3 else "Success"}
            for index in range(12)
        ]
        events.failure_issues = ["HarvestUITests/testCase3()"]
        arguments = ["-only-testing:PrintFarmerUITests/HarvestUITests"]
        self.assertEqual(
            runner.retry_selection(arguments, events, 65),
            ["-only-testing:PrintFarmerUITests/HarvestUITests/testCase3"],
        )
        events.unclassified_issues = ["unattributed issue"]
        self.assertEqual(runner.retry_selection(arguments, events, 65), [])
        events.unclassified_issues = []
        self.assertEqual(runner.retry_selection(arguments, events, 0), [])
        events.failure_issues = []
        self.assertEqual(runner.retry_selection(arguments, events, 65), [])

    def test_partial_line_is_retried_without_losing_event(self):
        events = runner.TestEvents()
        text = event("testStarted")
        stream = io.StringIO(text[:-1])
        events.read(stream, 1)
        self.assertEqual(events.active, set())
        self.assertEqual(stream.tell(), 0)
        events.read(io.StringIO(text), 2)
        self.assertEqual(events.active, {"Probe/test()"})

    def test_idle_deadline_continues_across_attempt_restart(self):
        budgets = runner.SharedBudgets()
        first = runner.TestEvents()
        first.idle_since = 1
        budgets.observe(first, 1)
        second = runner.TestEvents()
        budgets.observe(second, 3)
        self.assertEqual(budgets.idle_elapsed(3), 2)
        second.active.add("Probe/test()")
        budgets.observe(second, 4)
        self.assertIsNone(budgets.idle_started)
        second.active.clear()
        second.idle_since = 4
        budgets.observe(second, 4)
        self.assertEqual(budgets.idle_elapsed(6), 2)

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
        self.assertIn("needs: [select, test]", matrix)
        self.assertIn(
            "if: ${{ !cancelled() && needs.test.result == 'success' && "
            "needs.select.outputs.should_run != 'false' }}",
            matrix,
        )
        self.assertNotIn("needs.select.result == 'success'", matrix)
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
            "PrintFarmerUITests/LoginFlowUITests",
            "PrintFarmerUITests/OperatorShellUITests",
            "PrintFarmerUITests/ScanStationUITests",
            "PrintFarmerUITests/HarvestUITests",
            "PrintFarmerUITests/PartsInventoryUITests",
            "PrintFarmerUITests/PrinterListUITests",
            "PrintFarmerUITests/RootTitleAlignmentLargeTypeUITests/testFarmTitleSharesQueueAndFilamentLeadingInset",
            "PrintFarmerUITests/RootTitleAlignmentLargeTypeUITests/testFilamentFilterChipsScrollOnOneLineAtLargeType",
            "PrintFarmerUITests/FilamentCoverageUITests",
            "PrintFarmerUITests/ColdOfflineShellUITests",
            "PrintFarmerUITests/UIWaitBudgetTests",
            "PrintFarmerUITests/PrinterDetailHeaderUITests",
        ]
        self.assertEqual(Counter(selectors_by_family["iPhone"]), Counter(shared))
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
                        "PrintFarmerUITests/OperatorShellUITests",
                    ],
                )

        declarations = set()
        sources_by_suite = {}
        superclasses_by_suite = {}
        for source in (mobile / "PrintFarmerUITests").glob("*.swift"):
            contents = source.read_text()
            suite_declarations = list(re.finditer(
                r"\bclass\s+(\w+)\s*:\s*([\w.]+)",
                contents,
            ))
            for index, declaration in enumerate(suite_declarations):
                suite = declaration.group(1)
                declarations.add(suite)
                superclasses_by_suite[suite] = declaration.group(2).rsplit(".", 1)[-1]
                next_declaration = (
                    suite_declarations[index + 1].start()
                    if index + 1 < len(suite_declarations)
                    else len(contents)
                )
                sources_by_suite[suite] = contents[declaration.start():next_declaration]

        def test_methods_for_suite(suite):
            methods = set()
            visited = set()
            while suite in sources_by_suite and suite not in visited:
                visited.add(suite)
                methods.update(re.findall(
                    r"\bfunc\s+(test\w+)\s*\(",
                    sources_by_suite[suite],
                ))
                suite = superclasses_by_suite.get(suite)
                if suite is None:
                    break
            return methods

        for selectors in selectors_by_family.values():
            for selector in selectors:
                selector_parts = selector.split("/")
                suite = selector_parts[1]
                with self.subTest(suite=suite):
                    self.assertIn(suite, declarations, "A stale class selector executes zero XCTest cases")
                    if suite in sources_by_suite:
                        self.assertRegex(
                            sources_by_suite[suite],
                            r"\bfunc\s+test\w+\s*\(",
                            "A selected XCUI class must contain real tests, not an empty compatibility stub",
                        )
                if len(selector_parts) == 3:
                    with self.subTest(selector=selector):
                        self.assertIn(
                            selector_parts[2],
                            test_methods_for_suite(suite),
                            "A stale method selector executes zero XCTest cases",
                        )
        for family, selectors in selectors_by_family.items():
            selected_methods_by_suite = {}
            suite_selectors_by_suite = {}
            for selector in selectors:
                selector_parts = selector.split("/")
                suite = selector_parts[1]
                suite_selectors_by_suite.setdefault(suite, []).append(selector)
                if len(selector_parts) == 3:
                    selected_methods_by_suite.setdefault(suite, set()).add(selector_parts[2])
            for suite, selected_methods in selected_methods_by_suite.items():
                if all(
                    len(selector.split("/")) == 3
                    for selector in suite_selectors_by_suite[suite]
                ):
                    source_methods = test_methods_for_suite(suite)
                    with self.subTest(family=family, suite=suite):
                        self.assertEqual(
                            selected_methods,
                            source_methods,
                            "Method-selected XCUI suites must select every source test method",
                        )


class RunnerTests(unittest.TestCase):
    # Tests asserting a non-timeout outcome use these budgets only as hang detectors.
    # The shared post-test idle window deliberately spans attempt teardown and the
    # retry relaunch (test_idle_deadline_continues_across_attempt_restart), so it
    # must absorb a cold interpreter exec of the stub, which exceeds 0.2s on macOS
    # hosts. Timeout behaviour is proved separately with explicit tight budgets.
    OUTCOME_INVOCATION_TIMEOUT = 30
    OUTCOME_FINALIZATION_TIMEOUT = 10
    TIGHT_INVOCATION_TIMEOUT = 1.5
    TIGHT_FINALIZATION_TIMEOUT = 0.3

    def setUp(self):
        self.directory = Path("build/runner-tests") / uuid.uuid4().hex
        self.directory.mkdir(parents=True)
        self.bundle = self.directory / "Results.xcresult"
        self.arguments = [
            "test", "-destination", "platform=iOS Simulator,id=test-device",
            "-only-testing:PrintFarmerUITests/Probe",
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
            "marker=Path(os.environ['RUNNER_TEST_MARKER'])\n"
            "attempt=int(marker.read_text()) if marker.exists() else 0\n"
            "marker.write_text(str(attempt+1))\n"
            "def interrupt(sig, frame):\n"
            "    (bundle/'flushed').write_text('interrupted')\n"
            "    time.sleep(float(os.environ.get('RUNNER_TEST_FLUSH_DELAY', '0')))\n"
            "    sys.exit(0)\n"
            "signal.signal(signal.SIGINT, interrupt)\n"
            "def emit(name):\n"
            "    with stream.open('a') as f: f.write(os.environ[name])\n"
            "if mode=='no-events': time.sleep(10)\n"
            "if mode=='bad-stream':\n"
            "    stream.write_text('bad json\\n'); time.sleep(10)\n"
            "if mode not in ('no-events','bad-stream'):\n"
            "    emit('RUNNER_INVOCATION_START'); emit('RUNNER_ACTION_START')\n"
            "    if mode!='setup-error':\n"
            "        if mode=='retry-full-shard':\n"
            "            for line in json.loads(os.environ[f'RUNNER_TEST_EVENTS_{attempt}']):\n"
            "                with stream.open('a') as f: f.write(line)\n"
            "        else:\n"
            "            emit('RUNNER_TEST_START')\n"
            "            if attempt>0 and mode.startswith('retry'):\n"
            "                emit('RUNNER_TEST_RETRY_FINISH')\n"
            "                if mode=='retry-third':\n"
            "                    emit('RUNNER_TEST_START'); emit('RUNNER_TEST_RETRY_FINISH')\n"
            "                if os.environ['RUNNER_TEST_RETRY_STATUS'] in ('Failure','Failed'):\n"
            "                    emit('RUNNER_TEST_ISSUE')\n"
            "            else:\n"
            "                time.sleep(float(os.environ.get('RUNNER_TEST_BODY_SECONDS', '0.06')))\n"
            "                emit('RUNNER_TEST_FINISH')\n"
            "                if mode.startswith('retry'):\n"
            "                    if mode!='retry-no-issue': emit('RUNNER_TEST_ISSUE')\n"
            "    if mode=='retry-unclassified' and attempt==0:\n"
            "        emit('RUNNER_UNCLASSIFIED_ISSUE')\n"
            "    emit('RUNNER_ACTION_END')\n"
            "    if mode!='incomplete': emit('RUNNER_TEST_END')\n"
            "if mode=='hang': time.sleep(10)\n"
            "if mode=='cancel':\n"
            "    os.kill(os.getppid(), signal.SIGTERM); time.sleep(10)\n"
            "if mode=='ignore-interrupt':\n"
            "    signal.signal(signal.SIGINT, signal.SIG_IGN)\n"
            "    child=subprocess.Popen([sys.executable, '-c', "
            "'import signal,time; signal.signal(signal.SIGINT, signal.SIG_IGN); time.sleep(10)'])\n"
            "    (bundle/'child.pid').write_text(str(child.pid))\n"
            "    time.sleep(10)\n"
            "status=os.environ.get('RUNNER_TEST_STATUS','Success')\n"
            "retry_status=os.environ.get('RUNNER_TEST_RETRY_STATUS','Success')\n"
            "failed=(mode=='failed' or mode=='retry-native-failure' or "
            "mode=='setup-error' or (mode.startswith('retry') and "
            "((attempt==0 and status in ('Failure','Failed')) or "
            "(attempt>0 and retry_status in ('Failure','Failed')))))\n"
            "sys.exit(65 if failed else 42 if mode=='other-failure' else 0)\n"
        )
        self.stub.chmod(0o755)

    def tearDown(self):
        shutil.rmtree(self.directory)

    def invoke(self, mode, status="Success", retry_failed_tests=False,
               retry_status="Success", extra_environment=None, *,
               invocation_timeout=OUTCOME_INVOCATION_TIMEOUT,
               finalization_timeout=OUTCOME_FINALIZATION_TIMEOUT, flush_seconds=0.1):
        marker = self.directory / "attempt-count"
        marker.unlink(missing_ok=True)
        environment = {
            "RUNNER_TEST_MODE": mode,
            "RUNNER_TEST_MARKER": str(marker),
            "RUNNER_TEST_STATUS": status,
            "RUNNER_TEST_RETRY_STATUS": retry_status,
            "RUNNER_TEST_START": event("testStarted"),
            "RUNNER_TEST_FINISH": event("testFinished", status=status),
            "RUNNER_TEST_ISSUE": event("issueEmitted"),
            "RUNNER_UNCLASSIFIED_ISSUE": event(
                "issueEmitted", severity="testRunnerFailure",
            ),
            "RUNNER_INVOCATION_START": event("invocationStarted"),
            "RUNNER_ACTION_START": event("actionStarted"),
            "RUNNER_ACTION_END": event("actionFinished"),
            "RUNNER_TEST_END": event("invocationFinished"),
            "RUNNER_TEST_RETRY_FINISH": event("testFinished", status=retry_status),
            **(extra_environment or {}),
        }
        with patch.dict(os.environ, environment), patch("sys.stdout", new=io.StringIO()):
            code = runner.run(
                self.arguments, invocation_timeout=invocation_timeout,
                finalization_timeout=finalization_timeout,
                flush_seconds=flush_seconds, poll_seconds=0.01, executable=str(self.stub),
                retry_failed_tests=retry_failed_tests,
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
        self.assertNotIn("-retry-tests-on-failure", args)
        self.assertEqual(summary["max_attempts_per_test"], 1)
        self.assertAlmostEqual(summary["invocation_seconds"],
                               summary["reported_test_seconds"] +
                               summary["non_test_overhead_seconds"])

    def test_native_failures_are_not_hidden(self):
        code, summary = self.invoke("failed", status="Failure")
        self.assertEqual(code, 65)
        self.assertIsNone(summary["termination_reason"])

    def test_retry_success_is_flaky_and_all_attempts_are_preserved(self):
        step_summary = self.directory / "summary.md"
        with patch.dict(os.environ, {"GITHUB_STEP_SUMMARY": str(step_summary)}):
            code, summary = self.invoke("retry-success", status="Failure", retry_failed_tests=True)
        self.assertEqual(code, 0)
        self.assertEqual(summary["flaky_tests"], ["Probe/test()"])
        self.assertEqual(summary["max_attempts_per_test"], 2)
        self.assertEqual([test["status"] for test in summary["tests"]], ["Failure", "Success"])
        self.assertEqual(summary["reported_test_seconds"], 0.1)
        self.assertEqual(
            summary["retry_selectors"],
            ["-only-testing:PrintFarmerUITests/Probe/test"],
        )
        self.assertIn("Passed on retry (flaky): 1", step_summary.read_text())
        args = json.loads((self.bundle / "arguments.json").read_text())
        self.assertNotIn("-retry-tests-on-failure", args)
        retry_bundle = self.bundle.with_name("Results-retry.xcresult")
        retry_args = json.loads((retry_bundle / "arguments.json").read_text())
        self.assertIn("-only-testing:PrintFarmerUITests/Probe/test", retry_args)
        self.assertEqual(retry_args[retry_args.index("-resultBundlePath") + 1],
                         str(retry_bundle))
        self.assertTrue(retry_bundle.with_suffix(".events.jsonl").exists())
        self.assertTrue(retry_bundle.with_suffix(".timing.json").exists())
        self.assertTrue(
            self.bundle.with_name("Results-first.timing.json").exists()
        )
        self.assertEqual(summary["invocation_budget_seconds"],
                         self.OUTCOME_INVOCATION_TIMEOUT)
        self.assertEqual(summary["finalization_budget_seconds"],
                         self.OUTCOME_FINALIZATION_TIMEOUT)

    def test_unexpected_full_shard_retry_is_rejected_and_retained(self):
        self.bundle = self.directory / "FullShard.xcresult"
        self.arguments[-1] = str(self.bundle)
        self.arguments[self.arguments.index(
            "-only-testing:PrintFarmerUITests/Probe"
        )] = "-only-testing:PrintFarmerUITests/HarvestUITests"
        initial = []
        retry = []
        for index in range(12):
            identifier = f"HarvestUITests/testCase{index}()"
            first_status = "Failure" if index == 0 else "Success"
            initial.extend((
                event("testStarted", identifier=identifier),
                event("testFinished", identifier=identifier, status=first_status),
            ))
            retry.extend((
                event("testStarted", identifier=identifier),
                event("testFinished", identifier=identifier),
            ))
            if index == 0:
                initial.append(event("issueEmitted", identifier=identifier))
        code, summary = self.invoke(
            "retry-full-shard", status="Failure", retry_failed_tests=True,
            extra_environment={
                "RUNNER_TEST_EVENTS_0": json.dumps(initial),
                "RUNNER_TEST_EVENTS_1": json.dumps(retry),
            },
        )
        self.assertEqual(code, 70)
        self.assertEqual(summary["xcodebuild_exit_codes"], [65, 0])
        self.assertEqual(len(summary["attempts_by_test"]), 12)
        self.assertEqual(len(summary["repetition_errors"]), 12)
        self.assertEqual(len(summary["tests"]), 24)
        self.assertEqual(
            summary["retry_selectors"],
            ["-only-testing:PrintFarmerUITests/HarvestUITests/testCase0"],
        )
        retry_args = json.loads((
            self.bundle.with_name("FullShard-retry.xcresult") / "arguments.json"
        ).read_text())
        self.assertIn(
            "-only-testing:PrintFarmerUITests/HarvestUITests/testCase0",
            retry_args,
        )
        self.assertFalse(any(
            argument == "-only-testing:PrintFarmerUITests/HarvestUITests"
            for argument in retry_args
        ))

    def test_retry_still_failing_keeps_job_failed(self):
        code, summary = self.invoke("retry-failed", status="Failure",
                                    retry_failed_tests=True, retry_status="Failure")
        self.assertEqual(code, 65)
        self.assertEqual(summary["failed_tests"], ["Probe/test()"])
        self.assertEqual(summary["flaky_tests"], [])

    def test_flaky_warning_is_visible_in_github_actions(self):
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "true"}), \
                patch("sys.stdout", new=io.StringIO()) as output:
            environment = {
                "RUNNER_TEST_MODE": "retry-success",
                "RUNNER_TEST_MARKER": str(self.directory / "warning-attempt-count"),
                "RUNNER_TEST_START": event("testStarted"),
                "RUNNER_TEST_FINISH": event("testFinished", status="Failure"),
                "RUNNER_TEST_ISSUE": event("issueEmitted"),
                "RUNNER_INVOCATION_START": event("invocationStarted"),
                "RUNNER_ACTION_START": event("actionStarted"),
                "RUNNER_ACTION_END": event("actionFinished"),
                "RUNNER_TEST_RETRY_FINISH": event("testFinished"),
                "RUNNER_TEST_END": event("invocationFinished"),
                "RUNNER_TEST_STATUS": "Failure",
                "RUNNER_TEST_RETRY_STATUS": "Success",
            }
            with patch.dict(os.environ, environment):
                self.assertEqual(runner.run(
                    self.arguments, executable=str(self.stub), retry_failed_tests=True,
                ), 0)
        self.assertIn('::warning::Passed on retry (flaky): "Probe/test()"', output.getvalue())

    def test_retry_native_failure_without_failed_retry_result_fails_closed(self):
        code, summary = self.invoke("retry-native-failure", status="Failure",
                                    retry_failed_tests=True)
        self.assertEqual(code, 70)
        self.assertEqual(summary["xcodebuild_exit_codes"], [65, 65])

    def test_third_attempt_or_retry_without_opt_in_fails_closed(self):
        step_summary = self.directory / "anomaly-summary.md"
        with patch.dict(os.environ, {"GITHUB_STEP_SUMMARY": str(step_summary)}):
            code, summary = self.invoke("retry-third", status="Failure", retry_failed_tests=True)
        self.assertEqual(code, 70)
        self.assertEqual(len(summary["attempts_by_test"]["Probe/test()"]), 3)
        self.assertEqual(summary["failed_tests"], ["Probe/test()"])
        self.assertEqual(summary["flaky_tests"], [])
        self.assertGreaterEqual(len(summary["repetition_errors"]), 1)
        self.assertIn("Tests still failing: 1", step_summary.read_text())
        self.assertNotIn("Tests still failing: 0", step_summary.read_text())
        self.bundle = self.directory / "Disabled.xcresult"
        self.arguments[-1] = str(self.bundle)
        code, summary = self.invoke("retry-success", status="Failure")
        self.assertEqual(code, 65)
        self.assertEqual(summary["xcodebuild_exit_codes"], [65])

    def test_opt_in_does_not_retry_setup_or_unclassified_failures(self):
        code, summary = self.invoke("setup-error", retry_failed_tests=True)
        self.assertEqual(code, 65)
        self.assertEqual(summary["xcodebuild_exit_codes"], [65])
        self.bundle = self.directory / "Unclassified.xcresult"
        self.arguments[-1] = str(self.bundle)
        code, summary = self.invoke(
            "retry-unclassified", status="Failure", retry_failed_tests=True,
        )
        self.assertEqual(code, 65)
        self.assertEqual(summary["xcodebuild_exit_codes"], [65])
        self.assertFalse(
            self.bundle.with_name("Unclassified-retry.xcresult").exists()
        )
        self.bundle = self.directory / "Incomplete.xcresult"
        self.arguments[-1] = str(self.bundle)
        code, summary = self.invoke(
            "incomplete", status="Failure", retry_failed_tests=True,
        )
        self.assertEqual(code, 70)
        self.assertEqual(summary["xcodebuild_exit_codes"], [0])
        self.assertFalse(
            self.bundle.with_name("Incomplete-retry.xcresult").exists()
        )

    def test_opt_in_does_not_repeat_other_native_failure(self):
        code, summary = self.invoke("other-failure", retry_failed_tests=True)
        self.assertEqual(code, 42)
        self.assertEqual(len(summary["tests"]), 1)
        self.assertEqual(summary["flaky_tests"], [])

    def test_xcodebuild_launch_error_is_retained_and_not_retried(self):
        with patch("sys.stdout", new=io.StringIO()):
            code = runner.run(
                self.arguments, executable=str(self.directory / "missing-xcodebuild"),
                retry_failed_tests=True,
            )
        summary = json.loads(self.bundle.with_suffix(".timing.json").read_text())
        self.assertEqual(code, 70)
        self.assertEqual(summary["xcodebuild_exit_codes"], [None])
        self.assertEqual(summary["termination_reason"], "xcodebuild-launch-error")
        self.assertFalse(self.bundle.with_name("Results-retry.xcresult").exists())

    def test_other_native_exit_is_preserved(self):
        self.assertEqual(self.invoke("other-failure")[0], 42)

    def test_failed_event_cannot_be_reported_as_success(self):
        self.assertEqual(self.invoke("success", status="Failure")[0], 65)

    def test_post_test_timeout_flushes_artifacts_but_stays_failed(self):
        code, summary = self.invoke(
            "hang", extra_environment={"RUNNER_TEST_FLUSH_DELAY": "0.2"},
            invocation_timeout=5, finalization_timeout=2.5, flush_seconds=2,
        )
        self.assertEqual(code, 124)
        self.assertEqual(summary["xcodebuild_exit_codes"], [0])
        self.assertEqual(summary["termination_reason"], "post-test-timeout")
        self.assertEqual((self.bundle / "flushed").read_text(), "interrupted")
        self.assertEqual(summary["flush_seconds_included"], 2)
        self.assertEqual(summary["finalization_budget_seconds"], 2.5)
        self.assertLess(summary["post_last_test_seconds"], 3)

    def test_hard_stop_uses_configured_deadline_deterministically(self):
        clock = type("Clock", (), {})()
        clock.nanoseconds = 0
        deadline_ns = int(self.TIGHT_FINALIZATION_TIMEOUT * 1_000_000_000)
        poll_ns = 10_000_000

        def monotonic():
            return clock.nanoseconds / 1_000_000_000

        def sleep(seconds):
            clock.nanoseconds += round(seconds * 1_000_000_000)
            self.assertLessEqual(
                clock.nanoseconds, deadline_ns + poll_ns,
                "The runner did not hard-stop at its configured deadline",
            )

        class Process:
            pid = 12345
            returncode = None

            def poll(self):
                return self.returncode

            def wait(self):
                return self.returncode

        process = Process()
        signals = []

        def popen(command, start_new_session):
            self.assertTrue(start_new_session)
            stream_path = Path(command[command.index("-resultStreamPath") + 1])
            stream_path.write_text("".join((
                event("invocationStarted"),
                event("actionStarted"),
                event("testStarted"),
                event("testFinished"),
                event("actionFinished"),
                event("invocationFinished"),
            )))
            return process

        def killpg(pid, sig):
            self.assertEqual(pid, process.pid)
            signals.append((clock.nanoseconds, sig))
            if sig == signal.SIGKILL:
                process.returncode = -signal.SIGKILL

        with patch.object(runner.time, "monotonic", side_effect=monotonic), \
                patch.object(runner.time, "sleep", side_effect=sleep), \
                patch.object(runner.subprocess, "Popen", side_effect=popen), \
                patch.object(runner.os, "killpg", side_effect=killpg), \
                patch("sys.stdout", new=io.StringIO()):
            code = runner.run(
                self.arguments,
                invocation_timeout=self.TIGHT_INVOCATION_TIMEOUT,
                finalization_timeout=self.TIGHT_FINALIZATION_TIMEOUT,
                flush_seconds=0.1,
                poll_seconds=0.01,
            )

        self.assertEqual(code, 124)
        self.assertEqual(
            signals,
            [
                (deadline_ns - 100_000_000, signal.SIGINT),
                (deadline_ns, signal.SIGKILL),
                (deadline_ns + poll_ns, signal.SIGKILL),
            ],
        )
        summary = json.loads(self.bundle.with_suffix(".timing.json").read_text())
        self.assertEqual(summary["termination_reason"], "post-test-timeout")
        self.assertEqual(
            summary["finalization_budget_seconds"], self.TIGHT_FINALIZATION_TIMEOUT,
        )
        self.assertEqual(summary["xcodebuild_exit_codes"], [-signal.SIGKILL])
        self.assertEqual(summary["tests"][0]["identifier"], "Probe/test()")
        self.assertTrue(self.bundle.with_suffix(".events.jsonl").read_text())

    def test_hard_stop_bounds_unresponsive_process_and_descendant(self):
        code, summary = self.invoke(
            "ignore-interrupt", invocation_timeout=self.TIGHT_INVOCATION_TIMEOUT,
            finalization_timeout=self.TIGHT_FINALIZATION_TIMEOUT,
        )
        self.assertEqual(code, 124)
        self.assertEqual(summary["xcodebuild_exit_codes"], [-signal.SIGKILL])
        self.assertEqual(summary["termination_reason"], "post-test-timeout")
        self.assertEqual(
            summary["finalization_budget_seconds"], self.TIGHT_FINALIZATION_TIMEOUT,
        )
        self.assertEqual(summary["tests"][0]["identifier"], "Probe/test()")
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
        # The body outlasts the idle budget; the post-body teardown is in-process only.
        code, summary = self.invoke(
            "long-body", extra_environment={"RUNNER_TEST_BODY_SECONDS": "1.5"},
            finalization_timeout=1,
        )
        self.assertEqual(code, 0)
        self.assertGreater(summary["invocation_seconds"],
                           summary["finalization_budget_seconds"])
        self.assertFalse((self.bundle / "flushed").exists())

    def test_missing_events_hit_separate_invocation_bound(self):
        code, summary = self.invoke(
            "no-events", invocation_timeout=self.TIGHT_INVOCATION_TIMEOUT,
            finalization_timeout=self.TIGHT_FINALIZATION_TIMEOUT,
        )
        self.assertEqual(code, 124)
        self.assertEqual(summary["termination_reason"], "invocation-timeout")
        self.assertIsNone(summary["post_last_test_seconds"])

    def test_malformed_stream_fails_closed(self):
        self.assertEqual(self.invoke("bad-stream")[0], 70)

    def test_incomplete_successful_stream_fails_closed(self):
        self.assertEqual(self.invoke("incomplete")[0], 70)

    def test_cancellation_stays_nonzero_after_graceful_flush(self):
        code, summary = self.invoke(
            "cancel", extra_environment={"RUNNER_TEST_FLUSH_DELAY": "0.2"},
            invocation_timeout=5, finalization_timeout=2.5, flush_seconds=2,
        )
        self.assertEqual(code, 143)
        self.assertEqual(summary["xcodebuild_exit_codes"], [0])
        self.assertEqual(summary["termination_reason"], "cancelled")
        self.assertEqual((self.bundle / "flushed").read_text(), "interrupted")

    def test_ci_shell_commands_keep_selectors_artifacts_and_pipeline_exit(self):
        mobile = SCRIPT.parents[1]
        workflow = (mobile.parent / ".github/workflows/ios-pr-ci.yml").read_text()
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
                "Run unit tests",
                None,
                "build/TestResults",
                "-only-testing:PrintFarmerTests/PrinterFilamentSectionTests",
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
        for index, (step, key, stem, selectors, mode, expected) in enumerate(cases):
            with self.subTest(step=step, key=key, selectors=selectors):
                directory = self.directory / f"workflow-case-{index}"
                directory.mkdir()
                (directory / "scripts").symlink_to(
                    mobile / "scripts", target_is_directory=True,
                )
                (directory / "PrintFarmerUITests").symlink_to(
                    mobile / "PrintFarmerUITests", target_is_directory=True,
                )
                block = workflow.split(f"      - name: {step}\n", 1)[1]
                block = block.split("\n      - name:", 1)[0]
                shell = textwrap.dedent(block.split("        run: |\n", 1)[1])
                if key is not None:
                    shell = shell.replace("${{ matrix.key }}", key)
                    shell = shell.replace("${{ matrix.shard }}", key.rsplit("-", 1)[1])
                    shell = shell.replace("$SELECTORS", selectors)
                (directory / stem).parent.mkdir(parents=True, exist_ok=True)
                environment = {
                    **os.environ,
                    "UNIT_TEST_SELECTOR": selectors.removeprefix("-only-testing:"),
                    "PATH": str(self.stub.parent.resolve()) + os.pathsep + os.environ["PATH"],
                    "SIMULATOR_UDID": "test-device",
                    "RUNNER_TEST_MARKER": str(
                        (directory / "workflow-attempt-count").resolve()
                    ),
                    "RUNNER_TEST_MODE": mode,
                    "RUNNER_TEST_STATUS": "Failure" if mode == "failed" else "Success",
                    "RUNNER_TEST_RETRY_STATUS": "Success",
                    "RUNNER_TEST_ISSUE": event("issueEmitted"),
                    "RUNNER_INVOCATION_START": event("invocationStarted"),
                    "RUNNER_ACTION_START": event("actionStarted"),
                    "RUNNER_ACTION_END": event("actionFinished"),
                    "RUNNER_TEST_START": event("testStarted"),
                    "RUNNER_TEST_FINISH": event("testFinished",
                                              status="Failure" if mode == "failed" else "Success"),
                    "RUNNER_TEST_END": event("invocationFinished"),
                }
                result = subprocess.run(
                    ["bash", "-c", shell], cwd=directory, env=environment,
                    capture_output=True, text=True, timeout=10,
                )
                self.assertEqual(result.returncode, expected, result.stdout + result.stderr)
                bundle = directory / f"{stem}.xcresult"
                args = json.loads((bundle / "arguments.json").read_text())
                self.assertNotIn("-retry-tests-on-failure", args)
                self.assertIn("test-without-building", args)
                expected_selectors = selectors.split()
                if step == "Run unit tests":
                    self.assertEqual(
                        [arg for arg in args if arg.startswith("-only-testing:")],
                        expected_selectors,
                    )
                if key is not None and key.endswith("-1") and step == "Run XCUI shard":
                    self.assertNotIn(
                        "-only-testing:PrintFarmerUITests/LoginFlowUITests",
                        args,
                    )
                    expected_selectors = expected_selectors[1:]
                for selector in expected_selectors:
                    self.assertIn(selector, args)
                for suffix in (".log", ".events.jsonl", ".timing.json"):
                    self.assertTrue((directory / f"{stem}{suffix}").exists())
                upload = workflow.split(f"      - name: {step}\n", 1)[1]
                upload = upload.split("uses: actions/upload-artifact@v7", 1)[1]
                upload = upload.split("retention-days:", 1)[0]
                if key is not None:
                    upload = upload.replace("${{ matrix.key }}", key)
                for suffix in (".xcresult", ".log", ".events.jsonl", ".timing.json"):
                    self.assertIn(f"mobile/{stem}{suffix}", upload)
                if key is not None:
                    self.assertTrue(
                        (directory / f"{stem}-first.timing.json").exists()
                    )
                    self.assertIn(f"mobile/{stem}-first.timing.json", upload)
                    for suffix in (
                        "-retry.xcresult", "-retry.events.jsonl",
                        "-retry.timing.json",
                    ):
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
