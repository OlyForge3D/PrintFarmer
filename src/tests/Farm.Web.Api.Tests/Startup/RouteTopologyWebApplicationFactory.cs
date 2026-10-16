namespace Farm.Web.Api.Tests.Startup;

/// <summary>
/// Boots the API with every route-affecting deployment input pinned, so the route table does not
/// depend on the runner's architecture or ambient environment (issue #2821, finding R3394-H01).
/// </summary>
/// <remarks>
/// Program.cs reads <c>DEPLOYMENT_MODE</c>/<c>DEPLOYMENT_TYPE</c> and <c>Slicer:Enabled</c> before the
/// host is built, and auto-disables the slicer on ARM unless <c>Slicer:Enabled</c> is explicit.
/// <c>ConfigureAppConfiguration</c> overrides arrive too late, so the inputs are pinned through
/// process environment variables and restored on dispose. Test classes using this factory must
/// join the serialized <c>SlicerDisabled</c> collection.
/// </remarks>
internal sealed class RouteTopologyWebApplicationFactory : CustomWebApplicationFactory
{
    // Neither "monolith" nor "microservices": mirrors an unset deployment mode without letting a
    // DEPLOYMENT_TYPE fallback leak in from the runner.
    private const string DefaultDeploymentMode = "route-snapshot-default";

    private static readonly string[] PinnedVariables = ["DEPLOYMENT_MODE", "DEPLOYMENT_TYPE", "Slicer__Enabled"];

    private readonly Dictionary<string, string?> _originalValues = new(StringComparer.Ordinal);

    private RouteTopologyWebApplicationFactory(
        string deploymentMode,
        bool slicerEnabled,
        Dictionary<string, string?>? configOverrides)
        : base(configOverrides)
    {
        foreach (string name in PinnedVariables)
        {
            _originalValues[name] = Environment.GetEnvironmentVariable(name);
        }

        Environment.SetEnvironmentVariable("DEPLOYMENT_MODE", deploymentMode);
        Environment.SetEnvironmentVariable("DEPLOYMENT_TYPE", null);
        Environment.SetEnvironmentVariable("Slicer__Enabled", slicerEnabled ? "true" : "false");
    }

    /// <summary>Single-process deployment with the inline slicer module loaded (the canonical snapshot).</summary>
    public static RouteTopologyWebApplicationFactory SlicerEnabled(Dictionary<string, string?>? configOverrides = null) =>
        new(DefaultDeploymentMode, slicerEnabled: true, configOverrides);

    /// <summary>Microservices deployment: slicer routes are served by the separate slicer-host.</summary>
    public static RouteTopologyWebApplicationFactory Microservices(Dictionary<string, string?>? configOverrides = null) =>
        new("microservices", slicerEnabled: true, configOverrides);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach ((string name, string? value) in _originalValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        base.Dispose(disposing);
    }
}
