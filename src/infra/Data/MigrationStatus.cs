namespace Farm.Infrastructure.Data;

public sealed record MigrationStatus(string Mode, bool HasMigrations, bool AppliedAny, string? Provider);
