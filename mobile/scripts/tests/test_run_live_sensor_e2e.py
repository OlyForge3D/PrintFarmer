import importlib.util
import json
import subprocess
import tempfile
import unittest
from contextlib import contextmanager, redirect_stderr, redirect_stdout
from io import StringIO
from pathlib import Path
from unittest.mock import patch
from xml.etree import ElementTree


SCRIPT = Path(__file__).resolve().parents[1] / "run-live-sensor-e2e.py"
spec = importlib.util.spec_from_file_location("run_live_sensor_e2e", SCRIPT)
harness = importlib.util.module_from_spec(spec)
spec.loader.exec_module(harness)

EXPECTED_BACKEND_UI = (
    "PrinterSensorsLiveBackendUITests/"
    "testLiveSensorsSignalRControlsAndReconnectAgainstBothBackends()"
)
EXPECTED_RETAINED_STATE = (
    "PrinterSensorLiveStateTests/"
    "testProductionPrinterStateReceivesHumidityAndAvailabilityAcrossSignalRReconnect()"
)
MOBILE = SCRIPT.parents[1]


class SimulatorPreflightTests(unittest.TestCase):
    @contextmanager
    def stubbed_main(self, responses):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            mobile = root / "mobile"
            developer = root / "Xcode"
            mobile.mkdir()
            developer.mkdir()
            with (
                patch.object(harness, "MOBILE", mobile),
                patch.object(harness, "DEVELOPER_DIR", developer),
                patch.object(harness, "API_URL", "http://localhost:5245"),
                patch.object(harness, "MOONRAKER_URL", "http://127.0.0.1:7125"),
                patch.object(harness, "ensure_port_free"),
                patch.object(harness, "run_logged", side_effect=RuntimeError("preflight complete")),
                patch.object(harness.subprocess, "run", side_effect=responses) as run,
                patch.dict(harness.os.environ, {}, clear=True),
                redirect_stdout(StringIO()),
            ):
                yield mobile, run

    def test_resolver_failure_is_reported_before_simulator_inventory(self):
        diagnostic = "Xcode 26.6 is unsupported on macOS 27.2.\n"
        responses = [
            subprocess.CompletedProcess([], 0, stdout="Xcode 26.6\nBuild version 17F113\n"),
            subprocess.CompletedProcess([], 1, stdout="", stderr=diagnostic),
        ]
        with self.stubbed_main(responses) as (mobile, run):
            with self.assertRaisesRegex(RuntimeError, "unsupported on macOS 27.2"):
                harness.main([])
            commands = [entry.args[0] for entry in run.call_args_list]
            self.assertEqual(commands, [
                ["xcodebuild", "-version"],
                [str(harness.REPO / "scripts/ci/resolve-ios-simulator.sh"), "--udid"],
            ])
            run_dir = next((mobile / "build").iterdir())
            self.assertEqual((run_dir / "destination.log").read_text(), diagnostic)
            self.assertFalse((run_dir / "runtimes.json").exists())
            self.assertFalse((run_dir / "devices.json").exists())

    def test_missing_resolver_udid_fails_before_simulator_inventory(self):
        responses = [
            subprocess.CompletedProcess([], 0, stdout="Xcode 26.6\nBuild version 17F113\n"),
            subprocess.CompletedProcess([], 0, stdout="", stderr="no destination\n"),
        ]
        with self.stubbed_main(responses) as (mobile, run):
            with self.assertRaisesRegex(RuntimeError, "returned no UDID"):
                harness.main([])
            self.assertEqual(len(run.call_args_list), 2)
            run_dir = next((mobile / "build").iterdir())
            self.assertEqual((run_dir / "destination.log").read_text(), "no destination\n")

    def test_successful_resolver_precedes_inventory_and_preserves_evidence(self):
        responses = [
            subprocess.CompletedProcess([], 0, stdout="Xcode 26.6\nBuild version 17F113\n"),
            subprocess.CompletedProcess([], 0, stdout="SIMULATOR-UDID\n", stderr="approved\n"),
            subprocess.CompletedProcess([], 0, stdout=b'{"runtimes": []}'),
            subprocess.CompletedProcess([], 0, stdout=b'{"devices": {}}'),
        ]
        with self.stubbed_main(responses) as (mobile, run):
            with self.assertRaisesRegex(RuntimeError, "preflight complete"):
                harness.main([])
            commands = [entry.args[0] for entry in run.call_args_list]
            self.assertEqual(commands, [
                ["xcodebuild", "-version"],
                [str(harness.REPO / "scripts/ci/resolve-ios-simulator.sh"), "--udid"],
                ["xcrun", "simctl", "list", "runtimes", "-j"],
                ["xcrun", "simctl", "list", "devices", "available", "-j"],
            ])
            run_dir = next((mobile / "build").iterdir())
            self.assertEqual(
                (run_dir / "destination.log").read_text(),
                "approved\nUDID: SIMULATOR-UDID\n",
            )
            self.assertEqual((run_dir / "runtimes.json").read_bytes(), b'{"runtimes": []}')
            self.assertEqual((run_dir / "devices.json").read_bytes(), b'{"devices": {}}')


