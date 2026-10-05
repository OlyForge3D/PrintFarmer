import importlib.util
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location(
    "sampler", Path(__file__).with_name("sample-ui-test-processes.py")
)
sampler = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sampler)


class AppProcessSelectionTests(unittest.TestCase):
    def test_only_selected_simulator_app_is_sampled(self):
        listing = """
  101 /Users/test/Library/Developer/CoreSimulator/Devices/ABC/data/Containers/Bundle/Application/1/PrintFarmer.app/PrintFarmer --uitesting -AppleLanguages (en)
  102 /Users/test/Library/Developer/CoreSimulator/Devices/OTHER/data/Containers/Bundle/Application/1/PrintFarmer.app/PrintFarmer
  103 /Users/test/Library/Developer/CoreSimulator/Devices/ABC/data/Containers/Bundle/Application/1/PrintFarmerUITests-Runner.app/PrintFarmerUITests-Runner
  104 /Applications/PrintFarmer.app/PrintFarmer
  105 /Users/test/Library/Developer/CoreSimulator/Devices/ABC/data/Containers/Bundle/Application/1/PrintFarmer.app/PrintFarmerHelper
"""
        self.assertEqual(sampler.app_pids(listing, "ABC"), [101])

    def test_invalid_and_empty_process_lines_are_ignored(self):
        self.assertEqual(sampler.app_pids("\n 123\n not-a-pid /PrintFarmer.app/PrintFarmer", "ABC"), [])


if __name__ == "__main__":
    unittest.main()
