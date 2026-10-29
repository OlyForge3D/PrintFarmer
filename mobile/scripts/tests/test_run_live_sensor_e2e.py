import importlib.util
import json
import tempfile
import unittest
from contextlib import redirect_stdout
from io import StringIO
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "run-live-sensor-e2e.py"
spec = importlib.util.spec_from_file_location("run_live_sensor_e2e", SCRIPT)
harness = importlib.util.module_from_spec(spec)
spec.loader.exec_module(harness)

EXPECTED = (
    "PrinterSensorsLiveBackendUITests/"
    "testLiveSensorsSignalRControlsAndReconnectAgainstBothBackends()"
)


class VerifyNamedTestSucceededTests(unittest.TestCase):
    def verify(self, tests):
        with tempfile.TemporaryDirectory() as directory:
            timing = Path(directory) / "SensorsLive.timing.json"
            timing.write_text(json.dumps({"exit_code": 0, "tests": tests}))
            with redirect_stdout(StringIO()):
                harness.verify_named_test_succeeded(timing)

    def test_expected_identifier_matches_runner_selector(self):
        self.assertEqual(harness.EXPECTED_TEST_ID, EXPECTED)

    def test_single_successful_named_test_passes(self):
        self.verify([{"identifier": EXPECTED, "status": "Success"}])

    def test_skipped_named_test_is_rejected(self):
        with self.assertRaisesRegex(RuntimeError, "Skipped"):
            self.verify([{"identifier": EXPECTED, "status": "Skipped"}])

    def test_zero_tests_are_rejected(self):
        with self.assertRaises(RuntimeError):
            self.verify([])

    def test_other_or_extra_tests_are_rejected(self):
        with self.assertRaises(RuntimeError):
            self.verify([{"identifier": "Other/test()", "status": "Success"}])
        with self.assertRaises(RuntimeError):
            self.verify([
                {"identifier": EXPECTED, "status": "Success"},
                {"identifier": EXPECTED, "status": "Success"},
            ])

    def test_missing_timing_file_is_rejected(self):
        with self.assertRaisesRegex(RuntimeError, "No usable runner test results"):
            harness.verify_named_test_succeeded("/nonexistent/SensorsLive.timing.json")


if __name__ == "__main__":
    unittest.main()
