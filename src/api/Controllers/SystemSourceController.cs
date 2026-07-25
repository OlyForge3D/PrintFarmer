using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Farm.Web.Api.Controllers.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Exposes public licensing and corresponding-source metadata.
/// </summary>
[ApiController]
[Route("api/system")]
public sealed partial class SystemSourceController(IConfiguration configuration) : ControllerBase
{
    private const string DefaultRepositoryUrl = "https://github.com/OlyForge3D/PrintFarmer";
    private const string LicenseExpression = "AGPL-3.0-only";
    private static readonly string[] MutableArtifactReferences =
    [
        "current",
        "develop",
        "development",
        "head",
        "latest",
        "main",
        "master",
        "nightly",
        "snapshot",
    ];

    private readonly IConfiguration _configuration = configuration;

    /// <summary>
    /// Returns immutable source, license, notice, and SBOM links for this build.
    /// </summary>
    /// <returns>Public corresponding-source metadata.</returns>
    [AllowAnonymous]
    [HttpGet("source")]
    [ProducesResponseType(typeof(SourceInfoResponse), StatusCodes.Status200OK)]
    public ActionResult<SourceInfoResponse> GetSource()
    {
        string? configuredRepositoryUrl = _configuration["SourceInfo:RepositoryUrl"];
        bool hasRepositoryOverride = configuredRepositoryUrl is not null;
        string? repositoryUrl = NormalizePublicUrl(
            hasRepositoryOverride ? configuredRepositoryUrl : DefaultRepositoryUrl);
        string version = ResolveVersion();
        string? revision = NormalizeRevision(_configuration["SourceInfo:Revision"]);
        string? sourceUrl = revision is null || repositoryUrl is null
            ? null
            : $"{repositoryUrl}/tree/{revision}";
        string releaseVersion = version.StartsWith('v') ? version : $"v{version}";
        string? releaseBaseUrl = repositoryUrl is not null && ReleaseVersionPattern().IsMatch(releaseVersion)
            ? $"{repositoryUrl}/releases/download/{releaseVersion}"
            : null;
        string? sourceArchiveFallback = !hasRepositoryOverride && releaseBaseUrl is not null
            ? $"{releaseBaseUrl}/PrintFarmer-{releaseVersion}-source.tar.gz"
            : null;
        string? sbomFallback = !hasRepositoryOverride && releaseBaseUrl is not null
            ? $"{releaseBaseUrl}/printfarmer-{releaseVersion}.spdx.json"
            : null;

        var response = new SourceInfoResponse
        {
            Product = "PrintFarmer",
            Version = version,
            Revision = revision,
            License = LicenseExpression,
            SourceAvailable = sourceUrl is not null,
            RepositoryUrl = repositoryUrl,
            SourceUrl = sourceUrl,
            SourceArchiveUrl = sourceUrl is null
                ? null
                : ResolveImmutableArtifactUrl(
                    _configuration["SourceInfo:SourceArchiveUrl"],
                    sourceArchiveFallback,
                    releaseVersion,
                    revision),
            LicenseUrl = sourceUrl is null ? null : $"{repositoryUrl}/blob/{revision}/LICENSE",
            NoticesUrl = sourceUrl is null ? null : $"{repositoryUrl}/blob/{revision}/THIRD-PARTY-NOTICES.md",
            SbomUrl = sourceUrl is null
                ? null
                : ResolveImmutableArtifactUrl(
                    _configuration["SourceInfo:SbomUrl"],
                    sbomFallback,
                    releaseVersion,
                    revision),
        };

        return Ok(response);
    }

    private string ResolveVersion()
    {
        string? configuredVersion = _configuration["SourceInfo:Version"]?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredVersion))
        {
            return configuredVersion;
        }

        return typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+', 2, StringSplitOptions.TrimEntries)[0]
            ?? "unknown";
    }

    private static string? NormalizeRevision(string? value)
    {
        string? revision = value?.Trim().ToLowerInvariant();
        return revision is { Length: 40 } && revision.All(Uri.IsHexDigit)
            ? revision
            : null;
    }

    private static string? ResolveImmutableArtifactUrl(
        string? configuredValue,
        string? fallback,
        string releaseVersion,
        string? revision)
    {
        string? normalizedUrl = configuredValue is null
            ? NormalizePublicUrl(fallback)
            : NormalizePublicUrl(configuredValue);
        if (normalizedUrl is null
            || !Uri.TryCreate(normalizedUrl, UriKind.Absolute, out Uri? uri)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (MutableArtifactReferences.Any(reference => ContainsPathToken(path, reference)))
        {
            return null;
        }

        bool containsRevision = revision is not null && ContainsExactPathSegment(path, revision);
        bool containsRelease = ReleaseVersionPattern().IsMatch(releaseVersion)
            && ContainsExactPathSegment(path, releaseVersion);
        return containsRevision || containsRelease
            ? normalizedUrl
            : null;
    }

    private static bool ContainsExactPathSegment(string path, string value)
    {
        return path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(segment => segment.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsPathToken(string path, string value)
    {
        int startIndex = 0;
        while (startIndex < path.Length)
        {
            int index = path.IndexOf(value, startIndex, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            int endIndex = index + value.Length;
            bool startsAtBoundary = index == 0 || !char.IsLetterOrDigit(path[index - 1]);
            bool endsAtBoundary = endIndex == path.Length || !char.IsLetterOrDigit(path[endIndex]);
            if (startsAtBoundary && endsAtBoundary)
            {
                return true;
            }

            startIndex = index + 1;
        }

        return false;
    }

    private static string? NormalizePublicUrl(string? value)
    {
        string? candidate = value?.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.IsLoopback
            || uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || IsPrivateAddress(uri.Host))
        {
            return null;
        }

        return candidate.TrimEnd('/');
    }

    [GeneratedRegex(@"^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersionPattern();

    private static bool IsPrivateAddress(string host)
    {
        if (!IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsPrivateAddress(address.MapToIPv4().ToString());
        }

        byte[] bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork =>
                bytes[0] == 0
                || bytes[0] == 10
                || bytes[0] == 127
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2)
                || (bytes[0] == 198 && bytes[1] is 18 or 19)
                || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                || bytes[0] >= 224,
            System.Net.Sockets.AddressFamily.InterNetworkV6 =>
                address.IsIPv6LinkLocal
                || address.IsIPv6Multicast
                || address.IsIPv6SiteLocal
                || address.Equals(IPAddress.IPv6Any)
                || address.Equals(IPAddress.IPv6Loopback)
                || (bytes[0] & 0xfe) == 0xfc
                || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8),
            _ => true,
        };
    }
}
