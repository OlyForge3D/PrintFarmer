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

import importlib.util
import pathlib
import sys
import unittest

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


if __name__ == "__main__":
    sys.exit(0 if unittest.main(exit=False, verbosity=2).result.wasSuccessful() else 1)
