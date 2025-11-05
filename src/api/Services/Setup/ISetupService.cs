using Farm.Web.Shared;
using Farm.Web.Shared.Contracts.Setup;

namespace Farm.Web.Api.Services.Setup;

/// <summary>
/// Service for handling initial application setup and configuration.
/// </summary>
public interface ISetupService
{
    /// <summary>
    /// Checks if the application needs initial setup.
    /// Returns true if no admin users exist in the system.
    /// </summary>
    Task<bool> NeedsSetupAsync(CancellationToken ct);

    /// <summary>
    /// Creates the initial admin user and completes first-run setup.
    /// This is only allowed when no admin users exist.
    /// </summary>
    /// <param name="request">Initial admin user details</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Authentication result with token if successful</returns>
    Task<AuthenticationResult> CreateInitialAdminAsync(CreateInitialAdminRequest request, CancellationToken ct);

    /// <summary>
    /// Gets available configuration options for setup.
    /// </summary>
    SetupConfigurationOptions GetConfigurationOptions();
}

/// <summary>
/// Configuration options available during setup.
/// </summary>
public record SetupConfigurationOptions(
    string[] DatabaseProviders,
    string[] DefaultNetworkRanges,
    Dictionary<string, int> RecommendedPorts
);
