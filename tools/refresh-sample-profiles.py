#!/usr/bin/env python3
"""Refresh the OrcaSlicer sample-profile test fixtures.

`sample_profiles/orcaslicer/` is a checked-in mirror of OrcaSlicer's system
profiles for a fixed set of vendors. It is used as test data by
`Farm.Slicer.Module.Tests` (OrcaSlicerProfilesProviderTests,
ProfileSampleDataTests, OrcaSlicerLibraryTests). Every OrcaSlicer version bump
must refresh this mirror from the target version's `resources/profiles/` tree,
otherwise the tests validate stale profile data.

The repo stores JSON fixtures **tab-indented, UTF-8 (non-ASCII preserved), LF
line endings, with a trailing newline** and file mode 644. This script
reproduces that format exactly (verified byte-identical against unchanged
profiles), so diffs contain only genuine upstream changes. Binary assets
(cover PNGs, bed STLs, vendor SVG logos) are byte-copied.

Usage:
  # Refresh all vendors currently present in the mirror (from a source checkout)
  python3 tools/refresh-sample-profiles.py "$ORCA_SRC/resources/profiles"

  # Custom output dir / explicit vendor list
  python3 tools/refresh-sample-profiles.py <source_profiles_dir> \
      --output sample_profiles/orcaslicer --vendors Prusa Voron Elegoo

  # Dry run: report what would change without writing
  python3 tools/refresh-sample-profiles.py <source_profiles_dir> --check

Source options:
  - OrcaSlicer source checkout at the target tag: `<checkout>/resources/profiles`
    (recommended — contains every vendor).
  - OrcaSlicer.app: `/Applications/OrcaSlicer.app/Contents/Resources/profiles`
    (may only contain a subset of vendors depending on install).
"""

import argparse
import json
import os
import shutil
import sys
from pathlib import Path
from typing import List, Tuple

DEFAULT_OUTPUT = Path("sample_profiles/orcaslicer")
JSON_MODE = 0o644


def normalize_json(src_path: Path) -> str:
    """Return the canonical fixture text for a source JSON profile.

    Tab indentation, non-ASCII preserved, LF endings, trailing newline.
    Key order is preserved from the source (json preserves insertion order).
    """
    with src_path.open(encoding="utf-8") as fh:
        data = json.load(fh)
    return json.dumps(data, indent="\t", ensure_ascii=False) + "\n"


def mirror_vendor(
    src_root: Path, out_root: Path, vendor: str, check: bool
) -> Tuple[int, int, int]:
    """Mirror one vendor bundle (`Vendor.json` + `Vendor/` subtree).

    Returns (written, unchanged, removed) file counts.
    """
    written = unchanged = removed = 0
    sources: dict[Path, Path] = {}  # relative path -> absolute source path

    bundle = src_root / f"{vendor}.json"
    if bundle.exists():
        sources[Path(f"{vendor}.json")] = bundle
    vendor_dir = src_root / vendor
    if vendor_dir.is_dir():
        for dirpath, _, files in os.walk(vendor_dir):
            for fn in files:
                abs_src = Path(dirpath) / fn
                rel = abs_src.relative_to(src_root)
                sources[rel] = abs_src

    if not sources:
        print(f"  ! {vendor}: not found in source — skipping", file=sys.stderr)
        return (0, 0, 0)

    # Write / update
    for rel, abs_src in sorted(sources.items()):
        dest = out_root / rel
        if abs_src.suffix == ".json":
            new_text = normalize_json(abs_src)
            old_text = dest.read_text(encoding="utf-8") if dest.exists() else None
            if old_text == new_text:
                unchanged += 1
                continue
            if not check:
                dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_text(new_text, encoding="utf-8", newline="\n")
                os.chmod(dest, JSON_MODE)
            written += 1
        else:  # binary asset — byte copy
            if dest.exists() and dest.read_bytes() == abs_src.read_bytes():
                unchanged += 1
                continue
            if not check:
                dest.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(abs_src, dest)
                os.chmod(dest, JSON_MODE)
            written += 1

    # Remove fixture files that no longer exist upstream (full mirror)
    tracked_roots = [out_root / f"{vendor}.json", out_root / vendor]
    for root in tracked_roots:
        if root.is_file():
            rel = root.relative_to(out_root)
            if rel not in sources:
                if not check:
                    root.unlink()
                removed += 1
        elif root.is_dir():
            for dirpath, _, files in os.walk(root):
                for fn in files:
                    dest = Path(dirpath) / fn
                    rel = dest.relative_to(out_root)
                    if rel not in sources:
                        if not check:
                            dest.unlink()
                        removed += 1

    verb = "would change" if check else "changed"
    print(
        f"  {vendor}: {written} {verb}, {unchanged} unchanged, {removed} removed"
    )
    return (written, unchanged, removed)


def discover_vendors(out_root: Path) -> List[str]:
    """Vendors currently in the mirror = subdirectories of the output root."""
    return sorted(p.name for p in out_root.iterdir() if p.is_dir())


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Refresh sample_profiles/orcaslicer from an OrcaSlicer profiles tree.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument(
        "source",
        type=Path,
        help="Path to OrcaSlicer resources/profiles (source checkout or .app)",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=DEFAULT_OUTPUT,
        help=f"Mirror output directory (default: {DEFAULT_OUTPUT})",
    )
    parser.add_argument(
        "--vendors",
        nargs="+",
        help="Explicit vendor list (default: vendors already present in --output)",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="Dry run: report changes without writing",
    )
    args = parser.parse_args()

    if not args.source.is_dir():
        print(f"Error: source profiles dir not found: {args.source}", file=sys.stderr)
        return 1
    if not args.output.is_dir():
        print(f"Error: output mirror not found: {args.output}", file=sys.stderr)
        return 1

    vendors = args.vendors or discover_vendors(args.output)
    print(f"Refreshing {len(vendors)} vendor(s) from {args.source}")
    print(f"  vendors: {', '.join(vendors)}")

    totals = [0, 0, 0]
    for vendor in vendors:
        w, u, r = mirror_vendor(args.source, args.output, vendor, args.check)
        totals[0] += w
        totals[1] += u
        totals[2] += r

    action = "Would change" if args.check else "Changed"
    print(
        f"\n{action}: {totals[0]} file(s), {totals[1]} unchanged, "
        f"{totals[2]} removed across {len(vendors)} vendor(s)."
    )
    if args.check and totals[0] + totals[2] > 0:
        return 1  # non-zero so CI/pre-commit can detect a stale mirror
    return 0


if __name__ == "__main__":
    sys.exit(main())
