using System;

namespace Farm.Infrastructure.Services.Startup;

/// <summary>
/// Abstraction over application startup readiness/status used by controllers and startup logic.
/// Keep minimal so callers can depend on the contract rather than the concrete implementation.
/// </summary>
public interface IStartupStatus
{
    /// <summary>Gets when initialization started.</summary>
    DateTime? InitializationStartedUtc { get; }

    /// <summary>Gets when initialization completed.</summary>
    DateTime? InitializationCompletedUtc { get; }

    /// <summary>Gets the total initialization duration.</summary>
    TimeSpan? InitializationDuration { get; }

    /// <summary>Gets any exception that caused initialization to fail.</summary>
    Exception? FailureException { get; }

    /// <summary>Gets the current startup phase.</summary>
    StartupPhase Phase { get; }

    /// <summary>Gets a value indicating whether the application is ready to serve requests.</summary>
    bool IsReady { get; }

    /// <summary>Gets a value indicating whether initialization failed.</summary>
    bool IsFailed { get; }

    /// <summary>Marks that initialization has started.</summary>
    void MarkInitializationStarted();

    /// <summary>Marks the application as ready to serve requests.</summary>
    void MarkReady();

    /// <summary>Marks that initialization failed with an optional exception.</summary>
    void MarkFailed(Exception? ex = null);
}
