import json
import os
from pathlib import Path
import subprocess
import textwrap
import unittest

import test_run_tests as runner_tests


class HistoryMenuDiagnosticTests(unittest.TestCase):
    def test_only_authorized_ipad_test_runs_once_even_on_failure(self):
        mobile = Path(__file__).resolve().parents[2]
        workflow = (mobile.parent / ".github/workflows/ios-pr-ci.yml").read_text()
        block = workflow.split("      - name: Run XCUI shard\n", 1)[1]
        block = block.split("\n      - name:", 1)[0]
        shell = textwrap.dedent(block.split("        run: |\n", 1)[1])
        identifier = "HarvestUITests/testCompletedJobExposesHarvestAction()"
        for key in [f"{family}-{shard}" for family in ("iphone", "ipad") for shard in range(1, 5)]:
            for failed in (False, True):
                with self.subTest(key=key, failed=failed):
                    fixture = runner_tests.RunnerTests()
                    fixture.setUp()
                    try:
                        directory = fixture.directory.resolve()
                        (directory / "scripts").symlink_to(mobile / "scripts", target_is_directory=True)
                        stem = f"build-{key}/XCUIShard"
                        (directory / stem).parent.mkdir()
                        marker = directory / "attempt-count"
                        status = "Failure" if failed else "Success"
                        event = runner_tests.event
                        environment = {
                            **os.environ,
                            "HISTORY_MENU_DIAGNOSTIC": "1",
                            "SELECTORS": "-only-testing:PrintFarmerUITests/Unrelated",
                            "PATH": str(directory) + os.pathsep + os.environ["PATH"],
                            "SIMULATOR_UDID": "test-device",
                            "RUNNER_TEST_MODE": "retry-success" if failed else "success",
                            "RUNNER_TEST_STATUS": status,
                            "RUNNER_TEST_RETRY_STATUS": "Success",
                            "RUNNER_TEST_MARKER": str(marker),
                            "RUNNER_INVOCATION_START": event("invocationStarted"),
                            "RUNNER_ACTION_START": event("actionStarted"),
                            "RUNNER_ACTION_END": event("actionFinished"),
                            "RUNNER_TEST_START": event("testStarted", identifier),
                            "RUNNER_TEST_FINISH": event("testFinished", identifier, status=status),
                            "RUNNER_TEST_RETRY_FINISH": event("testFinished", identifier),
                            "RUNNER_TEST_ISSUE": event("issueEmitted", identifier),
                            "RUNNER_TEST_END": event("invocationFinished"),
                        }
                        command = shell.replace("${{ matrix.key }}", key).replace(
                            "${{ matrix.shard }}", key.rsplit("-", 1)[1]
                        )
                        result = subprocess.run(
                            ["bash", "-c", command], cwd=directory, env=environment,
                            capture_output=True, text=True, timeout=15,
                        )
                        expected = 65 if key == "ipad-2" and failed else 0
                        self.assertEqual(result.returncode, expected, result.stdout + result.stderr)
                        if key != "ipad-2":
                            self.assertFalse(marker.exists(), "No other shard may invoke XCTest")
                            continue
                        self.assertEqual(marker.read_text(), "1", "Failure must not retry")
                        args = json.loads((directory / f"{stem}.xcresult/arguments.json").read_text())
                        self.assertEqual(
                            [arg for arg in args if arg.startswith("-only-testing:")],
                            ["-only-testing:PrintFarmerUITests/HarvestUITests/testCompletedJobExposesHarvestAction"],
                        )
                        self.assertNotIn("-retry-tests-on-failure", args)
                        for suffix in (".log", ".events.jsonl", ".timing.json"):
                            self.assertTrue((directory / f"{stem}{suffix}").exists())
                        self.assertFalse((directory / f"{stem}-retry.xcresult").exists())
                    finally:
                        fixture.tearDown()


if __name__ == "__main__":
    unittest.main()
