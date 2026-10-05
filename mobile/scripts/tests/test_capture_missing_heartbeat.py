import importlib.util
import json
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location(
    "capture", Path(__file__).parents[1] / "capture-missing-heartbeat.py"
)
capture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(capture)


class CaptureSelectionTests(unittest.TestCase):
    def test_selects_only_exact_app_on_selected_simulator(self):
        listing = """
101 /Users/test/CoreSimulator/Devices/ABC/data/PrintFarmer.app/PrintFarmer --uitesting
102 /Users/test/CoreSimulator/Devices/OTHER/data/PrintFarmer.app/PrintFarmer --uitesting
103 /Users/test/CoreSimulator/Devices/ABC/data/PrintFarmer.app/PrintFarmerHelper
104 /Users/test/CoreSimulator/Devices/ABC/data/PrintFarmerUITests-Runner.app/PrintFarmerUITests-Runner
invalid /Users/test/CoreSimulator/Devices/ABC/data/PrintFarmer.app/PrintFarmer
"""
        self.assertEqual(capture.app_pids(listing, "ABC"), [101])

    def test_parses_ready_and_missing_heartbeat(self):
        for kind, suffix in [("READY", ""), ("MISSING", " beat=40 uptime=105.500 stalled=5.000")]:
            line = json.dumps({
                "processID": 101,
                "eventMessage": f"PFARM_STALL_CAPTURE_{kind} pid=101{suffix}",
            })
            self.assertEqual(capture.capture_event(line), (kind, 101))

    def test_rejects_malformed_or_mismatched_events(self):
        for value in ["stream starting", "[]", "null", "{}",
                      json.dumps({"processID": 102, "eventMessage": "PFARM_STALL_CAPTURE_READY pid=101"}),
                      json.dumps({"processID": "101", "eventMessage": "PFARM_STALL_CAPTURE_READY pid=101"}),
                      json.dumps({"processID": 101, "eventMessage": "Other pid=101"}),
                      json.dumps({"processID": 0, "eventMessage": "PFARM_STALL_CAPTURE_READY pid=0"})]:
            self.assertIsNone(capture.capture_event(value))


if __name__ == "__main__":
    unittest.main()
