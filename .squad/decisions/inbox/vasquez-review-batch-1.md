# Vasquez 🔒 — Review Batch 1

**Date:** 2026-05-31
**Reviewer:** Vasquez (Code Review, architecture/security focus)
**PRs Reviewed:** #370, #375, #376

---

## Health Report

| PR | Title | Verdict | Blockers | Security | Migrations |
|----|-------|---------|----------|----------|------------|
| #370 | feat(power): ISmartPlugProvider abstraction | ✅ APPROVE | 0 | Clean | N/A (no schema) |
| #375 | feat(import): PrintablesImportService | ✅ APPROVE | 0 | Clean | N/A (read-only) |
| #376 | feat(settings): migrate nav → Settings tabs | ✅ APPROVE | 0 | 1 question | N/A (frontend) |

**Overall assessment:** All three PRs are architecturally sound, well-tested, and safe to merge. No blockers found. Minor nits and clarification questions noted inline.

---

## PR #370 — ISmartPlugProvider + 4 Providers

**Verdict: APPROVE**

### Strengths
- Provider pattern with `IEnumerable<ISmartPlugProvider>` DI registration — extensible without core changes
- Clean separation: each provider owns its protocol (TCP/XOR for Kasa, HTTP for rest)
- 22 unit tests with mocked HTTP handlers
- No premature DB coupling — `PowerReading` is a plain record

### Follow-ups (non-blocking)
1. `HomeAssistantSmartPlugProvider` mutates `client.DefaultRequestHeaders.Authorization` — should use per-request headers to avoid shared handler state issues
2. Kasa creates new `TcpClient` per call — document if polling cadence decreases below 5s

### Extensibility (Home Assistant #371)
Provider #4 (HA) is already in this PR. Adding a 5th provider requires only: implement interface + add one `services.AddSingleton<>()` line. ✅

---

## PR #375 — PrintablesImportService + GraphQL Client

**Verdict: APPROVE**

### Strengths
- Strict URL regex (`printables\.com/model/(\d+)`) prevents SSRF — only Printables domain, numeric IDs only
- Outbound calls hardcoded to `https://api.printables.com/graphql/` — no user-controlled destination
- `[Authorize]` on controller — authenticated users only
- Typed HttpClient with proper timeout and User-Agent
- 18 tests covering parsing, client, and controller layers

### Security notes
- No SSRF vector: user URL parsed for ID extraction only, never fetched directly
- No credential storage needed (Printables public API)
- Route `/api/3d-models/printables/preview` correctly under slicer-host ownership

---

## PR #376 — Settings Tab Migration (16 nav items)

**Verdict: APPROVE**

### Strengths
- All old routes preserved as redirects — zero bookmark breakage
- Pages retain full functionality, just remounted inside `SettingsShell`
- Lazy loading preserved for heavy pages (SlicerProfilesPage)
- 16 redirect tests + updated shell tests
- Nav simplified significantly (removed ~10 section headers and items)

### Clarification needed (non-blocking)
1. **ApiKeysPage access change**: Previously at `/profile/api-keys` (no admin gate). Now behind `/settings` which requires `farm_admin`. Was this intentional? If regular users need API keys, add a separate non-admin route.
2. **`/locations/dashboard`**: Stays as top-level route while `/locations` redirects to settings. Intentional separation of dashboard vs management?

---

## Merge Order Recommendation

1. **#370** (power) — no dependencies, independent
2. **#375** (import) — no dependencies, independent
3. **#376** (settings) — stacked on #367, merge after that base

All three can merge in parallel (no conflicts between them), respecting #376's stack dependency.

— Vasquez 🔒
