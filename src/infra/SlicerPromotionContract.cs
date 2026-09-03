namespace Farm.Infrastructure;

/// <summary>Internal route and authentication contract for slicer artifact promotion content.</summary>
public static class SlicerPromotionContract
{
    /// <summary>Configuration section for the dedicated promotion transport.</summary>
    public const string SectionName = "SlicerPromotion";

    /// <summary>Configuration path for the dedicated shared credential.</summary>
    public const string SharedKeyPath = $"{SectionName}:SharedKey";

    /// <summary>Header carrying the dedicated shared credential.</summary>
    public const string ApiKeyHeaderName = "X-Slicer-Promotion-Key";

    /// <summary>Header carrying the owner-scoped operation identity that must hold the artifact pin.</summary>
    public const string OperationKeyHeaderName = "X-Slicer-Promotion-Operation-Key";

    /// <summary>Internal route base. This route is intentionally absent from public proxy routing.</summary>
    public const string RouteBase = "api/internal/slicer-promotion";

    /// <summary>Builds the relative content route for one pinned artifact.</summary>
    /// <param name="artifactId">Artifact identifier.</param>
    /// <returns>The internal relative route.</returns>
    public static string ArtifactContentPath(Guid artifactId) =>
        $"{RouteBase}/artifacts/{artifactId:D}/content";
}
