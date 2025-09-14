using Farm.Web.Shared;

namespace Farm.Web.Api.Services.SlicerServices;

public interface ISlicerExecutableManager
{
    /// <summary>
    /// Attempt to find a configured executable path and argument template for the given engine.
    /// Arg template should contain placeholders {input} and {output} where appropriate.
    /// </summary>
    bool TryGetExecutable(SlicerEngineType engine, out string? executablePath, out string? argsTemplate);

    /// <summary>
    /// Validate that the configured executable for the engine is present and runnable.
    /// </summary>
    Task<bool> ValidateSlicerInstallationAsync(SlicerEngineType engine, CancellationToken cancellationToken = default);
}
