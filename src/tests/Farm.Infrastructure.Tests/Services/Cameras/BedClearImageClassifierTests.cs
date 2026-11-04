using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Cameras;

namespace Farm.Infrastructure.Tests.Services.Cameras;

public sealed class BedClearImageClassifierTests
{
    [Fact]
    public void Classify_WhenReferenceCountIsBelowMinimum_ReturnsUnknown()
    {
        BedClearImageResult result = BedClearImageClassifier.Classify(
            1,
            [1]);

        result.Verdict.Should().Be(BedClearDetectorVerdict.Unknown);
        result.ReasonCode.Should().Be("insufficient_references");
        result.Score.Should().BeNull();
    }

    [Fact]
    public void Classify_WhenCurrentImageMatchesReviewedReferences_ReturnsClear()
    {
        BedClearImageResult result = BedClearImageClassifier.Classify(
            0x0000_0000_0000_0000,
            [0x0000_0000_0000_0000, 0x0000_0000_0000_0000, 0x0000_0000_0000_0000]);

        result.Verdict.Should().Be(BedClearDetectorVerdict.Clear);
        result.Score.Should().Be(1);
    }

    [Fact]
    public void Classify_WhenCurrentImageDiffersFromReviewedReferences_ReturnsOccupied()
    {
        BedClearImageResult result = BedClearImageClassifier.Classify(
            ulong.MaxValue,
            [0, 0, 0]);

        result.Verdict.Should().Be(BedClearDetectorVerdict.Occupied);
        result.Score.Should().NotBeNull();
    }

    [Theory]
    [InlineData(0, BedClearDetectorVerdict.Clear)]
    [InlineData(5, BedClearDetectorVerdict.Clear)]
    [InlineData(6, BedClearDetectorVerdict.Unknown)]
    [InlineData(14, BedClearDetectorVerdict.Unknown)]
    [InlineData(15, BedClearDetectorVerdict.Occupied)]
    public void Classify_AtDistanceBoundary_ReturnsExpectedVerdict(
        int differingBits,
        BedClearDetectorVerdict expected)
    {
        ulong hash = (1UL << differingBits) - 1;
        BedClearImageResult result = BedClearImageClassifier.Classify(hash, [0, 0, 0]);

        result.Verdict.Should().Be(expected);
        result.Score.Should().Be(1d - (differingBits / 64d));
    }

    [Fact]
    public void Classify_WhenCurrentHashIsMissing_ReturnsUnknownWithNoScore()
    {
        BedClearImageResult result = BedClearImageClassifier.Classify(null, [0, 0, 0]);

        result.Verdict.Should().Be(BedClearDetectorVerdict.Unknown);
        result.Score.Should().BeNull();
        result.ReasonCode.Should().Be("invalid_image");
    }
}
