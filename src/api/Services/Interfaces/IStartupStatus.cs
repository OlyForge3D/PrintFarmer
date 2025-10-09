using System;
using Farm.Web.Api.Services;

namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Abstraction over application startup readiness/status used by controllers and startup logic.
/// Keep minimal so callers can depend on the contract rather than the concrete implementation.
/// </summary>
public interface IStartupStatus
{
    DateTime? InitializationStartedUtc { get; }
    DateTime? InitializationCompletedUtc { get; }
    TimeSpan? InitializationDuration { get; }
    Exception? FailureException { get; }
    StartupPhase Phase { get; }
    bool IsReady { get; }
    bool IsFailed { get; }

    void MarkInitializationStarted();
    void MarkReady();
    void MarkFailed(Exception? ex = null);
}
