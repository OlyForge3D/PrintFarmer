using System.Data.Common;
using System.Security.Claims;
using Farm.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Applies the same persisted account eligibility gate in the API and standalone slicer host.
/// </summary>
public static class JwtAccountEligibility
{
    /// <summary>
    /// Returns an authentication failure for a disabled, missing or unverifiable account.
    /// Cancellation and unexpected configuration failures propagate to the host.
    /// </summary>
    public static async Task<string?> GetFailureAsync(HttpContext context, ClaimsPrincipal? principal, ILogger? logger = null)
    {
        try
        {
            AppDbContext db = context.RequestServices.GetRequiredService<AppDbContext>();
            string? subject = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(subject, out Guid userId) ||
                !await db.Users.AnyAsync(user => user.Id == userId && user.IsActive, context.RequestAborted))
            {
                return "User account is disabled or no longer exists.";
            }
        }
        catch (DbException exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            LogStoreFailure(context, logger, exception);
            return "Unable to validate user account.";
        }
        catch (InvalidOperationException exception) when (
            !context.RequestAborted.IsCancellationRequested
            && exception.InnerException is (DbException or TimeoutException))
        {
            // EF's PostgreSQL/SQL Server execution strategies wrap transient store failures.
            LogStoreFailure(context, logger, exception);
            return "Unable to validate user account.";
        }

        return null;
    }

    private static void LogStoreFailure(HttpContext context, ILogger? logger, Exception exception)
    {
        logger ??= context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(JwtAccountEligibility));
        logger.LogError(exception, "[JWT][OnTokenValidated] Unable to validate user account.");
    }
}
