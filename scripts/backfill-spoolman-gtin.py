#!/usr/bin/env python3
"""Backfill Spoolman filament ``gtin`` values from legacy ``article_number`` barcodes.

PrintFarmer versions before the GTIN migration stored scanned retail barcodes in
Spoolman's ``article_number`` field. Barcode resolution now matches on ``gtin``
only, so those legacy mappings must be copied across to stay scannable.

This script talks to Spoolman's REST API (the filament data lives in Spoolman's
database, not PrintFarmer's, so this cannot ship as an EF Core migration). It is
**dry-run by default** - pass ``--apply`` to write.

It only ever *adds* a ``gtin``. It never clears or modifies ``article_number``,
and it never overwrites a ``gtin`` that is already set.

Usage:
    python3 scripts/backfill-spoolman-gtin.py --spoolman-url http://localhost:7912
    python3 scripts/backfill-spoolman-gtin.py --spoolman-url http://localhost:7912 --apply

Exit codes:
    0  success (nothing to do, or all planned writes applied)
    1  usage / connectivity error
    2  one or more writes failed
"""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.parse
import urllib.request

GTIN_LENGTH = 14
# A formatted GTIN-14 with separators between every digit is well under this.
# Mirrors GtinNormalizer.MaxRawLength so this script accepts exactly what the
# application accepts -- no more, no less.
MAX_RAW_LENGTH = 64
PAGE_SIZE = 500


def has_valid_check_digit(digits: str) -> bool:
    """Validate the GS1 mod-10 check digit (last digit) of a GTIN-8/12/13/14."""
    check_digit = int(digits[-1])
    total = 0
    # Weighting alternates 3/1 starting from the digit immediately left of the
    # check digit, per the GS1 general specification.
    for index in range(len(digits) - 2, -1, -1):
        digit = int(digits[index])
        is_odd_position_from_right = (len(digits) - 1 - index) % 2 == 1
        total += digit * 3 if is_odd_position_from_right else digit
    return (10 - (total % 10)) % 10 == check_digit


def normalize_gtin(barcode: str | None) -> str | None:
    """Normalize a scanned barcode to a 14-digit GTIN.

    Deliberately mirrors ``GtinNormalizer.Normalize`` in
    ``src/infra/Normalization/GtinNormalizer.cs``. Returns ``None`` when the
    value is not a GTIN, which is how a genuine vendor SKU is distinguished
    from a barcode that merely happens to live in ``article_number``.
    """
    if barcode is None or not barcode.strip():
        return None
    if len(barcode) > MAX_RAW_LENGTH:
        return None
    digits = "".join(c for c in barcode if c.isdigit())
    if len(digits) not in (8, 12, 13, 14):
        return None
    if not has_valid_check_digit(digits):
        return None
    return digits.rjust(GTIN_LENGTH, "0")


def request_json(url: str, timeout: float, method: str = "GET", body: dict | None = None):
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Accept", "application/json")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        payload = resp.read().decode("utf-8")
    return json.loads(payload) if payload else None


def fetch_all_filaments(base_url: str, timeout: float) -> list[dict]:
    filaments: list[dict] = []
    offset = 0
    while True:
        query = urllib.parse.urlencode({"limit": PAGE_SIZE, "offset": offset})
        page = request_json(f"{base_url}/api/v1/filament?{query}", timeout)
        if not page:
            break
        filaments.extend(page)
        if len(page) < PAGE_SIZE:
            break
        offset += PAGE_SIZE
    return filaments


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Backfill Spoolman filament gtin values from legacy article_number barcodes.",
    )
    parser.add_argument(
        "--spoolman-url",
        required=True,
        help="Base URL of the Spoolman instance, e.g. http://localhost:7912",
    )
    parser.add_argument(
        "--apply",
        action="store_true",
        help="Actually write the changes. Without this flag the script only reports a plan.",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=30.0,
        help="Per-request timeout in seconds (default: 30).",
    )
    args = parser.parse_args()

    base_url = args.spoolman_url.rstrip("/")

    try:
        filaments = fetch_all_filaments(base_url, args.timeout)
    except (urllib.error.URLError, urllib.error.HTTPError, TimeoutError, json.JSONDecodeError) as exc:
        print(f"ERROR: could not read filaments from {base_url}: {exc}", file=sys.stderr)
        return 1

    planned: list[tuple[dict, str]] = []
    already_set = 0
    sku_skipped = 0

    for filament in filaments:
        if (filament.get("gtin") or "").strip():
            already_set += 1
            continue
        article_number = filament.get("article_number")
        if not (article_number or "").strip():
            continue
        normalized = normalize_gtin(article_number)
        if normalized is None:
            # Not a barcode -- a genuine vendor SKU. Leave it alone.
            sku_skipped += 1
            continue
        planned.append((filament, normalized))

    print(f"Spoolman            : {base_url}")
    print(f"filaments           : {len(filaments)}")
    print(f"  gtin already set  : {already_set}")
    print(f"  article_number is a SKU (skipped) : {sku_skipped}")
    print(f"  to backfill       : {len(planned)}")
    print()

    if not planned:
        print("Nothing to backfill. Every legacy barcode already has a gtin.")
        return 0

    for filament, normalized in planned:
        print(
            f"  filament {filament.get('id')}: "
            f"article_number={filament.get('article_number')!r} -> gtin={normalized}"
        )
    print()

    if not args.apply:
        print("DRY RUN - no changes written. Re-run with --apply to write these values.")
        return 0

    failures = 0
    for filament, normalized in planned:
        filament_id = filament.get("id")
        try:
            request_json(
                f"{base_url}/api/v1/filament/{filament_id}",
                args.timeout,
                method="PATCH",
                body={"gtin": normalized},
            )
            print(f"  OK   filament {filament_id} -> {normalized}")
        except (urllib.error.URLError, urllib.error.HTTPError, TimeoutError, json.JSONDecodeError) as exc:
            failures += 1
            print(f"  FAIL filament {filament_id}: {exc}", file=sys.stderr)

    print()
    print(f"Backfilled {len(planned) - failures}/{len(planned)} filaments.")
    if failures:
        print(f"{failures} write(s) failed; re-run to retry the remainder.", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
