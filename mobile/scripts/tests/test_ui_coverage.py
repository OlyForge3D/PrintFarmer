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
DEVICE_SPEC = importlib.util.spec_from_file_location("select_ui_device", MOBILE / "scripts/select-ui-coverage-device.py")
DEVICE_MODULE = importlib.util.module_from_spec(DEVICE_SPEC)
DEVICE_SPEC.loader.exec_module(DEVICE_MODULE)


class CoverageReportTests(unittest.TestCase):
    def setUp(self):
        self.catalog = MODULE.read_json(MOBILE / "ui-coverage/catalog.json")
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def candidate(self, scenario="01-farm"):
        profile = {**self.catalog["renderProfile"],
                   **self.catalog.get("scenarioProfileOverrides", {}).get(scenario, {})}
        image = self.root / f"image-{scenario}.png"
        image.write_bytes(b"synthetic-image-contract-test-not-native-evidence")
        receipt = self.root / f"receipt-{scenario}.json"
        receipt.write_text(json.dumps({
            "scenarioId": scenario, "audit": "passed",
            "sha256": hashlib.sha256(image.read_bytes()).hexdigest(),
            "appearance": profile["appearance"], "themeMode": profile["themeMode"],
            "contentSize": profile["contentSize"],
            "runtime": "26.5", "device": "iPhone17,3",
            "locale": "en_US", "orientation": profile["orientation"],
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

    def test_failed_audit_is_reported_without_certifying_row(self):
        attachments = self.candidate()
        receipt = MODULE.read_json(attachments["catalog.01-farm.json"])
        receipt["audit"] = "failed"
        receipt["auditIssues"] = "Dynamic Type font sizes are partially unsupported"
        attachments["catalog.01-farm.json"].write_text(json.dumps(receipt), encoding="utf-8")
        result = MODULE.report(self.catalog, attachments, "iPhone", "a" * 40)
        self.assertEqual(result["scenarioResults"]["01-farm"]["status"], "accessibility-failure")
        self.assertEqual(result["summary"]["failedRows"], 1)
        self.assertEqual(result["summary"]["failedAuditScenarios"], 1)
        self.assertEqual(result["summary"]["exercisedScenarios"], 1)
        self.assertEqual(result["summary"]["fullyCoveredRows"], 0)
        self.assertIn(receipt["auditIssues"], result["entries"][8]["reason"])

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
        self.assertEqual(len(captures), 52)
        self.assertIn("try app.performAccessibilityAudit(for: .all)", source)
        self.assertIn('auditIssues.append("\\(issue.compactDescription): \\(issue.detailedDescription)")', source)
        self.assertIn("XCTAssertTrue(catalogAuditFailures.isEmpty", source)

    def test_scenario_profiles_are_strict_and_cannot_change_environment_pins(self):
        for scenario in self.catalog["scenarioProfileOverrides"]:
            result = MODULE.report(self.catalog, self.candidate(scenario), "iPhone", "a" * 40)
            self.assertEqual(result["scenarioResults"][scenario]["status"], "awaiting-human-golden")
        attachments = self.candidate("51-viewport-landscape")
        receipt = MODULE.read_json(attachments["catalog.51-viewport-landscape.json"])
        receipt["orientation"] = "portrait"
        attachments["catalog.51-viewport-landscape.json"].write_text(json.dumps(receipt), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "orientation"):
            MODULE.report(self.catalog, attachments, "iPhone", "a" * 40)
        for overrides in ({"runtime": "27.0"}, {"orientation": "other"}, {"themeMode": "automatic"}):
            malformed = copy.deepcopy(self.catalog)
            malformed["scenarioProfileOverrides"]["51-viewport-landscape"] = overrides
            with self.assertRaisesRegex(ValueError, "environment pins"):
                MODULE.validate_catalog(malformed)
        malformed = copy.deepcopy(self.catalog)
        malformed["scenarioProfileOverrides"]["99-not-mapped"] = {"appearance": "light"}
        with self.assertRaisesRegex(ValueError, "mapped scenario"):
            MODULE.validate_catalog(malformed)

    def test_catalog_restoration_and_root_isolation_source_guards(self):
        # Lexical regression guards only; native execution must prove frame stability.
        source = (MOBILE / "PrintFarmerUITests/AppStoreScreenshotUITests.swift").read_text(encoding="utf-8")
        capture = source.split("    func capture(_ name: String) throws {")[1].split("\n@MainActor")[0]
        self.assertIn("if capturesCatalog && auditedFrame == nil", capture)
        self.assertIn("if let auditedFrame, data != auditedFrame { continue }", capture)
        self.assertIn("guard identicalFrames >= 3 else { continue }", capture)
        self.assertIn("guard confirmed.pngRepresentation == data else", capture)
        restoration = capture.split("auditedFrame = data\n")[1].split("\n            }")[0]
        self.assertIn("deadline = ProcessInfo.processInfo.systemUptime + 20", restoration)
        self.assertIn("identicalFrames = 0", restoration)
        self.assertIn("continue", restoration)
        self.assertIn('issues.name = "catalog-unsettled-\\(name)-audit-issues"', capture)
        root = source.split("final class CatalogRootVisualAccessibilityUITests:")[1]
        for method, scenario in [
            ("testCaptureLaunchSplash", "33-launch-splash"),
            ("testCaptureFirstServer", "34-first-server"),
            ("testCaptureBackendLoading", "35-backend-loading"),
            ("testCaptureSnapshotActivation", "36-snapshot-activation"),
        ]:
            body = root.split(f"    func {method}() throws {{")[1].split("\n    }")[0]
            self.assertIn(f'try capture("{scenario}")', body)
            self.assertEqual(body.count("try capture("), 1)
        self.assertNotIn("testCaptureHeldRootStates", root)

    def test_account_navigation_selects_button_not_toolbar_wrapper(self):
        # The iPad toolbar exposes the identifier on both Other and Button nodes.
        source = (MOBILE / "PrintFarmerUITests/AppStoreScreenshotUITests.swift").read_text(encoding="utf-8")
        body = source.split("    private func openAccount() throws {")[1].split("\n    }")[0]
        self.assertIn('try tapRequired(app.buttons["navigation.account"].firstMatch)', body)
        self.assertNotIn("descendants(matching: .any)", body)

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

    def test_failed_audit_makes_cli_red_even_when_all_images_and_receipts_exist(self):
        manifest = []
        scenarios = MODULE.validate_catalog(self.catalog)
        for scenario in scenarios:
            attachments = self.candidate(scenario)
            if scenario == "01-farm":
                receipt_path = attachments[f"catalog.{scenario}.json"]
                receipt = MODULE.read_json(receipt_path)
                receipt.update(audit="failed", auditIssues="Dynamic Type partially unsupported")
                receipt_path.write_text(json.dumps(receipt), encoding="utf-8")
            for kind, extension in (("image", "png"), ("receipt", "json")):
                manifest.append({
                    "suggestedHumanReadableName": f"catalog-{kind}-{scenario}",
                    "exportedFileName": attachments[f"catalog.{scenario}.{extension}"].name,
                    "isAssociatedWithFailure": False,
                })
        (self.root / "manifest.json").write_text(json.dumps([{"attachments": manifest}]))
        output = self.root / "failed-audit-report.json"
        result = subprocess.run([
            sys.executable, str(MOBILE / "scripts/report-ui-coverage.py"),
            "--catalog", str(MOBILE / "ui-coverage/catalog.json"),
            "--approvals", str(MOBILE / "ui-coverage/approved-goldens.json"),
            "--family", "iPhone", "--head", "a" * 40,
            "--attachments", str(self.root), "--metadata-directory", str(self.metadata()),
            "--output", str(output),
        ], capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 1, result.stderr)
        summary = MODULE.read_json(output)["summary"]
        self.assertEqual(summary["exercisedScenarios"], len(scenarios))
        self.assertEqual(summary["failedAuditScenarios"], 1)
        self.assertEqual(summary["pendingHumanGoldens"], len(scenarios) - 1)
        self.assertEqual(summary["fullyCoveredRows"], 0)

    def test_cli_merges_exports_and_rejects_cross_export_duplicates(self):
        directories = []
        for index, scenario in enumerate(("01-farm", "22-login")):
            directory = self.root / f"export-{index}"
            directory.mkdir()
            manifest = []
            for name, path in self.candidate(scenario).items():
                (directory / path.name).write_bytes(path.read_bytes())
                kind = "receipt" if name.endswith(".json") else "image"
                manifest.append({
                    "suggestedHumanReadableName": f"catalog-{kind}-{scenario}",
                    "exportedFileName": path.name,
                    "isAssociatedWithFailure": False,
                })
            (directory / "manifest.json").write_text(json.dumps([{"attachments": manifest}]))
            directories.append(directory)
        output = self.root / "merged-report.json"
        command = [
            sys.executable, str(MOBILE / "scripts/report-ui-coverage.py"),
            "--catalog", str(MOBILE / "ui-coverage/catalog.json"),
            "--approvals", str(MOBILE / "ui-coverage/approved-goldens.json"),
            "--family", "iPhone", "--head", "a" * 40,
            "--metadata-directory", str(self.metadata()), "--output", str(output),
        ]
        result = subprocess.run(command + [
            "--attachments", str(directories[0]), "--attachments", str(directories[1]),
        ], capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertEqual(MODULE.read_json(output)["summary"]["exercisedScenarios"], 2)
        result = subprocess.run(command + [
            "--attachments", str(directories[0]), "--attachments", str(directories[0]),
        ], capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 2)
        self.assertIn("Duplicate native attachments across exports", result.stderr)

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
                         ["CatalogVisualAccessibilityUITests", "CatalogRootVisualAccessibilityUITests"])
        self.assertEqual(catalog["testTargets"][1]["selectedTests"], ["UITestBootstrapTests"])
        self.assertEqual(catalog["testTargets"][1]["target"], default["testTargets"][0]["target"])
        workflow = (MOBILE.parent / ".github/workflows/ios-ui-coverage.yml").read_text()
        self.assertIn("build-for-testing", workflow)
        self.assertIn("test-without-building", workflow)
        self.assertEqual(workflow.count("-testPlan CatalogVisualAccessibility"), 3)
        operator_capture = workflow.split("        id: capture\n")[1].split("      - name:")[0]
        root_capture = workflow.split("        id: capture_root\n")[1].split("      - name:")[0]
        for capture in (operator_capture, root_capture):
            self.assertIn("timeout-minutes: 25", capture)
            self.assertIn("test-without-building", capture)
        self.assertIn("-only-testing:PrintFarmerUITests/CatalogVisualAccessibilityUITests",
                      operator_capture)
        self.assertIn("-only-testing:PrintFarmerTests/UITestBootstrapTests", operator_capture)
        self.assertNotIn("CatalogRootVisualAccessibilityUITests", operator_capture)
        self.assertIn("-only-testing:PrintFarmerUITests/CatalogRootVisualAccessibilityUITests",
                      root_capture)
        self.assertNotIn("-only-testing:PrintFarmerUITests/CatalogVisualAccessibilityUITests",
                         root_capture)
        self.assertIn("steps.capture.outcome == 'failure'", root_capture)
        self.assertIn("--attachments build/ui-coverage/attachments", workflow)
        self.assertIn("--attachments build/ui-coverage/root-attachments", workflow)
        for target, classes in (
            ("PrintFarmerUITests", catalog["testTargets"][0]["selectedTests"]),
            ("PrintFarmerTests", catalog["testTargets"][1]["selectedTests"]),
        ):
            for name in classes:
                self.assertIn(f"-only-testing:{target}/{name}", workflow)
        self.assertIn("CatalogVisualAccessibilityUITests", default["testTargets"][1]["skippedTests"])
        self.assertIn("CatalogRootVisualAccessibilityUITests", default["testTargets"][1]["skippedTests"])
        source = (MOBILE / "PrintFarmerUITests/AppStoreScreenshotUITests.swift").read_text(encoding="utf-8")
        store_class = source.split("final class AppStoreScreenshotUITests:")[1].split("@MainActor")[0]
        self.assertNotIn("testCaptureCatalog", store_class)
        self.assertIn("testCaptureStoreScreens", store_class)
        import re
        # Lexical placement guard, not a Swift compiler or XCTest discovery test.
        code = re.sub(r'//[^\n]*|"(?:\\.|[^"\\])*"', "", source)
        for name in ("AppStoreScreenshotUITests", "CatalogVisualAccessibilityUITests",
                     "CatalogRootVisualAccessibilityUITests"):
            declaration = re.search(rf"\bfinal class {name}\b", code)
            self.assertIsNotNone(declaration)
            prefix = code[:declaration.start()]
            self.assertEqual(prefix.count("{") - prefix.count("}"), 0,
                             f"{name} must be at file scope for XCTest discovery")

    def test_simulator_selector_ignores_legacy_last_entries(self):
        inventory = {"devicetypes": [
            {"name": "iPhone 15", "productFamily": "iPhone", "identifier": "modern-phone"},
            {"name": "iPad Pro 13-inch (M5)", "productFamily": "iPad", "identifier": "modern-pad"},
            {"name": "iPod touch (7th generation)", "productFamily": "iPhone", "identifier": "legacy-ipod"},
            {"name": "iPad mini 4", "productFamily": "iPad", "identifier": "legacy-ipad"},
        ]}
        for family, preference, expected in [
            ("iPhone", "iPhone 15,iPhone 16", "modern-phone"),
            ("iPad", "iPad Pro 13-inch (M5)", "modern-pad"),
        ]:
            selected = DEVICE_MODULE.select_device(inventory, family, preference)
            self.assertEqual(selected["identifier"], expected)
            result = subprocess.run([sys.executable, str(MOBILE / "scripts/select-ui-coverage-device.py"),
                                     "--family", family, "--preferences", preference],
                                    input=json.dumps(inventory), capture_output=True, text=True, check=False)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(json.loads(result.stdout)["identifier"], expected)
        with self.assertRaisesRegex(ValueError, "No installed"):
            DEVICE_MODULE.select_device(inventory, "iPhone", "iPhone does not exist")

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
