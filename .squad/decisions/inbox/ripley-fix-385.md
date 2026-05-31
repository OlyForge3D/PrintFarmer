# Decision: Optimistic Concurrency for Settings Writes

**Author:** Ripley (Frontend, backend fix per lockout rule)  
**PR:** #385  
**Date:** 2025-05-31  

## Context

Multi-writer scenarios on settings endpoints (PUT /api/settings/user and PUT /api/settings/farm)
could silently overwrite changes made by concurrent writers — a classic lost-update problem.

## Decision

Add application-managed concurrency tokens (`RowVersion` byte[] column) to:
- `UserSettings` entity (per-user preferences)
- `AppSettingsEntity` (farm-wide settings key-value store)

### Mechanism

1. **Token generation:** `AppDbContext.SaveChanges()` stamps a new GUID-based `RowVersion` on every Added/Modified entity.
2. **EF Core config:** `IsConcurrencyToken()` — provider-agnostic (works with SQLite, Postgres, SqlServer).
3. **PUT enforcement:** Clients supply `rowVersion` in the request body or `If-Match` header. Stale tokens yield HTTP 409 Conflict.
4. **Backward compatibility:** If no `rowVersion` is supplied, the write proceeds without a concurrency check (graceful degradation for older clients).

### Why not `IsRowVersion()` / `[Timestamp]`?

`IsRowVersion()` relies on server-side value generation (SQL Server `rowversion`, Postgres `xmin`). This creates provider-specific migration differences and breaks SQLite (local dev + tests). Application-managed tokens are simpler and portable.

## Migrations

- `AddSettingsConcurrencyTokens` for both PostgreSQL and SqlServer providers.
- Adds `RowVersion BYTEA/VARBINARY` column to `UserSettings` and `AppSettingsEntities` tables.

## Alternatives Considered

- **ETag via `UpdatedAt` timestamp:** Lower precision, timestamp collisions possible.
- **Database-native `xmin`/`rowversion`:** Provider-specific, doesn't work with SQLite.
- **Pessimistic locking:** Overly restrictive for settings that change infrequently.
