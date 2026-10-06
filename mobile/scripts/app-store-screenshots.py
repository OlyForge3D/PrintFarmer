#!/usr/bin/env python3
"""Capture native store PNGs; never resize, crop, upload, or record test baselines."""

import argparse
import datetime
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile


MOBILE = Path(__file__).resolve().parents[1]
RESOLVER = MOBILE.parent / "scripts/ci/resolve-ios-simulator.sh"
SCREENS = ("01-farm", "02-printer-status", "03-queue", "04-filament", "05-scan")
DEVICES = {
    "iPhone": ("iPhone 17 Pro Max", "iPhone-17-Pro-Max", (1320, 2868)),
    "iPad": ("iPad Pro 13-inch (M5)", "iPad-Pro-13-inch-M5-12GB", (2064, 2752)),
}


def run(arguments, *, env=None):
    return subprocess.check_output(arguments, cwd=MOBILE, env=env, text=True)


def resolve(family):
    name, device_type, _ = DEVICES[family]
    env = os.environ.copy()
    # Prefix excludes smaller resolver fallbacks; a preference alone does not.
    env.update(IOS_SIMULATOR_DEVICE_FAMILY=family,
               IOS_SIMULATOR_DEVICE_PREFIX=name,
               IOS_SIMULATOR_DEVICE_PREFERENCE=name)
    env.pop("GITHUB_ENV", None)
    udid = run(["bash", str(RESOLVER), "--udid"], env=env).strip()
    devices = json.loads(run(["xcrun", "simctl", "list", "devices", "available", "-j"]))
    device = next(
        (d for d in devices["devices"].get("com.apple.CoreSimulator.SimRuntime.iOS-26-5", [])
         if d["udid"] == udid), None
    )
    if device is None or device["deviceTypeIdentifier"] != f"com.apple.CoreSimulator.SimDeviceType.{device_type}":
        raise RuntimeError(f"No approved {family} of the required display class; install/create {name}.")
    return device


def export(bundle, directory):
    attachments = directory / "attachments"
    run(["xcrun", "xcresulttool", "export", "attachments",
         "--path", str(bundle), "--output-path", str(attachments)])
    manifest = json.loads((attachments / "manifest.json").read_text())
    found = {}
    for test in manifest:
        for attachment in test["attachments"]:
            name = attachment["suggestedHumanReadableName"]
            for screen in SCREENS:
                if re.fullmatch(
                    rf"app-store-{re.escape(screen)}(?:_\d+_[0-9A-Fa-f-]{{36}})?(?:\.png)?", name
                ):
                    if screen in found or attachment["isAssociatedWithFailure"]:
                        raise RuntimeError(f"Ambiguous or failed screenshot attachment: {name}")
                    found[screen] = attachments / attachment["exportedFileName"]
    if set(found) != set(SCREENS):
        raise RuntimeError(f"Missing store attachments: {set(SCREENS) - set(found)}")
    images = []
    for screen, source in found.items():
        target = directory / f"{screen}.png"
        shutil.copyfile(source, target)
        images.append(target)
    return images


def capture(device, directory, derived_data):
    directory.mkdir(parents=True)
    udid = device["udid"]
    inventory = json.loads(run(["xcrun", "simctl", "list", "devices", "available", "-j"]))
    current = next(
        (d for devices in inventory["devices"].values() for d in devices if d["udid"] == udid), None
    )
    if current is None:
        raise RuntimeError(f"Selected simulator became unavailable: {udid}")
    if current["state"] != "Booted":
        run(["xcrun", "simctl", "boot", udid])
    run(["xcrun", "simctl", "bootstatus", udid, "-b"])
    run(["xcrun", "simctl", "ui", udid, "appearance", "dark"])
    run(["xcrun", "simctl", "ui", udid, "content_size", "large"])
    status_bar_date = datetime.datetime(2026, 10, 5, 9, 41).astimezone(
        datetime.timezone.utc
    ).strftime("%Y-%m-%dT%H:%M:%S.000Z")
    run(["xcrun", "simctl", "status_bar", udid, "override",
         "--time", status_bar_date, "--dataNetwork", "wifi", "--wifiMode", "active",
         "--wifiBars", "3", "--batteryState", "charged", "--batteryLevel", "100"])
    bundle = directory / "Screenshots.xcresult"
    command = [
        sys.executable, str(MOBILE / "scripts/run-tests.py"), "--",
        "test", "-scheme", "PrintFarmer",
        "-destination", f"platform=iOS Simulator,id={udid}",
        "-derivedDataPath", str(derived_data),
        "-only-testing:PrintFarmerUITests/AppStoreScreenshotUITests",
        "-parallel-testing-enabled", "NO", "-resultBundlePath", str(bundle),
    ]
    try:
        with (directory / "test.log").open("w") as log:
            subprocess.run(command, cwd=MOBILE, stdout=log, stderr=subprocess.STDOUT, check=True)
        return export(bundle, directory)
    finally:
        run(["xcrun", "simctl", "status_bar", udid, "clear"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verify-repeatability", action="store_true",
                        help="Capture twice and require identical decoded RGBA pixels for every image.")
    args = parser.parse_args()
    # Resolve both before building; missing display classes are blockers, not resize requests.
    devices = {family: resolve(family) for family in DEVICES}
    root = MOBILE / "build/app-store"
    root.mkdir(parents=True, exist_ok=True)
    output = Path(tempfile.mkdtemp(
        prefix=datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ-"), dir=root
    ))
    metadata = {
        "commit": run(["git", "rev-parse", "HEAD"]).strip(),
        "xcode": run(["xcodebuild", "-version"]).strip(),
        "runtimes": json.loads(run(["xcrun", "simctl", "list", "runtimes", "-j"])),
        "devices": devices,
        "repeatability": "not requested",
    }
    (output / "environment.json").write_text(json.dumps(metadata, indent=2) + "\n")
    print(f"Retaining PNGs, logs and result bundles in {output}", flush=True)
    evidence = {}
    for iteration in range(1, 3 if args.verify_repeatability else 2):
        for family, device in devices.items():
            directory = output / f"run-{iteration}" / family
            print(f"Capturing {family}, run {iteration}; log: {directory / 'test.log'}", flush=True)
            images = capture(device, directory, root / "DerivedData")
            pixels = json.loads(run(["swift", str(MOBILE / "scripts/screenshot-pixels.swift"),
                                     *map(str, images)]))
            evidence[f"{iteration}/{family}"] = pixels
            (output / "pixels.json").write_text(json.dumps(evidence, indent=2) + "\n")
            for path, image in pixels.items():
                if (image["width"], image["height"]) != DEVICES[family][2]:
                    raise RuntimeError(f"Wrong native screenshot dimensions: {path}: {image}")
                if iteration == 2:
                    original = output / "run-1" / family / Path(path).name
                    if image != evidence[f"1/{family}"][str(original)]:
                        raise RuntimeError(f"Pixel mismatch between captures: {path}")
    metadata["repeatability"] = "identical RGBA pixels" if args.verify_repeatability else "not requested"
    (output / "environment.json").write_text(json.dumps(metadata, indent=2) + "\n")
    print(f"Store screenshots ready for manual review/upload: {output / 'run-1'}")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"App Store screenshot capture failed: {error}. Inspect the retained test.log.", file=sys.stderr)
        sys.exit(1)
