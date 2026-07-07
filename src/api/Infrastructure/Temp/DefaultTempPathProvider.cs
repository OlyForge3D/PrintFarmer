namespace Farm.Web.Api.Infrastructure.Temp;

/// <summary>
/// Default implementation that resolves a temp root using (descending precedence):
/// 1. Config value TempStorage:Path
/// 2. Environment variable PRINTFARM_TEMP_ROOT
/// 3. Current working directory / "temp" directory
/// </summary>
public sealed class DefaultTempPathProvider : ITempPathProvider
{
    private readonly string _tempRoot;

    public DefaultTempPathProvider(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? configured = configuration["TempStorage:Path"];
        string? env = Environment.GetEnvironmentVariable("PRINTFARM_TEMP_ROOT");
        _tempRoot = !string.IsNullOrWhiteSpace(configured)
            ? configured!
            : !string.IsNullOrWhiteSpace(env)
                ? env!
                : Path.Combine(Directory.GetCurrentDirectory(), "temp");

        try
        {
            _ = Directory.CreateDirectory(_tempRoot);
        }
        catch
        {
#pragma warning disable S5443 // Last-resort fallback for configured local development storage when app-owned temp root cannot be created.
            // Fallback to process temp if creation fails (last resort)
            string fallback = Path.GetTempPath();
#pragma warning restore S5443
            try
            {
                _ = Directory.CreateDirectory(fallback);
            }
            catch
            {
                // swallow – final fallback
            }

            _tempRoot = fallback;
        }
    }

    public string GetTempRoot() => _tempRoot;
}
