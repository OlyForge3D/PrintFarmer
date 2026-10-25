#!/usr/bin/env python3
"""Read or verify the repository's approved SDK without requiring dotnet."""

import argparse
import json
from pathlib import Path
import re
import subprocess
import sys


def approved_version(root):
    sdk = json.loads((root / "global.json").read_text(encoding="utf-8"))["sdk"]
    version = sdk["version"]
    if not re.fullmatch(r"10\.0\.\d{3}", version) or int(version.split(".")[2]) < 200:
        raise ValueError("global.json must approve a released .NET 10 SDK >=10.0.200")
    if sdk.get("rollForward") != "disable" or sdk.get("allowPrerelease") is not False:
        raise ValueError("global.json requires rollForward:disable and allowPrerelease:false")
    return version


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["version", "tag", "verify"])
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    args = parser.parse_args()
    version = approved_version(args.root)
    if args.command == "verify":
        selected = subprocess.run(
            ["dotnet", "--version"], cwd=args.root, text=True, capture_output=True, check=True
        ).stdout.strip()
        print(f".NET SDK selected: {selected}; approved: {version}")
        if selected != version:
            raise ValueError(f"Selected SDK {selected} differs from global.json ({version})")
    else:
        print(f"{version}-noble" if args.command == "tag" else version)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        print(f"SDK policy error: {error}", file=sys.stderr)
        if isinstance(error, subprocess.CalledProcessError):
            print(error.stderr, file=sys.stderr)
        sys.exit(1)
