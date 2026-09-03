using Farm.Slicer.Module.Services;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>Reads promotion bytes directly from monolith artifact storage.</summary>
public sealed class LocalPromotionArtifactContentSource(IArtifactsService artifacts)
    : IPromotionArtifactContentSource
{
    private readonly IArtifactsService _artifacts =
        artifacts ?? throw new ArgumentNullException(nameof(artifacts));

    /// <inheritdoc />
    public async Task<PromotionArtifactContent?> OpenReadAsync(
        Guid artifactId,
        string operationKey,
        long expectedSizeBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        try
        {
            ArtifactContentStream? content =
                await _artifacts.OpenReadStreamAsync(artifactId, cancellationToken);
            return content is null
                ? null
                : PromotionArtifactContent.Create(content.Content, expectedSizeBytes, content.DisposeAsync);
        }
        catch (IOException exception)
        {
            throw new PromotionSourceTransportException(
                "Local promotion content could not be opened.",
                exception);
        }
    }
}
