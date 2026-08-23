#!/usr/bin/env python3
"""Parity tests for ``scripts/backfill-spoolman-gtin.py``.

The backfill script hand-ports ``GtinNormalizer.Normalize`` from
``src/infra/Normalization/GtinNormalizer.cs`` into Python. The two
implementations must agree exactly: the script writes ``gtin`` values that the
application later has to match, and it skips any filament that already has a
``gtin`` -- so a value the application cannot match is not self-healing on a
re-run.

These tests pin the port against the C# fixtures, and specifically guard the
ASCII-digit boundary, where a natural Python spelling (``str.isdigit()``)
silently diverges from the C# ``c is >= '0' and <= '9'``.

Run:  python3 scripts/tests/test_backfill_spoolman_gtin.py
"""

from __future__ import annotations

import contextlib
import importlib.util
import io
import pathlib
import sys
import unittest
import urllib.error
from unittest import mock

_SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "backfill-spoolman-gtin.py"
_spec = importlib.util.spec_from_file_location("backfill_spoolman_gtin", _SCRIPT)
assert _spec and _spec.loader
backfill = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(backfill)

normalize_gtin = backfill.normalize_gtin


class NormalizeGtinCSharpParityTests(unittest.TestCase):
    """Fixtures transcribed from GtinNormalizerTests.cs."""

    def test_accepts_and_pads_valid_gtins(self):
        cases = {
            "40123455": "00000040123455",        # GTIN-8
            "123456789012": "00123456789012",    # GTIN-12 (UPC-A)
            "4006381333931": "04006381333931",   # GTIN-13 (EAN-13)
            "04006381333931": "04006381333931",  # GTIN-14
        }
        for raw, expected in cases.items():
            with self.subTest(raw=raw):
                self.assertEqual(normalize_gtin(raw), expected)

    def test_rejects_null_and_blank(self):
        for raw in (None, "", "   "):
            with self.subTest(raw=raw):
                self.assertIsNone(normalize_gtin(raw))

    def test_rejects_wrong_digit_counts(self):
        for raw in ("123", "123456789", "123456789012345"):
            with self.subTest(raw=raw):
                self.assertIsNone(normalize_gtin(raw))

    def test_rejects_bad_check_digit(self):
        # Valid UPC-A is 123456789012; flipping the check digit must fail.
        self.assertIsNone(normalize_gtin("123456789011"))

    def test_rejects_oversized_raw_input(self):
        # Embeds a genuinely valid GTIN-13 padded with enough non-digit separators to push
        # the RAW input past MAX_RAW_LENGTH. Without the length guard, digit-filtering would
        # still extract "4006381333931" -- valid payload, correct check digit -- and return
        # non-null. An all-digit oversized string (e.g. "9" * 65) would NOT test this: the
        # 8/12/13/14 length check rejects it on its own, so the test would pass even with the
        # guard removed. Mirrors Normalize_OversizedRawInput_ReturnsNullEvenWhenDigitsWould
        # OtherwiseBeValid in GtinNormalizerTests.cs.
        oversized_but_digits_valid = "-" * 60 + "4006381333931"
        self.assertGreater(len(oversized_but_digits_valid), backfill.MAX_RAW_LENGTH)
        self.assertEqual(normalize_gtin("4006381333931"), "04006381333931", "inner payload must be valid")
        self.assertIsNone(normalize_gtin(oversized_but_digits_valid))

    def test_zero_pad_equivalence_across_formats(self):
        # GTIN-12/13/14 forms of the same product normalize identically.
        self.assertEqual(normalize_gtin("123456789012"), normalize_gtin("0123456789012"))
        self.assertEqual(normalize_gtin("123456789012"), normalize_gtin("00123456789012"))


