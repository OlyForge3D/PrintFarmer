namespace Farm.Web.Api.Tests;

/// <summary>
/// WebApplicationFactory that boots the API with <c>DEPLOYMENT_MODE=microservices</c>,
/// which prevents the slicer module from loading inline (simulating microservices deployment).
/// Uses an environment variable so the value is visible to
/// <c>builder.Configuration.GetValue()</c> BEFORE <c>builder.Build()</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ConfigureAppConfiguration</c> overrides are deferred until the host is built,
/// which is too late for service-registration decisions in Program.cs.
/// The <c>DEPLOYMENT_MODE</c> environment variable is read by the
/// <c>EnvironmentVariablesConfigurationProvider</c> that the
/// <c>ConfigurationManager</c> includes from the very start.
/// </para>
/// <para>
/// This factory must NOT run in parallel with tests that expect the slicer to be enabled.
/// Use <c>[Collection("SlicerDisabled")]</c> on the test class.
/// </para>
/// </remarks>
internal sealed class SlicerDisabledWebApplicationFactory : CustomWebApplicationFactory
{
    private const string EnvVarName = "DEPLOYMENT_MODE";
    private readonly string? _originalEnvValue;

    public SlicerDisabledWebApplicationFactory()
    {
        // Capture and override env var BEFORE the host boots (host is built lazily)
        _originalEnvValue = Environment.GetEnvironmentVariable(EnvVarName);
        Environment.SetEnvironmentVariable(EnvVarName, "microservices");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Environment.SetEnvironmentVariable(EnvVarName, _originalEnvValue);
        }

        base.Dispose(disposing);
    }
}
