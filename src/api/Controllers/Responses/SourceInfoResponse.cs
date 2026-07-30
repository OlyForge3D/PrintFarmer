namespace Farm.Web.Api.Controllers.Responses;

/// <summary>
/// Identifies the license and corresponding source for the running PrintFarmer version.
/// </summary>
public sealed class SourceInfoResponse
{
    /// <summary>
    /// Product name.
    /// </summary>
    public required string Product { get; init; }

    /// <summary>
    /// Running product version.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Immutable source commit, or <see langword="null"/> for an unversioned development build.
    /// </summary>
    public string? Revision { get; init; }

    /// <summary>
    /// SPDX license expression for first-party PrintFarmer code.
    /// </summary>
    public required string License { get; init; }

    /// <summary>
    /// Whether an immutable corresponding-source link is available.
    /// </summary>
    public bool SourceAvailable { get; init; }

    /// <summary>
    /// Repository containing the corresponding source.
    /// </summary>
    public string? RepositoryUrl { get; init; }

    /// <summary>
    /// Exact source tree for <see cref="Revision"/>.
    /// </summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// Release source archive for the running version.
    /// </summary>
    public string? SourceArchiveUrl { get; init; }

    /// <summary>
    /// License text matching the running source.
    /// </summary>
    public string? LicenseUrl { get; init; }

    /// <summary>
    /// Third-party notices matching the running source.
    /// </summary>
    public string? NoticesUrl { get; init; }

    /// <summary>
    /// Release SBOM matching the running version.
    /// </summary>
    public string? SbomUrl { get; init; }
}