class AsciiDigitBoundaryTests(unittest.TestCase):
    """The C# filter is ASCII-only; Python's str.isdigit() is not.

    Accepting a non-ASCII digit would write a `gtin` the application can never
    match, into a field this script refuses to correct on a re-run.
    """

    FULLWIDTH_UPC = "\uff18\uff15\uff10\uff10\uff17\uff18\uff17\uff11\uff14\uff19\uff12\uff13"
    ARABIC_INDIC_UPC = "\u0661\u0662\u0663\u0664\u0665\u0666\u0667\u0668\u0669\u0660\u0661\u0662"

    def test_rejects_fullwidth_digits(self):
        self.assertTrue(self.FULLWIDTH_UPC.isdigit(), "fixture must be str.isdigit()-true to be meaningful")
        self.assertIsNone(normalize_gtin(self.FULLWIDTH_UPC))

    def test_rejects_arabic_indic_digits(self):
        self.assertTrue(self.ARABIC_INDIC_UPC.isdigit(), "fixture must be str.isdigit()-true to be meaningful")
        self.assertIsNone(normalize_gtin(self.ARABIC_INDIC_UPC))

    def test_superscript_digit_does_not_crash(self):
        # '\u00b3'.isdigit() is True but int() raises on it -- the ASCII filter
        # must drop it before any int() conversion.
        self.assertTrue("\u00b3".isdigit())
        self.assertIsNone(normalize_gtin("85007871492\u00b3"))

    def test_rejects_mixed_ascii_and_arabic_indic_digits(self):
        # Mirrors Normalize_NonAsciiDecimalDigits in GtinNormalizerTests.cs: dropping the
        # non-ASCII digits leaves only 9 ASCII digits, which is not a valid GTIN length.
        self.assertIsNone(normalize_gtin("\u0661\u06623\u066456789012"))

    def test_result_is_always_ascii_when_accepted(self):
        for raw in ("40123455", "123456789012", "4006381333931", "04006381333931"):
            with self.subTest(raw=raw):
                result = normalize_gtin(raw)
                self.assertIsNotNone(result)
                self.assertTrue(result.isascii())
                self.assertEqual(len(result), 14)

    def test_separators_are_stripped_like_csharp(self):
        # C# strips any non-digit, so a formatted barcode still normalizes.
        self.assertEqual(normalize_gtin("1-234 5678-9012"), "00123456789012")


class SkuDiscriminationTests(unittest.TestCase):
    """A genuine vendor SKU must not be mistaken for a barcode."""

    def test_typical_skus_are_rejected(self):
        for sku in ("PLA-GB-1000", "UPC123", "ABC/DEF 12%3&x=y", "04850807Z", "missing"):
            with self.subTest(sku=sku):
                self.assertIsNone(normalize_gtin(sku))


def _run_main(argv: list[str], filaments: list[dict], patch_responses: dict[int, dict | None] | None = None):
    """Run backfill.main() with fetch_all_filaments and request_json stubbed.

    ``patch_responses`` maps filament id -> the JSON body ``request_json`` should
    return for that filament's PATCH call. A missing entry raises a network error
    to simulate connectivity failure for that specific write. Returns
    (exit_code, captured_stdout).
    """
    patch_responses = patch_responses or {}

    def fake_request_json(url, timeout, method="GET", body=None):
        if method != "PATCH":
            raise AssertionError(f"unexpected non-PATCH request_json call: {method} {url}")
        # URL shape is f"{base}/api/v1/filament/{id}".
        filament_id = int(url.rsplit("/", 1)[-1])
        if filament_id not in patch_responses:
            raise urllib.error.URLError("simulated connectivity failure")
        return patch_responses[filament_id]

    stdout = io.StringIO()
    stderr = io.StringIO()
    with (
        mock.patch.object(sys, "argv", ["backfill-spoolman-gtin.py", *argv]),
        mock.patch.object(backfill, "fetch_all_filaments", return_value=filaments),
        mock.patch.object(backfill, "request_json", side_effect=fake_request_json) as mock_request_json,
        contextlib.redirect_stdout(stdout),
        contextlib.redirect_stderr(stderr),
    ):
        exit_code = backfill.main()
    return exit_code, stdout.getvalue() + stderr.getvalue(), mock_request_json


