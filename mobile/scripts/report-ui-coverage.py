"""Report native candidates without promoting inventory rows to full coverage."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import sys


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def validate_catalog(catalog):
    if catalog["schemaVersion"] != 1:
        raise ValueError("Unsupported catalog schema")
    entries = catalog["entries"]
    ids = [entry["id"] for entry in entries]
    if len(ids) != 37 or len(set(ids)) != len(ids):
        raise ValueError("Expected exactly 37 unique iOS inventory rows")
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
            if not name.startswith("catalog."):
                continue
            if name in result:
                raise ValueError(f"Duplicate catalog attachment: {name}")
            result[name] = safe_file(directory, attachment["exportedFileName"])
    return result


def report(catalog, attachments, family, head, blocked_reason=None, approvals=None, golden_directory=None):
    scenarios = validate_catalog(catalog)
    if family not in ("iPhone", "iPad") or not re.fullmatch(r"[0-9a-f]{40}", head):
        raise ValueError("Report needs an explicit device family and full source SHA")
    expected_profile = catalog["renderProfile"]
    results = {}
    approvals = approvals or {}
    for scenario in scenarios:
        receipt_path = attachments.get(f"catalog.{scenario}.json")
        image_path = attachments.get(f"catalog.{scenario}.png")
        if receipt_path is None or image_path is None:
            results[scenario] = {"status": "blocked", "reason": blocked_reason or "Missing native receipt/image"}
            continue
        receipt = read_json(receipt_path)
        digest = hashlib.sha256(image_path.read_bytes()).hexdigest()
        if receipt["scenarioId"] != scenario or receipt["sha256"] != digest:
            raise ValueError(f"Mismatched native receipt/image: {scenario}")
        for field in ("appearance", "contentSize", "runtime"):
            if receipt[field] != expected_profile[field]:
                raise ValueError(f"Unexpected native {field} for {scenario}")
        if not receipt["device"].startswith(family):
            raise ValueError(f"Unexpected simulator device for {scenario}")
        if receipt["audit"] != "passed":
            raise ValueError(f"Accessibility audit did not pass for {scenario}")
        approved = approvals.get(f"{receipt['device']}.{scenario}")
        status = "awaiting-human-golden"
        if approved is not None:
            if not approved["reviewedBy"] or not re.fullmatch(r"[0-9a-f]{40}", approved["sourceSha"]):
                raise ValueError("Golden approval needs human review attribution and source SHA")
            if approved["profile"] != expected_profile:
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
        row["evidenceFlags"] = [f"native-candidate:{value}" for value in exercised]
        if exercised:
            row["disposition"] = "partial-exercised"
        elif blocked_reason:
            row["reason"] = f"{blocked_reason} {row['reason']}"
        entries.append(row)
    return {
        "schemaVersion": 1, "source": {**catalog["source"], "testedHead": head},
        "renderProfile": {**expected_profile, "family": family}, "entries": entries,
        "scenarioResults": results,
        "summary": {
            "inventoriedRows": len(entries), "fullyCoveredRows": 0,
            "partiallyExercisedRows": sum(row["disposition"] == "partial-exercised" for row in entries),
            "blockedRows": sum(row["disposition"] == "blocked" for row in entries),
            "exercisedScenarios": sum(value["status"] != "blocked" for value in results.values()),
            "pendingHumanGoldens": sum(value["status"] == "awaiting-human-golden" for value in results.values()),
        },
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, default=Path("ui-coverage/catalog.json"))
    parser.add_argument("--attachments", type=Path)
    parser.add_argument("--approvals", type=Path, default=Path("ui-coverage/approved-goldens.json"))
    parser.add_argument("--goldens", type=Path, default=Path("ui-coverage/goldens"))
    parser.add_argument("--family", choices=("iPhone", "iPad"), required=True)
    parser.add_argument("--head", required=True)
    parser.add_argument("--blocked-reason")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    attachments = exported_attachments(args.attachments) if args.attachments else {}
    if not args.attachments and not args.blocked_reason:
        parser.error("No native attachments: an explicit blocker is required")
    result = report(read_json(args.catalog), attachments, args.family, args.head,
                    args.blocked_reason, read_json(args.approvals), args.goldens)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result["summary"], sort_keys=True))
    if args.attachments and any(value["status"] in ("blocked", "visual-regression")
                                for value in result["scenarioResults"].values()):
        print("Native adapter evidence incomplete or approved golden differs.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
