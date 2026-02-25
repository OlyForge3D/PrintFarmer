using Farm.Infrastructure.Services.Authentication;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.Authentication;

/// <summary>
/// Background service that periodically cleans up expired token revocations.
/// Runs daily to remove revocation records that are past their expiration date.
/// </summary>
public class TokenRevocationCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<TokenRevocationCleanupService> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<TokenRevocationCleanupService> _logger = logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(24); // Run daily

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Token revocation cleanup service started");

        // Wait a bit before first run to let the app fully start
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupExpiredRevocationsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during token revocation cleanup");
            }

            // Wait until next cleanup
            try
            {
                await Task.Delay(_cleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when service is stopping
                break;
            }
        }

        _logger.LogInformation("Token revocation cleanup service stopped");
    }

    private async Task CleanupExpiredRevocationsAsync()
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        ITokenRevocationService tokenRevocationService = scope.ServiceProvider.GetRequiredService<ITokenRevocationService>();

        _logger.LogInformation("Starting token revocation cleanup");

        try
        {
            int deletedCount = await tokenRevocationService.CleanupExpiredRevocationsAsync();

            if (deletedCount > 0)
            {
                _logger.LogInformation($"Token revocation cleanup completed - removed {deletedCount} expired records");
            }
            else
            {
                _logger.LogDebug("Token revocation cleanup completed - no expired records found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup expired token revocations");
            throw;
        }
    }
}
