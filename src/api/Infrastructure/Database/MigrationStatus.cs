using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Infrastructure.Database;

public sealed record MigrationStatus(string Mode, bool HasMigrations, bool AppliedAny, string? Provider);
