# Kane — #405 Revision (Round 2 Response)

**Date**: 2025-06-02  
**Branch**: `squad/405-sqlserver-loginaudit-fix`  
**Addressing**: Hicks's `REQUEST_CHANGES` blockers from `hicks-405-round2.md`

---

## Blocker 1: SQLite `HasConversion` lossiness

**Decision**: Acceptable in practice — document the constraint, don't change behavior.

`LoginAuditService.RecordAsync` always writes `DateTimeOffset.UtcNow`, so every
persisted `Timestamp` has offset `+00:00`. The `HasConversion` lossiness only fires
if a caller writes a non-UTC offset, which the service contract forbids.

**Fix applied**: Added a 3-line comment directly above the `HasConversion` call in
`AppDbContext.cs` explaining (a) why the conversion exists, (b) that it is lossy for
non-UTC offsets, and (c) that the service contract forbids that scenario.

```csharp
// SQLite has no native DateTimeOffset type. We normalize to UTC for storage
// since LoginAuditService always writes DateTimeOffset.UtcNow. This conversion
// is LOSSY for non-UTC offsets — that scenario is forbidden by service contract.
```

---

## Blocker 2: No API round-trip test for UTC timestamps

**Fix applied**: Added `GetLoginAudit_Timestamp_SerializesAsUtcIso8601` to
`SecurityAuditControllerTests.cs`.

What the test proves end-to-end:
1. Seeds a `LoginAuditEntry` with `DateTimeOffset.UtcNow` (offset `+00:00`) via EF Core.
2. GETs `/api/admin/security/login-audit` as an authenticated admin.
3. Parses the raw response JSON (not the deserialized DTO) to inspect the literal
   `timestamp` string.
4. Asserts it ends with `Z` or `+00:00` (both are valid UTC ISO 8601 representations).
5. Asserts `DateTimeOffset.TryParse` succeeds.
6. Asserts `parsed.Offset == TimeSpan.Zero`.

The test uses the existing `CustomWebApplicationFactory` + `SeedEntriesAsync` helpers —
no new mocking layers introduced.

---

## Test counts

| Scope | Before | After |
|---|---|---|
| `SecurityAuditControllerTests` | 11 | 12 |
| `LoginAuditServiceTests` | 7 | 7 |
| **Total (filter match)** | **18** | **19** |

All 19 passed. Build: 0 errors, all warnings pre-existing.
