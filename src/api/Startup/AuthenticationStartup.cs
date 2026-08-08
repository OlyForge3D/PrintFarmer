using System.Text;
using Farm.Infrastructure.Authorization;
using Farm.Web.Api.Authorization;
using Farm.Web.Api.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.IdentityModel.Tokens;

namespace Farm.Web.Api.Startup;

/// <summary>
/// Configures JWT Authentication and Authorization policies.
/// </summary>
public static class AuthenticationStartup
{
    /// <summary>
    /// Minimum required length, in bytes, for a production JWT signing key. This is a
    /// floor on key length, not a measure of secret entropy/strength.
    /// </summary>
    public const int MinimumKeyLengthBytes = 32;

    /// <summary>
    /// JWT signing key values shipped as committed defaults anywhere in this repository's
    /// deployment templates (e.g. compose files). These must never be accepted outside
    /// Development, since anyone with repo access already knows them. Intentionally not
    /// documented further here beyond what already exists in the templates themselves.
    /// </summary>
    internal static readonly string[] ShippedPlaceholderKeys =
    [
        "dev-super-secret-key-change-this-please-1234567890",
    ];

    /// <summary>
    /// Validates that a configured JWT signing key is safe to use outside the
    /// <c>Development</c> environment: it must not be one of the placeholder values shipped
    /// in this repository's templates, and it must meet the minimum byte-length floor.
    /// Always allowed (no further checks) when running in <c>Development</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the key is empty, or (outside Development) is a known shipped placeholder
    /// or shorter than <see cref="MinimumKeyLengthBytes"/> bytes.
    /// </exception>
    public static void ValidateJwtKey(string? key, string environmentName)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("JWT Key not configured. Provide a 32+ byte secret via environment variable Jwt__Key or user-secrets in development.");
        }

        if (string.Equals(environmentName, "Development", StringComparison.Ordinal))
        {
            return;
        }

        foreach (string placeholder in ShippedPlaceholderKeys)
        {
            if (string.Equals(key, placeholder, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "JWT Key matches a placeholder value shipped in this repository's deployment templates. " +
                    "Configure a unique secret via environment variable Jwt__Key (the deploy installer generates one automatically).");
            }
        }

        int keyByteLength = Encoding.UTF8.GetByteCount(key);
        if (keyByteLength < MinimumKeyLengthBytes)
        {
            throw new InvalidOperationException(
                $"JWT Key is too short ({keyByteLength} bytes). A minimum of {MinimumKeyLengthBytes} bytes is required outside Development.");
        }
    }

    /// <summary>
    /// Adds PrintFarmer JWT Authentication and Authorization configuration.
    /// </summary>
    public static IServiceCollection AddPrintFarmerAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        // Add JWT Authentication
        services.AddAuthentication("Bearer")
            .AddJwtBearer("Bearer", options =>
            {
                // Avoid building a temporary service provider here. The event handlers resolve
                // request-scoped services when needed and also support SignalR access tokens.
                options.Events = ProgramHelpers.CreateJwtEvents(null, null);

                // Allow HTTP in test runs and relax validation for test environment
                if (environment.EnvironmentName == "Testing")
                {
                    options.RequireHttpsMetadata = false;
                }

                string? key = configuration["Jwt:Key"];
                ValidateJwtKey(key, environment.EnvironmentName);

                string issuer = configuration["Jwt:Issuer"] ?? "PrintFarmer";
                string audience = configuration["Jwt:Audience"] ?? "PrintFarmer";

                TokenValidationParameters tvp = new()
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key!)),
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };

                // NOTE: Previously issuer/audience validation was relaxed in the "Testing" environment.
                // All integration tests now obtain tokens exclusively via the authentication endpoints,
                // which generate tokens including both issuer and audience (see AuthenticationService).
                // Enforcing validation in tests prevents accidental acceptance of malformed tokens.
                // (If a future test truly needs to bypass these checks, generate a properly formed token
                // instead of weakening validation here.)
                options.TokenValidationParameters = tvp;
            });

        // Add Authorization with custom policies
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            options.AddPolicy("RequireAuthentication", policy => policy.RequireAuthenticatedUser());
            options.AddPolicy("RequireAdmin", policy =>
            {
                _ = policy.RequireAuthenticatedUser();
                _ = policy.RequireRole("farm_admin");
            });

            // Historical policy name used across controllers. Keep an alias so existing
            // controllers using [Authorize(Policy = "farm_admin")] continue to work.
            options.AddPolicy("farm_admin", policy =>
            {
                _ = policy.RequireAuthenticatedUser();
                _ = policy.RequireRole("farm_admin");
            });
            options.AddPolicy("CanViewSliceQueue", policy =>
            {
                _ = policy.RequireAuthenticatedUser();
                _ = policy.RequireRole("farm_admin");
            });

            // Desktop API-key exchange scope policies (issue #838). A normal login/session
            // token is unaffected (DesktopScopeAuthorizationHandler passes it through); only
            // a Desktop-exchange token must carry the specific scope claim.
            options.AddPolicy("ModelRead", policy =>
            {
                _ = policy.RequireAuthenticatedUser();
                _ = policy.AddRequirements(new DesktopScopeRequirement("ModelRead"));
            });
            options.AddPolicy("ModelWrite", policy =>
            {
                _ = policy.RequireAuthenticatedUser();
                _ = policy.AddRequirements(new DesktopScopeRequirement("ModelWrite"));
            });
            options.AddPolicy("LibrarySync", policy =>
            {
                _ = policy.RequireAuthenticatedUser();
                _ = policy.AddRequirements(new DesktopScopeRequirement("LibrarySync"));
            });
        });

        // Register authorization handlers
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, DevModeAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, DesktopScopeAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuthorizationProblemDetailsResultHandler>();

        return services;
    }
}
