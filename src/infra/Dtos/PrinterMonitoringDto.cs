namespace Farm.Infrastructure;

/// <summary>Committed monitoring intent and the new revision for subsequent guarded edits.</summary>
public sealed record PrinterMonitoringDto(Guid Id, DateTime? ExpectedOfflineSinceUtc, string? RowVersion);
