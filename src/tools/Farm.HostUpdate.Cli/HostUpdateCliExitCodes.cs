namespace Farm.HostUpdate.Cli;

/// <summary>Stable, documented process exit codes. Scripts must branch on these, never on text.</summary>
public static class HostUpdateCliExitCodes
{
    /// <summary>Command succeeded; for <c>recover --confirm</c> the release is durably rolled back and admission reopened.</summary>
    public const int Success = 0;

    /// <summary>Invalid command, option, or identifier. Nothing was read or changed.</summary>
    public const int Usage = 2;

    /// <summary>Configuration or same-namespace proof failed. Nothing was changed.</summary>
    public const int ConfigurationUnproven = 3;

    /// <summary>Durable state (journal, outcome store, installed state) is unreadable or fails integrity.</summary>
    public const int StateUnreadable = 4;

    /// <summary>The journal has no history for the requested release.</summary>
    public const int NoHistory = 5;

    /// <summary>The recovery request was refused (not in recovery, binding missing/mismatched, request mismatch).</summary>
    public const int Refused = 6;

    /// <summary>The execution lock is held by another process (an executor, the API, or another CLI).</summary>
    public const int LockHeld = 7;

    /// <summary>No supported automatic path; an operator must resolve the host manually.</summary>
    public const int NeedsOperator = 10;

    /// <summary>The rollback is durable but the admission fence release still has to be retried.</summary>
    public const int FenceReleasePending = 11;

    /// <summary>
    /// Drift since the recorded authorization was detected and not reapproved with the exact
    /// token from <c>recover --preview</c> (issue #2998). Nothing was changed.
    /// </summary>
    public const int DriftUnapproved = 12;
}