class MainDryRunTests(unittest.TestCase):
    """``main()`` without ``--apply`` must only report a plan, never write."""

    FILAMENTS = [
        {"id": 1, "gtin": "00123456789012", "article_number": None},  # already has gtin
        {"id": 2, "gtin": None, "article_number": "123456789012"},  # valid GTIN-12 -> planned
        {"id": 3, "gtin": None, "article_number": "PLA-GB-1000"},  # genuine SKU -> skipped
        {"id": 4, "gtin": None, "article_number": None},  # nothing to backfill
    ]

    def test_dry_run_makes_no_patch_calls_and_exits_zero(self):
        exit_code, output, mock_request_json = _run_main(
            ["--spoolman-url", "http://localhost:7912"], self.FILAMENTS
        )
        self.assertEqual(exit_code, 0)
        mock_request_json.assert_not_called()
        self.assertIn("DRY RUN", output)
        self.assertIn("to backfill       : 1", output)
        self.assertIn("filament 2: article_number='123456789012' -> gtin=00123456789012", output)

    def test_dry_run_skips_existing_gtin_and_genuine_sku(self):
        _exit_code, output, _mock = _run_main(["--spoolman-url", "http://localhost:7912"], self.FILAMENTS)
        self.assertIn("gtin already set  : 1", output)
        self.assertIn("article_number is a SKU (skipped) : 1", output)
        # Neither the already-set nor the SKU-only filament should appear as planned.
        self.assertNotIn("filament 1:", output)
        self.assertNotIn("filament 3:", output)

    def test_nothing_to_backfill_short_circuits(self):
        no_op_filaments = [{"id": 1, "gtin": "00123456789012", "article_number": None}]
        exit_code, output, mock_request_json = _run_main(
            ["--spoolman-url", "http://localhost:7912"], no_op_filaments
        )
        self.assertEqual(exit_code, 0)
        mock_request_json.assert_not_called()
        self.assertIn("Nothing to backfill", output)


class MainApplyTests(unittest.TestCase):
    """``--apply`` must write exactly the planned filaments and reflect server truth."""

    FILAMENTS = [{"id": 2, "gtin": None, "article_number": "123456789012"}]

    def test_apply_success_writes_and_exits_zero(self):
        exit_code, output, mock_request_json = _run_main(
            ["--spoolman-url", "http://localhost:7912", "--apply"],
            self.FILAMENTS,
            patch_responses={2: {"id": 2, "gtin": "00123456789012"}},
        )
        self.assertEqual(exit_code, 0)
        mock_request_json.assert_called_once()
        self.assertIn("OK   filament 2 -> 00123456789012", output)
        self.assertIn("Backfilled 1/1 filaments.", output)

    def test_apply_reports_failure_when_server_does_not_persist_gtin(self):
        # Spoolman without the `gtin` column silently ignores the field and still
        # returns 200 -- the script must detect this by reading the value back,
        # not by trusting a non-error HTTP response.
        exit_code, output, _mock = _run_main(
            ["--spoolman-url", "http://localhost:7912", "--apply"],
            self.FILAMENTS,
            patch_responses={2: {"id": 2, "gtin": None}},
        )
        self.assertEqual(exit_code, 2)
        self.assertIn("FAIL filament 2", output)
        self.assertIn("did not persist gtin", output)

    def test_apply_reports_failure_on_network_error(self):
        # No entry for filament 2 in patch_responses => fake_request_json raises.
        exit_code, output, _mock = _run_main(
            ["--spoolman-url", "http://localhost:7912", "--apply"],
            self.FILAMENTS,
            patch_responses={},
        )
        self.assertEqual(exit_code, 2)
        self.assertIn("FAIL filament 2", output)
        self.assertIn("re-run to retry the remainder", output)


if __name__ == "__main__":
    sys.exit(0 if unittest.main(exit=False, verbosity=2).result.wasSuccessful() else 1)
