# Fix-Up Assignments — Dallas Lead Synthesis (Cycle 2)

10 blocked PRs require revision. Per squad lockout rule, original author may NOT produce the fix.

## Assignment Matrix

| PR | Title | Original Author | Revision Owner | Severity |
|----|-------|-----------------|----------------|----------|
| #377 | NFC pairing modal (#361) | Ripley | **Lambert** | Normal |
| #381 | PowerMonitor entities + migrations (#346) | Lambert | **Ripley** | Normal |
| #383 | NFC backend event routing (#362) | Lambert | **Ripley** | Normal |
| #384 | Model3DFile attribution (#351) | Lambert | **Ripley** | **URGENT** |
| #385 | Settings per-user vs farm-wide backend (#359) | Lambert | **Ripley** | Normal |
| #387 | PowerMonitor management UI (#348) | Ripley | **Lambert** | Deferred (wait #386) |
| #390 | PrintJob cost aggregation (#344) | Lambert | **Ripley** | Normal |
| #391 | PowerMonitorPollingService (#347) | Lambert | **Ripley** | Normal |
| #393 | Home Assistant integration (#371) | Lambert | **Ripley** | Normal |
| #394 | Passkey login UI (#355) | Ripley | **Lambert** | Normal |

## Required Fixes Per PR

### #377 — Lambert to fix

1. Wire `useNfcPairingSession` hook to actual SignalR NFC events (not mocked delay)
2. Implement `/api/nfc/link` endpoint body (currently returns 501 Not Implemented)
3. Add error state handling when NFC reader is unavailable

### #381 — Ripley to fix

1. Regenerate migrations from clean baseline — remove unintended `LoginAuditEntries.Timestamp` column alteration
2. Verify idempotency: applying migration to existing DB must not alter unrelated tables
3. Add migration test asserting only `PowerMonitor`/`PowerReading` tables are created

### #383 — Ripley to fix

1. Add unique constraint on `NfcTagId` in `SpoolNfcLinks` table
2. Wrap link creation in retry-on-duplicate-key pattern or use `INSERT ... ON CONFLICT`
3. Add concurrency test verifying only one link succeeds for same tag ID

### #384 — Ripley to fix (**URGENT — SCRUB GATE VIOLATION**)

1. Remove ALL `bambuddy` and `maziggy` string literals, comments, and references from the diff
2. Replace with correct PrintFarmer-native attribution identifiers
3. Run `grep -ri 'bambuddy\|maziggy'` across the branch and confirm zero hits before re-submitting
4. This is a hard gate — PR cannot merge until scrub is verified clean

### #385 — Ripley to fix

1. Add `ConcurrencyToken`/`RowVersion` to settings entities
2. Implement ETag or `If-Match` header check on PUT endpoints
3. Return 409 Conflict on stale write attempt
4. Add test verifying concurrent write detection

### #387 — Lambert to fix (DEFERRED)

**Depends on #386 (backend endpoints) merging first.** lambert-15 is currently working on #386.

1. Wait for #386 (backend endpoints) to merge
2. Update API client calls to match actual endpoint contract from #386
3. Verify all CRUD operations (`GET/POST/PUT/DELETE /api/admin/power-monitors`) work against live backend
4. Alternative: merge #386 and #387 in lockstep if both ready simultaneously

### #390 — Ripley to fix

1. Same root cause as #381 — regenerate migrations from clean baseline
2. Ensure only `PrintJobCostAggregation`-related tables/columns are touched
3. Coordinate with #381 fix to avoid migration ordering conflicts (use sequential timestamps)

### #391 — Ripley to fix

1. Replace direct `IEnumerable<ISmartPlugProvider>` constructor injection with `IServiceScopeFactory`
2. Create a new scope per polling iteration and resolve providers within it
3. Add integration test verifying scoped providers are resolved per-iteration (no captive dependency)
4. Verify no startup crash when HA provider (#393) is registered as scoped

### #393 — Ripley to fix

1. Add URL validation rejecting loopback (127.0.0.0/8), link-local (169.254.0.0/16), and RFC1918 private ranges (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16)
2. Validate scheme is `http` or `https` only (reject `file://`, `ftp://`, etc.)
3. Add unit tests for SSRF-blocked URLs (localhost, 127.0.0.1, 10.0.0.1, 169.254.169.254, ::1)
4. Document security model: if HA is always on local network, consider admin-only URL configuration with explicit private-range opt-in

### #394 — Lambert to fix

1. Update all endpoint paths to match backend WebAuthn contract:
   - Registration: `/api/auth/webauthn/register-options` (GET) + `/api/auth/webauthn/register` (POST)
   - Login: `/api/auth/webauthn/login-options` (GET) + `/api/auth/webauthn/login` (POST)
2. Fix registration payload to send `attestationResponse` + `clientDataJSON` per FIDO2 spec
3. Fix assertion payload for login flow to match `Fido2NetLib` expected shapes
4. Add integration test asserting request/response shapes match backend DTOs

## Stacked PRs Awaiting Rebase (not blocked — mechanical)

These PRs were unanimously approved but need rebasing after parent squash-merge:

| Original PR | New PR | Branch | Needs |
|-------------|--------|--------|-------|
| #369 | #398 | squad/334-gcodeviewer3d-via-service | Rebase onto development |
| #373 | #399 | squad/335-preview-button-and-url-helper | Rebase onto development |
| #374 | #400 | squad/339-bed-type-override | Rebase onto development |
| #376 | #401 | squad/358-migrate-nav-into-settings | Rebase onto development |
| #379 | #402 | squad/350-printables-import-modal | Rebase onto development |
| #392 | #403 | squad/354-user-passkey-credential-entity | Rebase onto development |

Rebase is mechanical (no lockout applies). Original authors can rebase their own branches.
Branches are in worktrees — rebase must be done from those worktrees.

## Priority Order

1. **#384** — URGENT scrub gate violation (Ripley)
2. **#381 + #390** — migration corruption pair (Ripley, coordinate together)
3. **#393** — SSRF security issue (Ripley)
4. **#391** — DI crash on merge with #393 (Ripley)
5. **#383** — race condition (Ripley)
6. **#385** — concurrency (Ripley)
7. **#377** — incomplete wiring (Lambert)
8. **#394** — contract mismatch (Lambert)
9. **#387** — deferred until #386 lands (Lambert)
