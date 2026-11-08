import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import subprocess
import sys

MOBILE = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("report_ui_coverage", MOBILE / "scripts/report-ui-coverage.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class CoverageReportTests(unittest.TestCase):
    def setUp(self):
        self.catalog = MODULE.read_json(MOBILE / "ui-coverage/catalog.json")
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def candidate(self, scenario="01-farm"):
        image = self.root / "image.png"
        image.write_bytes(b"synthetic-image-contract-test-not-native-evidence")
        receipt = self.root / "receipt.json"
        receipt.write_text(json.dumps({
            "scenarioId": scenario, "audit": "passed",
            "sha256": hashlib.sha256(image.read_bytes()).hexdigest(),
            "appearance": "dark", "contentSize": "UICTContentSizeCategoryL",
            "runtime": "26.5", "device": "iPhone17,3",
            "locale": "en_US", "orientation": "portrait",
        }), encoding="utf-8")
        return {f"catalog.{scenario}.json": receipt, f"catalog.{scenario}.png": image}

    def metadata(self):
        (self.root / "source-sha.txt").write_text("a" * 40)
        (self.root / "xcode.txt").write_text("Xcode 26.6\nBuild version 17F113\n")
        (self.root / "simulator-udid.txt").write_text("fixture-udid")
        runtime = "com.apple.CoreSimulator.SimRuntime.iOS-26-5"
        (self.root / "runtimes.json").write_text(json.dumps({"runtimes": [
            {"identifier": runtime, "isAvailable": True, "buildversion": "23F77"},
        ]}))
        (self.root / "devices.json").write_text(json.dumps({"devices": {runtime: [
            {"udid": "fixture-udid", "isAvailable": True, "name": "iPhone 17"},
        ]}}))
        return self.root

    def test_no_host_blocks_every_row_without_claiming_coverage(self):
        result = MODULE.report(self.catalog, {}, "iPhone", "a" * 40, "No approved macOS host.")
        self.assertEqual(result["summary"]["blockedRows"], 37)
        self.assertEqual(result["summary"]["exercisedScenarios"], 0)
        self.assertEqual(result["summary"]["fullyCoveredRows"], 0)
        self.assertTrue(all("No approved macOS host." in row["reason"] for row in result["entries"]))

    def test_candidate_never_auto_approves_golden_or_whole_row(self):
        result = MODULE.report(self.catalog, self.candidate(), "iPhone", "a" * 40)
        self.assertEqual(result["scenarioResults"]["01-farm"]["status"], "awaiting-human-golden")
        self.assertEqual(result["summary"]["partiallyExercisedRows"], 1)
        self.assertEqual(result["summary"]["fullyCoveredRows"], 0)

    def test_mismatched_image_is_rejected(self):
        attachments = self.candidate()
        attachments["catalog.01-farm.png"].write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "Mismatched"):
            MODULE.report(self.catalog, attachments, "iPhone", "a" * 40)

    def test_wrong_device_or_rendering_profile_is_rejected(self):
        attachments = self.candidate()
        with self.assertRaisesRegex(ValueError, "device"):
            MODULE.report(self.catalog, attachments, "iPad", "a" * 40)
        receipt = MODULE.read_json(attachments["catalog.01-farm.json"])
        receipt["appearance"] = "light"
        attachments["catalog.01-farm.json"].write_text(json.dumps(receipt), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "appearance"):
            MODULE.report(self.catalog, attachments, "iPhone", "a" * 40)

    def test_failed_audit_is_rejected(self):
        attachments = self.candidate()
        receipt = MODULE.read_json(attachments["catalog.01-farm.json"])
        receipt["audit"] = "failed"
        attachments["catalog.01-farm.json"].write_text(json.dumps(receipt), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "audit"):
            MODULE.report(self.catalog, attachments, "iPhone", "a" * 40)

    def test_catalog_requires_all_rows_and_unique_scenarios(self):
        malformed = copy.deepcopy(self.catalog)
        malformed["entries"].pop()
        with self.assertRaisesRegex(ValueError, "37"):
            MODULE.validate_catalog(malformed)
        malformed = copy.deepcopy(self.catalog)
        malformed["entries"][0]["scenarioIds"] = ["01-farm"]
        with self.assertRaisesRegex(ValueError, "exactly one"):
            MODULE.validate_catalog(malformed)
        malformed = copy.deepcopy(self.catalog)
        malformed["entries"][0]["id"] = "ios.substituted-screen"
        with self.assertRaisesRegex(ValueError, "identities"):
            MODULE.validate_catalog(malformed)
        malformed = copy.deepcopy(self.catalog)
        malformed["source"]["sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "provenance"):
            MODULE.validate_catalog(malformed)

    def test_catalog_scenarios_match_real_capture_calls(self):
        import re
        source = (MOBILE / "PrintFarmerUITests/AppStoreScreenshotUITests.swift").read_text(encoding="utf-8")
        captures = re.findall(r'try capture\("([^"]+)"\)', source)
        self.assertEqual(set(MODULE.validate_catalog(self.catalog)), set(captures))
        self.assertEqual(len(captures), 7)
        self.assertIn("try app.performAccessibilityAudit(for: .all)", source)

    def test_cli_reports_explicit_blockers_and_rejects_missing_adapter_images(self):
        output = self.root / "report.json"
        command = [sys.executable, str(MOBILE / "scripts/report-ui-coverage.py"),
                   "--catalog", str(MOBILE / "ui-coverage/catalog.json"),
                   "--approvals", str(MOBILE / "ui-coverage/approved-goldens.json"),
                   "--family", "iPhone", "--head", "a" * 40, "--output", str(output)]
        result = subprocess.run(command + ["--blocked-reason", "No native host."],
                                capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(MODULE.read_json(output)["summary"]["blockedRows"], 37)
        (self.root / "manifest.json").write_text("[]", encoding="utf-8")
        result = subprocess.run(command + ["--attachments", str(self.root),
                                          "--metadata-directory", str(self.metadata())],
                                capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertEqual(MODULE.read_json(output)["summary"]["exercisedScenarios"], 0)

    def test_attachment_path_traversal_and_duplicates_are_rejected(self):
        with self.assertRaises(ValueError):
            MODULE.safe_file(self.root, "../outside.png")
        with self.assertRaises(ValueError):
            MODULE.safe_file(self.root, "..\\outside.png")
        attachment = {"suggestedHumanReadableName": "catalog-image-01-farm",
                      "exportedFileName": "image.png", "isAssociatedWithFailure": False}
        (self.root / "manifest.json").write_text(json.dumps([{"attachments": [attachment, attachment]}]))
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            MODULE.exported_attachments(self.root)

    def test_real_exporter_name_normalization_and_failure_flag(self):
        attachment = {"suggestedHumanReadableName": "catalog-image-01-farm_1_00000000-0000-0000-0000-000000000001.png",
                      "exportedFileName": "image.png", "isAssociatedWithFailure": False}
        (self.root / "manifest.json").write_text(json.dumps([{"attachments": [attachment]}]))
        self.assertEqual(MODULE.exported_attachments(self.root), {"catalog.01-farm.png": self.root / "image.png"})
        attachment["isAssociatedWithFailure"] = True
        (self.root / "manifest.json").write_text(json.dumps([{"attachments": [attachment]}]))
        with self.assertRaisesRegex(ValueError, "failed"):
            MODULE.exported_attachments(self.root)

    def test_native_toolchain_and_destination_metadata_is_required(self):
        directory = self.metadata()
        observed = MODULE.native_profile(directory, self.catalog, "iPhone", "a" * 40)
        self.assertEqual(observed["runtimeBuild"], "23F77")
        with self.assertRaisesRegex(ValueError, "source SHA"):
            MODULE.native_profile(directory, self.catalog, "iPhone", "b" * 40)
        with self.assertRaisesRegex(ValueError, "wrong-family"):
            MODULE.native_profile(directory, self.catalog, "iPad", "a" * 40)
        (self.root / "xcode.txt").write_text("Xcode 26.6\nBuild version WRONG\n")
        with self.assertRaisesRegex(ValueError, "Xcode"):
            MODULE.native_profile(directory, self.catalog, "iPhone", "a" * 40)

    def test_app_store_and_catalog_test_plans_are_isolated(self):
        store = MODULE.read_json(MOBILE / "AppStoreScreenshots.xctestplan")
        catalog = MODULE.read_json(MOBILE / "CatalogVisualAccessibility.xctestplan")
        default = MODULE.read_json(MOBILE / "PrintFarmer.xctestplan")
        self.assertEqual(store["testTargets"][0]["selectedTests"], ["AppStoreScreenshotUITests"])
        self.assertEqual(catalog["testTargets"][0]["selectedTests"],
                         ["CatalogVisualAccessibilityUITests/testCaptureCatalogWithAccessibilityAudits"])
        self.assertIn("CatalogVisualAccessibilityUITests", default["testTargets"][1]["skippedTests"])
        source = (MOBILE / "PrintFarmerUITests/AppStoreScreenshotUITests.swift").read_text(encoding="utf-8")
        store_class = source.split("final class AppStoreScreenshotUITests:")[1].split("@MainActor")[0]
        self.assertNotIn("testCaptureCatalog", store_class)
        self.assertIn("testCaptureStoreScreens", store_class)

    def test_human_approval_mismatch_is_a_regression_not_rebaseline(self):
        (self.root / "golden.png").write_bytes(b"human-reviewed-contract-fixture")
        golden_digest = hashlib.sha256((self.root / "golden.png").read_bytes()).hexdigest()
        approval = {"iPhone17,3.01-farm": {
            "reviewedBy": "human-review-required", "sourceSha": "b" * 40,
            "sha256": golden_digest, "profile": self.catalog["renderProfile"], "file": "golden.png",
        }}
        result = MODULE.report(self.catalog, self.candidate(), "iPhone", "a" * 40,
                               approvals=approval, golden_directory=self.root)
        self.assertEqual(result["scenarioResults"]["01-farm"]["status"], "visual-regression")
        self.assertEqual(approval["iPhone17,3.01-farm"]["sha256"], golden_digest)


if __name__ == "__main__":
    unittest.main()
