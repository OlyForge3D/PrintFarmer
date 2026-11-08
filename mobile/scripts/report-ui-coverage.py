"""Report native candidates without promoting inventory rows to full coverage."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import sys

INVENTORY_IDS = frozenset("""
ios.launch-splash ios.first-server ios.onboarding ios.local-network-permission
ios.login ios.backend-gate ios.snapshot-activation ios.certificate-trust
ios.farm-dashboard ios.printer-detail ios.queue-job-list ios.job-history
ios.job-detail ios.filament-spool-inventory ios.add-spool ios.parts-inventory
ios.part-lookup ios.selected-part ios.notifications ios.account ios.settings
ios.servers ios.server-editor ios.server-switcher ios.offline-queue ios.scan-flow
ios.qr-scanner ios.barcode-intake ios.nfc-write ios.spool-picker ios.bin-scan-result
ios.harvest-sheet ios.navigation-tabs ios.appearance-axis
ios.viewport-accessibility-axis ios.native-alerts ios.native-inline-panels
""".split())
SOURCE_SNAPSHOT = "fce568d72a78600ecb845749a6f0bf6544d7b5d8"
SOURCE_HASH = "c19d17d5a6f18898126127fd766e0d2fed14fe7c5142443906ac61155b2373ee"


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def validate_catalog(catalog):
    if catalog["schemaVersion"] != 1:
        raise ValueError("Unsupported catalog schema")
    entries = catalog["entries"]
    ids = [entry["id"] for entry in entries]
    if len(ids) != 37 or set(ids) != INVENTORY_IDS:
        raise ValueError("Expected the exact 37 pinned iOS inventory identities")
    if catalog["source"]["snapshot"] != SOURCE_SNAPSHOT or catalog["source"]["sha256"] != SOURCE_HASH:
        raise ValueError("Inventory provenance metadata differs from supplied source pin")
    scenarios = []
    for entry in entries:
        if entry["platform"] != "ios" or not re.fullmatch(r"ios\.[a-z-]+", entry["id"]):
            raise ValueError("Invalid iOS row identity")
        if entry["disposition"] != "blocked" or not entry["reason"]:
            raise ValueError("Inventory seeds must retain explicit coverage blockers")
        scenarios.extend(entry["scenarioIds"])
    if len(set(scenarios)) != len(scenarios):
        raise ValueError("Scenario IDs must belong to exactly one row")
    if any(not re.fullmatch(r"\d{2}-[a-z-]+", value) for value in scenarios):
        raise ValueError("Unsafe scenario ID")
    allowed_overrides = {
        "appearance": {"light", "dark"},
        "themeMode": {"light", "dark", "system"},
        "contentSize": {"UICTContentSizeCategoryL", "UICTContentSizeCategoryAccessibilityXXXL"},
        "orientation": {"portrait", "landscape"},
    }
    for scenario, overrides in catalog.get("scenarioProfileOverrides", {}).items():
        if scenario not in scenarios or not isinstance(overrides, dict) or not overrides:
            raise ValueError("Profile override needs a mapped scenario and explicit fields")
        for field, value in overrides.items():
            if field not in allowed_overrides or not isinstance(value, str) or value not in allowed_overrides[field]:
                raise ValueError("Profile override cannot change environment pins or use unknown values")
    return scenarios


def safe_file(directory, name):
    if not isinstance(name, str) or Path(name).name != name or "\\" in name:
        raise ValueError("Attachment path must be a plain filename")
    path = (directory / name).resolve()
    if path.parent != directory.resolve() or path.is_symlink():
        raise ValueError("Attachment escaped its directory")
    return path


def exported_attachments(directory):
    manifest = read_json(directory / "manifest.json")
    if not isinstance(manifest, list):
        raise ValueError("Unsupported xcresult attachment manifest; retain raw evidence")
    result = {}
    for test in manifest:
        for attachment in test["attachments"]:
            name = attachment["suggestedHumanReadableName"]
            match = re.fullmatch(
                r"catalog-(image|receipt)-(\d{2}-[a-z-]+)"
                r"(?:_\d+_[0-9A-Fa-f-]{36})?(?:\.(?:png|json))?", name
            )
            if match is None:
                continue
            extension = "png" if match[1] == "image" else "json"
            key = f"catalog.{match[2]}.{extension}"
            if key in result or attachment["isAssociatedWithFailure"]:
                raise ValueError(f"Duplicate or failed catalog attachment: {name}")
            result[key] = safe_file(directory, attachment["exportedFileName"])
    return result


def native_profile(directory, catalog, family, head):
    if (directory / "source-sha.txt").read_text().strip() != head:
        raise ValueError("Native evidence belongs to a different source SHA")
    xcode = (directory / "xcode.txt").read_text().splitlines()
    profile = catalog["renderProfile"]
    if xcode != [f"Xcode {profile['xcode']}", f"Build version {profile['xcodeBuild']}"]:
        raise ValueError("Native evidence has an unapproved Xcode toolchain")
    runtime_id = "com.apple.CoreSimulator.SimRuntime.iOS-" + profile["runtime"].replace(".", "-")
    runtimes = read_json(directory / "runtimes.json")["runtimes"]
    runtime = [row for row in runtimes if row["identifier"] == runtime_id]
    if len(runtime) != 1 or not runtime[0]["isAvailable"] or runtime[0]["buildversion"] != profile["runtimeBuild"]:
        raise ValueError("Native evidence has an unapproved simulator runtime")
    udid = (directory / "simulator-udid.txt").read_text().strip()
    devices = read_json(directory / "devices.json")["devices"].get(runtime_id, [])
    selected = [row for row in devices if row["udid"] == udid]
    if len(selected) != 1 or not selected[0]["isAvailable"] or not selected[0]["name"].startswith(family):
        raise ValueError("Native evidence has an unavailable or wrong-family destination")
    return {"xcode": profile["xcode"], "xcodeBuild": profile["xcodeBuild"],
            "runtimeBuild": runtime[0]["buildversion"], "simulatorUdid": udid}


def report(catalog, attachments, family, head, blocked_reason=None, approvals=None, golden_directory=None):
    scenarios = validate_catalog(catalog)
    if family not in ("iPhone", "iPad") or not re.fullmatch(r"[0-9a-f]{40}", head):
        raise ValueError("Report needs an explicit device family and full source SHA")
    expected_profile = catalog["renderProfile"]
    results = {}
    approvals = approvals or {}
    for scenario in scenarios:
        scenario_profile = {**expected_profile,
                            **catalog.get("scenarioProfileOverrides", {}).get(scenario, {})}
        receipt_path = attachments.get(f"catalog.{scenario}.json")
        image_path = attachments.get(f"catalog.{scenario}.png")
        if receipt_path is None or image_path is None:
            results[scenario] = {"status": "blocked", "reason": blocked_reason or "Missing native receipt/image"}
            continue
        receipt = read_json(receipt_path)
        digest = hashlib.sha256(image_path.read_bytes()).hexdigest()
        if receipt["scenarioId"] != scenario or receipt["sha256"] != digest:
            raise ValueError(f"Mismatched native receipt/image: {scenario}")
        for field in ("appearance", "themeMode", "contentSize", "runtime", "locale", "orientation"):
            if receipt[field] != scenario_profile[field]:
                raise ValueError(f"Unexpected native {field} for {scenario}")
        if not receipt["device"].startswith(family):
            raise ValueError(f"Unexpected simulator device for {scenario}")
        if receipt["audit"] == "failed":
            if not receipt["auditIssues"]:
                raise ValueError(f"Failed audit needs concrete findings for {scenario}")
            results[scenario] = {"status": "accessibility-failure", "sha256": digest,
                                 "device": receipt["device"], "image": image_path.name,
                                 "audit": "failed", "reason": receipt["auditIssues"]}
            continue
        if receipt["audit"] != "passed":
            raise ValueError(f"Invalid accessibility audit status for {scenario}")
        approved = approvals.get(f"{receipt['device']}.{scenario}")
        status = "awaiting-human-golden"
        if approved is not None:
            if not approved["reviewedBy"] or not re.fullmatch(r"[0-9a-f]{40}", approved["sourceSha"]):
                raise ValueError("Golden approval needs human review attribution and source SHA")
            if approved["profile"] != scenario_profile:
                raise ValueError("Golden approval profile differs from pinned rendering")
            if golden_directory is None:
                raise ValueError("Golden approval needs its retained human-reviewed PNG")
            golden = safe_file(golden_directory, approved["file"])
            if hashlib.sha256(golden.read_bytes()).hexdigest() != approved["sha256"]:
                raise ValueError("Retained golden differs from human approval hash")
            status = "golden-matched" if approved["sha256"] == digest else "visual-regression"
        results[scenario] = {"status": status, "sha256": digest, "device": receipt["device"],
                             "image": image_path.name, "audit": "passed"}
    entries = []
    for entry in catalog["entries"]:
        row = dict(entry)
        exercised = [value for value in row["scenarioIds"] if results[value]["status"] != "blocked"]
        # A handful of candidates cannot certify all states of a broad inventory row.
        failed = [value for value in exercised if results[value]["status"] == "accessibility-failure"]
        row["evidenceFlags"] = [
            f"native-audit-failed:{value}" if value in failed else f"native-candidate:{value}"
            for value in exercised
        ]
        if failed:
            row["disposition"] = "failed"
            row["reason"] = "Native accessibility failures: " + "; ".join(
                f"{value}: {results[value]['reason']}" for value in failed
            ) + " " + row["reason"]
        elif exercised:
            row["disposition"] = "partial-exercised"
        elif blocked_reason:
            row["reason"] = f"{blocked_reason} {row['reason']}"
        entries.append(row)
    return {
        "schemaVersion": 1, "source": {**catalog["source"], "testedHead": head},
        "renderProfile": {**expected_profile, "family": family},
        "scenarioProfileOverrides": catalog.get("scenarioProfileOverrides", {}), "entries": entries,
        "scenarioResults": results,
        "summary": {
            "inventoriedRows": len(entries), "fullyCoveredRows": 0,
            "partiallyExercisedRows": sum(row["disposition"] == "partial-exercised" for row in entries),
            "blockedRows": sum(row["disposition"] == "blocked" for row in entries),
            "failedRows": sum(row["disposition"] == "failed" for row in entries),
            "exercisedScenarios": sum(value["status"] != "blocked" for value in results.values()),
            "failedAuditScenarios": sum(value["status"] == "accessibility-failure" for value in results.values()),
            "pendingHumanGoldens": sum(value["status"] == "awaiting-human-golden" for value in results.values()),
        },
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, default=Path("ui-coverage/catalog.json"))
    parser.add_argument("--attachments", type=Path, action="append", default=[])
    parser.add_argument("--metadata-directory", type=Path)
    parser.add_argument("--approvals", type=Path, default=Path("ui-coverage/approved-goldens.json"))
    parser.add_argument("--goldens", type=Path, default=Path("ui-coverage/goldens"))
    parser.add_argument("--family", choices=("iPhone", "iPad"), required=True)
    parser.add_argument("--head", required=True)
    parser.add_argument("--blocked-reason")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    attachments = {}
    for directory in args.attachments:
        exported = exported_attachments(directory)
        duplicates = attachments.keys() & exported.keys()
        if duplicates:
            parser.error(f"Duplicate native attachments across exports: {sorted(duplicates)}")
        attachments.update(exported)
    if not args.attachments and not args.blocked_reason:
        parser.error("No native attachments: an explicit blocker is required")
    catalog = read_json(args.catalog)
    validate_catalog(catalog)
    metadata = None
    if args.attachments:
        if args.metadata_directory is None:
            parser.error("Native attachments require retained toolchain/destination/source metadata")
        metadata = native_profile(args.metadata_directory, catalog, args.family, args.head)
    result = report(catalog, attachments, args.family, args.head,
                    args.blocked_reason, read_json(args.approvals), args.goldens)
    if metadata:
        result["nativeEnvironment"] = metadata
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result["summary"], sort_keys=True))
    if args.attachments and any(value["status"] in ("blocked", "visual-regression", "accessibility-failure")
                                for value in result["scenarioResults"].values()):
        print("Native adapter evidence incomplete or approved golden differs.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
