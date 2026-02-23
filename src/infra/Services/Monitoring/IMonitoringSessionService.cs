namespace Farm.Infrastructure.Services.Monitoring;

public interface IMonitoringSessionService
{
    string CreateMonitoringToken(string username);

    Task<MonitoringTokenValidationResult> ValidateMonitoringTokenAsync(string token);
}

public record MonitoringTokenValidationResult(bool IsValid, string? Username = null);
