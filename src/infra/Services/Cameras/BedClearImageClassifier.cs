using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>Compares normalized bed-region image hashes in shadow mode.</summary>
public static class BedClearImageClassifier
{
    public const int MinimumReferences = 3;
    private const float ClearScoreThreshold = 0.92f;
    private const float OccupiedScoreThreshold = 0.78f;

    /// <summary>
    /// Produces a fail-closed verdict from a current image and independently reviewed,
    /// camera/bed-type-matched empty-bed references.
    /// </summary>
    public static BedClearImageResult Classify(
        ulong? currentHash,
        IReadOnlyList<ulong> referenceHashes)
    {
        if (currentHash is null)
        {
            return BedClearImageResult.Unknown("invalid_image");
        }

        if (referenceHashes.Count < MinimumReferences)
        {
            return BedClearImageResult.Unknown("insufficient_references");
        }

        List<int> distances = referenceHashes
            .Select(referenceHash =>
                System.Numerics.BitOperations.PopCount(currentHash.Value ^ referenceHash))
            .ToList();
        distances.Sort();
        int medianDistance = distances[distances.Count / 2];
        float score = 1f - (medianDistance / 64f);
        BedClearDetectorVerdict verdict = score switch
        {
            >= ClearScoreThreshold => BedClearDetectorVerdict.Clear,
            <= OccupiedScoreThreshold => BedClearDetectorVerdict.Occupied,
            _ => BedClearDetectorVerdict.Unknown,
        };
        string? resultReason = verdict is BedClearDetectorVerdict.Unknown
            ? "ambiguous_similarity"
            : null;

        return new BedClearImageResult(verdict, score, resultReason);
    }
}

public sealed record BedClearImageResult(
    BedClearDetectorVerdict Verdict,
    double? Score,
    string? ReasonCode)
{
    public static BedClearImageResult Unknown(string reason) =>
        new(BedClearDetectorVerdict.Unknown, null, reason);
}

public interface IBedClearImageHashService
{
    bool TryCreateHash(ReadOnlyMemory<byte> imageBytes, out ulong hash, out string? reasonCode);
}
