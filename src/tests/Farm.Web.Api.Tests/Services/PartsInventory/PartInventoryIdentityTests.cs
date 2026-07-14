using Farm.Infrastructure.Services.PartsInventory;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.PartsInventory;

/// <summary>
/// Unit tests for <see cref="PartInventoryIdentity"/> (issue #715, Hicks r4 blocker 1). SKU and
/// bin-code identity must fold Unicode compatibility/width variants (NFKC) BEFORE upper-casing, so
/// app-side identity matches SQL Server's width-insensitive default collation on the
/// <c>PartInventory.Sku</c> / bin-code columns. Without this, a fullwidth SKU mints a distinct
/// idempotency record yet resolves to the same physical row at the store, double-applying a stock
/// delta under one Idempotency-Key.
///
/// <para>
/// Test inputs use explicit <c>\uXXXX</c> escapes so the exact code points under test are
/// unambiguous and immune to source-encoding surprises.
/// </para>
/// </summary>
public class PartInventoryIdentityTests
{
    [Theory]
    // ASCII is already NFKC + upper: existing behavior is unchanged (guardrail against regressions).
    [InlineData("ABC", "ABC")]
    [InlineData("abc", "ABC")]
    [InlineData("  rd-1  ", "RD-1")]            // trimmed then upper-cased, exactly as before
    // Fullwidth Latin letters ＡＢＣ (U+FF21..U+FF23) collapse to ASCII ABC.
    [InlineData("\uFF21\uFF22\uFF23", "ABC")]
    // Mixed width A + fullwidth Ｂ + ascii c → ABC.
    [InlineData("A\uFF22c", "ABC")]
    // Fullwidth digits １２３ (U+FF11..U+FF13) collapse to ASCII 123.
    [InlineData("\uFF11\uFF12\uFF13", "123")]
    // The ﬁ ligature (U+FB01) decomposes to "fi" under NFKC → "FILE".
    [InlineData("\uFB01le", "FILE")]
    public void NormalizeSku_AppliesNfkcThenUppercases(string input, string expected)
        => PartInventoryIdentity.NormalizeSku(input).Should().Be(expected);

    [Fact]
    public void NormalizeSku_WidthVariantsShareOneCanonicalIdentity()
    {
        // The core of blocker 1: ASCII and fullwidth spellings of the same SKU must normalize to
        // the identical string so both the idempotency route key and the domain lookup agree.
        string ascii = PartInventoryIdentity.NormalizeSku("ABC");
        string fullwidth = PartInventoryIdentity.NormalizeSku("\uFF21\uFF22\uFF23"); // ＡＢＣ

        _ = fullwidth.Should().Be(ascii, "a width-equivalent SKU must map to the same canonical identity");
    }

    [Fact]
    public void NormalizeSku_ComposedAndDecomposedAccentsAreEquivalent()
    {
        // NFKC also unifies canonical composition: "Café" written precomposed (é = U+00E9) and
        // decomposed (e + combining acute U+0301) must yield one identity, so an accented SKU
        // cannot fork into two records / two rows on keyboard-composition differences alone.
        string precomposed = PartInventoryIdentity.NormalizeSku("Caf\u00E9");       // Café
        string decomposed = PartInventoryIdentity.NormalizeSku("Cafe\u0301");       // Cafe + ́

        _ = decomposed.Should().Be(precomposed);
        _ = precomposed.Should().Be("CAF\u00C9", "the normalized identity is upper-cased NFKC (É = U+00C9)");
    }

    [Theory]
    // Bin codes share the same normalizer, so they get the same width-insensitive alignment.
    [InlineData("BIN-A", "BIN-A")]
    [InlineData("\uFF22\uFF29\uFF2E-A", "BIN-A")] // fullwidth ＢＩＮ-A → BIN-A
    public void NormalizeBinCode_AppliesNfkc(string input, string expected)
        => PartInventoryIdentity.NormalizeBinCode(input).Should().Be(expected);

    [Fact]
    public void NormalizeOperationKey_TrimsButDoesNotFoldOrUppercase()
    {
        // The operation key is persisted verbatim (only trimmed): it is a client-opaque token, and
        // NFKC-normalizing it at rest could break the (PartInventoryId, OperationKey) uniqueness of
        // legitimate clients using accented characters. NFKC is applied to the operation key ONLY
        // for the reserved-prefix comparison (see IdempotencyKeyUtilities.IsReservedOperationKey),
        // never for storage.
        _ = PartInventoryIdentity.NormalizeOperationKey("  Caf\u00E9-op  ").Should().Be("Caf\u00E9-op");
        _ = PartInventoryIdentity.NormalizeOperationKey(null).Should().BeNull();
        _ = PartInventoryIdentity.NormalizeOperationKey("   ").Should().BeNull();
    }
}