class VerifyNamedTestSucceededTests(unittest.TestCase):
    def verify(self, proof, tests):
        with tempfile.TemporaryDirectory() as directory:
            timing = Path(directory) / "SensorsLive.timing.json"
            timing.write_text(json.dumps({"exit_code": 0, "tests": tests}))
            with redirect_stdout(StringIO()):
                harness.verify_named_test_succeeded(
                    timing,
                    harness.PROOFS[proof]["expected_test_id"],
                )

    def test_default_preserves_original_backend_ui_proof(self):
        args = harness.parse_args([])
        self.assertEqual(args.proof, "backend-ui")
        proof = harness.PROOFS[args.proof]
        self.assertEqual(proof["test_plan"], "PrintFarmerLiveSensorE2E")
        self.assertEqual(
            proof["test_name"],
            "PrintFarmerUITests/PrinterSensorsLiveBackendUITests/"
            "testLiveSensorsSignalRControlsAndReconnectAgainstBothBackends",
        )
        self.assertEqual(proof["expected_test_id"], EXPECTED_BACKEND_UI)

    def test_retained_state_proof_is_explicit_and_exact(self):
        args = harness.parse_args(["--proof", "retained-state"])
        proof = harness.PROOFS[args.proof]
        self.assertEqual(proof["test_plan"], "PrintFarmerLiveSensorStateE2E")
        self.assertEqual(
            proof["test_name"],
            "PrintFarmerTests/PrinterSensorLiveStateTests/"
            "testProductionPrinterStateReceivesHumidityAndAvailabilityAcrossSignalRReconnect",
        )
        self.assertEqual(proof["expected_test_id"], EXPECTED_RETAINED_STATE)

    def test_connection_waiter_waits_even_when_initial_state_matches(self):
        source = (
            MOBILE / "PrintFarmerTests/ViewModels/PrinterDetailViewModelTests.swift"
        ).read_text(encoding="utf-8")
        waiter = source.split("private func waitForConnectionState(", 1)[1].split(
            "private func waitForPrinterState(", 1
        )[0]
        before_wait, after_wait = waiter.split(
            "await fulfillment(of: [stateExpectation], timeout: 60)", 1
        )
        self.assertNotIn("return", before_wait)
        self.assertRegex(
            before_wait,
            r"if registration\.initial == expected \{\s*"
            r"stateExpectation\.fulfill\(\)\s*\}",
        )
        self.assertIn("defer { registration.subscription.cancel() }", before_wait)
        self.assertIn("guard service.connectionState == expected else", after_wait)

    def test_each_test_plan_selects_only_its_matching_proof(self):
        expected = {
            "backend-ui": (
                "PrintFarmerLiveSensorE2E.xctestplan",
                "PrintFarmerUITests",
                "F7060002000000000000D001",
                ["PrinterSensorsLiveBackendUITests"],
            ),
            "retained-state": (
                "PrintFarmerLiveSensorStateE2E.xctestplan",
                "PrintFarmerTests",
                "AFC2BA1C050C049765D60160",
                ["PrinterSensorLiveStateTests/"
                 "testProductionPrinterStateReceivesHumidityAndAvailabilityAcrossSignalRReconnect"],
            ),
        }
        for proof_name, (plan_name, target_name, target_id, selected_tests) in expected.items():
            proof = harness.PROOFS[proof_name]
            self.assertEqual(plan_name, f"{proof['test_plan']}.xctestplan")
            plan = json.loads((MOBILE / plan_name).read_text(encoding="utf-8"))
            target = plan["testTargets"][0]
            self.assertEqual(target["target"]["name"], target_name)
            self.assertEqual(target["target"]["identifier"], target_id)
            self.assertEqual(target["selectedTests"], selected_tests)
            self.assertEqual(len(plan["testTargets"]), 1)

    def test_each_proof_plan_is_registered_without_changing_scheme_default(self):
        scheme_path = (
            MOBILE
            / "PrintFarmer.xcodeproj/xcshareddata/xcschemes/PrintFarmer.xcscheme"
        )
        test_plans = ElementTree.parse(scheme_path).find("./TestAction/TestPlans")
        self.assertIsNotNone(test_plans)
        references = {
            entry.attrib["reference"]: entry.attrib
            for entry in test_plans.findall("TestPlanReference")
        }
        for proof in harness.PROOFS.values():
            plan_reference = f"container:{proof['test_plan']}.xctestplan"
            self.assertIn(plan_reference, references)
        self.assertEqual(
            references["container:PrintFarmer.xctestplan"].get("default"),
            "YES",
        )
        self.assertNotIn(
            "default",
            references["container:PrintFarmerLiveSensorStateE2E.xctestplan"],
        )

    def test_runner_command_selects_only_the_matching_plan_and_test(self):
        for proof_name, proof in harness.PROOFS.items():
            command = harness.build_test_command(
                proof_name,
                "SIMULATOR-UDID",
                Path("/tmp/SensorsLive.xcresult"),
            )
            self.assertEqual(
                command[command.index("-testPlan") + 1],
                proof["test_plan"],
            )
            self.assertEqual(
                [arg for arg in command if arg.startswith("-only-testing:")],
                ["-only-testing:" + proof["test_name"]],
            )
            self.assertIn(
                "platform=iOS Simulator,id=SIMULATOR-UDID",
                command,
            )

    def test_workflow_defaults_to_backend_and_offers_explicit_state_mode(self):
        workflow = MOBILE.parent / ".github/workflows/ios-live-sensor-e2e.yml"
        source = workflow.read_text(encoding="utf-8")
        self.assertIn("default: backend-ui", source)
        self.assertIn("type: choice", source)
        self.assertIn("- backend-ui\n          - retained-state", source)
        self.assertIn(
            "SENSOR_E2E_PROOF: ${{ github.event.inputs.proof || 'backend-ui' }}",
            source,
        )
        self.assertIn(
            'python3 scripts/run-live-sensor-e2e.py --proof "$SENSOR_E2E_PROOF"',
            source,
        )

    def test_single_successful_named_test_passes(self):
        self.verify(
            "backend-ui",
            [{"identifier": EXPECTED_BACKEND_UI, "status": "Success"}],
        )
        self.verify(
            "retained-state",
            [{"identifier": EXPECTED_RETAINED_STATE, "status": "Success"}],
        )

    def test_skipped_named_test_is_rejected(self):
        for proof, expected in (
            ("backend-ui", EXPECTED_BACKEND_UI),
            ("retained-state", EXPECTED_RETAINED_STATE),
        ):
            with self.subTest(proof=proof), self.assertRaisesRegex(RuntimeError, "Skipped"):
                self.verify(proof, [{"identifier": expected, "status": "Skipped"}])

    def test_zero_tests_are_rejected(self):
        for proof in ("backend-ui", "retained-state"):
            with self.subTest(proof=proof), self.assertRaises(RuntimeError):
                self.verify(proof, [])

    def test_other_or_extra_tests_are_rejected(self):
        for proof, expected in (
            ("backend-ui", EXPECTED_BACKEND_UI),
            ("retained-state", EXPECTED_RETAINED_STATE),
        ):
            with self.subTest(proof=proof), self.assertRaises(RuntimeError):
                self.verify(proof, [{"identifier": "Other/test()", "status": "Success"}])
            with self.subTest(proof=proof), self.assertRaises(RuntimeError):
                self.verify(proof, [
                    {"identifier": expected, "status": "Success"},
                    {"identifier": expected, "status": "Success"},
                ])

    def test_unknown_proof_is_rejected(self):
        with self.assertRaises(SystemExit):
            with redirect_stdout(StringIO()), redirect_stderr(StringIO()):
                harness.parse_args(["--proof", "all"])

    def test_missing_timing_file_is_rejected(self):
        with self.assertRaisesRegex(RuntimeError, "No usable runner test results"):
            harness.verify_named_test_succeeded(
                "/nonexistent/SensorsLive.timing.json",
                harness.PROOFS["retained-state"]["expected_test_id"],
            )


if __name__ == "__main__":
    unittest.main()
