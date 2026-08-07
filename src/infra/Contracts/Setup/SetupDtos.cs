using System;

namespace Farm.Infrastructure.Contracts.Setup;

public class CreateInitialAdminRequest
{
    public string Username { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string? FirstName { get; set; }

    public string? LastName { get; set; }
}

/// <summary>
/// Non-secret deployment defaults exposed only while initial setup is required.
/// </summary>
/// <param name="BaseUrl">The deployment-configured Spoolman base URL, or an empty string when unset.</param>
public sealed record SetupBootstrapResponse(string BaseUrl);
