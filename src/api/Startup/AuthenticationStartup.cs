using System.Text;
using Farm.Web.Api.Authorization;
using Farm.Web.Api.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace Farm.Web.Api.Startup;

/// <summary>
/// Configures JWT Authentication and Authorization policies.
/// </summary>
public static class AuthenticationStartup
{
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
                // Always accept ?access_token= for SignalR hub transports; keep verbose diagnostics disabled here
                // because startup logging is only wired for Development/Testing elsewhere.
                options.Events = ProgramHelpers.CreateJwtEvents(null, null);

                // Allow HTTP in test runs and relax validation for test environment
                if (environment.EnvironmentName == "Testing")
                {
                    options.RequireHttpsMetadata = false;
                }

                string? key = configuration["Jwt:Key"];
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new InvalidOperationException("JWT Key not configured. Provide a 32+ character secret via environment variable Jwt__Key or user-secrets in development.");
                }

                string issuer = configuration["Jwt:Issuer"] ?? "PrintFarmer";
                string audience = configuration["Jwt:Audience"] ?? "PrintFarmer";

                TokenValidationParameters tvp = new()
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
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
        });

        // Register authorization handlers
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, DevModeAuthorizationHandler>();

        return services;
    }
}
