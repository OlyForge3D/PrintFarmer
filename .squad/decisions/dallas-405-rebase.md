# Dallas — PR #412 Rebase Log (squad/405-sqlserver-loginaudit-fix)

**Date:** 2026-06-01  
**Actor:** Dallas  
**PR:** #412 — `fix(#405): change LoginAuditEntry.Timestamp to DateTimeOffset`

---

## New HEAD SHA

`2aaf0deed`

---

## Rebase Situation

Branch had **6 commits** since merge base `50771cc6e`:

| SHA | Message | Disposition |
|---|---|---|
| `f03fdb538` | feat: Home Assistant settings persistence and admin integration endpoints | **Dropped** — already in `origin/development` via PR #411 |
| `b4680ba40` | fix(squad): address #371 trio blockers | **Dropped** — already in `origin/development` via PR #411 |
| `1487790fe` | chore(squad): add dallas-371 revision decision doc | **Dropped** — already in `origin/development` via PR #411 |
| `45333917a` | fix(squad): address #371 round-3 trio blockers | **Dropped** — already in `origin/development` via PR #411 |
| `ae87bedb8` | chore(squad): add brett-371 round-3 revision decision | **Dropped** — already in `origin/development` via PR #411 |
| `50b42a74a` | fix(#405): change LoginAuditEntry.Timestamp to DateTimeOffset | **Kept** — the actual #405 fix |

Strategy used: `git rebase --onto origin/development ae87bedb8` to replay only the DateTimeOffset commit on top of development. This cleanly avoided replaying #371 work that had already landed.

---

## Files That Conflicted and How Resolved

**No conflicts.** The `--onto` strategy sidestepped all conflicts by dropping the 5 already-merged commits. The single replayed commit (`50b42a74a`) applied cleanly against `origin/development`.

One minor fix was needed post-rebase: the PG migration's empty `Up()`/`Down()` methods triggered 4 new SA/S warnings. Both methods were given explanatory comments (no behavior change).

---

## Conflict Prevention Notes

The earlier first-attempt `git rebase origin/development` (without `--onto`) stopped immediately on `f03fdb538` with `add/add` conflicts on `AdminHomeAssistantController.cs`, `HomeAssistantSettings.cs`, and `HomeAssistantSmartPlugProvider.cs` — because those files were already present in `origin/development` (from PR #411/#371). The `--onto` skipping of those commits is the correct resolution.

---

## Build & Test Status

| Check | Result |
|---|---|
| `dotnet build ./farm-web.sln -c Debug` | ✅ 0 errors, 9 pre-existing warnings |
| `dotnet test --filter "FullyQualifiedName~LoginAudit\|FullyQualifiedName~SecurityAudit"` | ✅ 18/18 passed |

---

## PR Mergeability

```json
{
  "mergeStateStatus": "CLEAN",
  "mergeable": "MERGEABLE"
}
```

---

## Migration Snapshot Integrity

Both snapshot files correctly reflect:
- All entities added by #355 (passkeys) and #371 (power monitors, NFC, user settings)
- `LoginAuditEntry.Timestamp` as `DateTimeOffset` / `datetimeoffset` (SqlServer) / `timestamp with time zone` (Postgres)

The branch is clean and ready for review.
