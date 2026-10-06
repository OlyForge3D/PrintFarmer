import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    "app_store_screenshots", Path(__file__).resolve().parents[1] / "app-store-screenshots.py"
)
capture = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(capture)


class ScreenshotPipelineTests(unittest.TestCase):
    def test_resolve_rejects_smaller_fallback_device(self):
        inventory = {"devices": {"com.apple.CoreSimulator.SimRuntime.iOS-26-5": [{
            "udid": "device",
            "deviceTypeIdentifier": "com.apple.CoreSimulator.SimDeviceType.iPhone-17",
        }]}}
        with patch.object(capture, "run", side_effect=["device\n", json.dumps(inventory)]):
            with self.assertRaisesRegex(RuntimeError, "required display class"):
                capture.resolve("iPhone")

    def test_resolve_restricts_prefix_and_accepts_required_display_class(self):
        device = {
            "udid": "device",
            "deviceTypeIdentifier": "com.apple.CoreSimulator.SimDeviceType.iPhone-17-Pro-Max",
        }
        inventory = {"devices": {"com.apple.CoreSimulator.SimRuntime.iOS-26-5": [device]}}
        with patch.object(capture, "run", side_effect=["device\n", json.dumps(inventory)]) as run:
            self.assertEqual(capture.resolve("iPhone"), device)
            env = run.call_args_list[0].kwargs["env"]
            self.assertEqual(env["IOS_SIMULATOR_DEVICE_PREFIX"], "iPhone 17 Pro Max")
            self.assertNotIn("GITHUB_ENV", env)

    def test_resolve_rejects_unapproved_runtime_inventory(self):
        inventory = {"devices": {"com.apple.CoreSimulator.SimRuntime.iOS-27-0": []}}
        with patch.object(capture, "run", side_effect=["device\n", json.dumps(inventory)]):
            with self.assertRaises(RuntimeError):
                capture.resolve("iPhone")

    def make_attachments(self, directory, screens):
        attachments = directory / "attachments"
        attachments.mkdir()
        manifest = []
        for index, screen in enumerate(screens):
            filename = f"{index}.png"
            (attachments / filename).write_bytes(b"native-png-bytes")
            manifest.append({
                "exportedFileName": filename,
                "suggestedHumanReadableName": f"app-store-{screen}_0_5DF22B22-86D6-4138-BDB8-BFD6DD649FAC.png",
                "isAssociatedWithFailure": False,
            })
        (attachments / "manifest.json").write_text(json.dumps([{"attachments": manifest}]))

    def test_export_requires_all_five_named_images_and_preserves_bytes(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(capture, "run"):
            directory = Path(temp)
            self.make_attachments(directory, capture.SCREENS)
            images = capture.export(directory / "Screenshots.xcresult", directory)
            self.assertEqual({p.name for p in images}, {f"{s}.png" for s in capture.SCREENS})
            for image in images:
                self.assertEqual(image.read_bytes(), b"native-png-bytes")

    def test_export_rejects_missing_or_duplicate_images(self):
        for screens in [capture.SCREENS[:-1], (*capture.SCREENS, capture.SCREENS[0])]:
            with self.subTest(screens=screens):
                with tempfile.TemporaryDirectory() as temp, patch.object(capture, "run"):
                    directory = Path(temp)
                    self.make_attachments(directory, screens)
                    with self.assertRaises(RuntimeError):
                        capture.export(directory / "Screenshots.xcresult", directory)


if __name__ == "__main__":
    unittest.main()
