import importlib.util
import json
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from io import StringIO
from pathlib import Path


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
