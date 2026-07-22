# Dallas → Squad: #355 Passkey Enrollment — Revision Complete

**Branch**: `squad/355-passkey-enrollment`
**Commit**: `4183347b1`
**Date**: 2025-07-18

---

## What Was Fixed

All three consensus blockers from the Bishop/Hicks/Vasquez round-one reviews are addressed.

### Blocker 1 — Dead catch path in `AuthContext.loginWithPasskey`

**Root cause**: The `catch` block swallowed every error and returned `false`, making the
`LoginModal` catch unreachable for ceremony failures (user cancel, hardware timeout, network,
401 assertion failure).

**Fix**: Removed the `catch` block entirely. Errors now propagate naturally through the
`finally { setIsLoading(false) }` clause. Backend soft-failures (`result.success === false`
on a 200 OK response) are still handled inline in the `try` branch via `setError()` + `return false`.

Two-tier failure model:
- **Ceremony error** (throws) → re-thrown → `LoginModal` catch → `passkeyError` state → shown
  inline near the passkey button
- **Backend soft-fail** (no throw, 200 body) → `setError()` in AuthContext → shown at top of
  modal via the existing `{error && …}` block

**`LoginModal` catch** also updated to extract `apiErr.details ?? apiErr.message` — axios errors
arrive as plain objects (`{ message, statusCode, details? }`), not `Error` instances, so
`instanceof Error` checks would have swallowed the message silently.

---

### Blocker 2 — Global 401 interceptor hijacking passkey assertion failures

**Root cause**: The backend returns HTTP 401 (with an `AuthenticationResult` body) for failed
passkey assertions. When `LoginModal` is open as a modal overlay, the user is on a page other
than `/login`, so the global interceptor's `pathname !== '/login'` guard does not suppress the
redirect. The user gets navigated away instead of seeing an inline error.

**Fix**: Added a URL guard in `api.ts` — the redirect+token-clear is skipped for any response
whose config URL ends with `/auth/passkey/login/complete`. All other 401s (expired session
tokens, protected resource access) still trigger the full logout+redirect flow.

**Scope note**: The guard is intentionally narrow. The `/auth/passkey/login/complete` endpoint
is the only one in the system that returns 401 semantically meaning "wrong credential" rather
than "unauthenticated session". If passkey login is ever embedded in additional non-login-page
contexts beyond the current modal, this remains correct. If new auth endpoints adopt the same
401-for-wrong-credential pattern, each would need its own exemption — worth a wider
interceptor-design audit at that point.

---

### Blocker 3 — Rename-by-diff race condition in `PasskeysPage`

**Root cause**: `registerMutation` snapshotted `beforeIds`, registered, re-fetched the list,
then diffed to find the new credential ID. Any concurrent registration (same user, different
tab/device) could produce multiple new IDs, and the diff was non-deterministic.

**Fix**: Backend now returns the ID directly.

- `IPasskeyService.CompleteRegistrationAsync` return type changed to
  `Task<(RegisteredPublicKeyCredential Credential, int NewCredentialId)>`.
- `PasskeyService.cs` captures `credential.Id` (EF-generated int PK) after `SaveChangesAsync`
  and returns it in the tuple. EF Core populates the PK on the local entity after the save, so
  this is reliable.
- `AuthController.PasskeyRegisterCompleteAsync` destructures the tuple and includes
  `newCredentialId` in the `200 OK` response body alongside the existing `credentialId`
  (which is the base64-encoded FIDO2 ID — a different value).
- Frontend `passkeyService.registerPasskey` return type extended to include `newCredentialId: number`.
- `PasskeysPage.registerMutation` now calls `result.newCredentialId` directly and eliminates all
  before/after snapshot and diff logic.

---

## Bonus: Pre-existing merge conflict resolved

`AppDbContext.cs` had unresolved conflict markers between this branch's HEAD
(`PowerMonitor`/`PowerReading` DbSets) and commit `7be713d3d` (`UserSettings` DbSet from #359).
Both sets of DbSets are independently valid — resolved by keeping both sides.

---

## What the Trio Should Re-Verify

| Area | What to check |
|---|---|
| `AuthContext.tsx` | `loginWithPasskey` — no `catch` block; errors propagate through `finally` |
| `LoginModal.tsx` | `handlePasskeyLogin` catch uses `apiErr.details ?? apiErr.message`; `role="alert"` inline error renders near passkey button |
| `api.ts` ~line 297 | 401 interceptor skips redirect only for `endsWith('/auth/passkey/login/complete')` |
| `PasskeyService.cs` | Tuple return after `SaveChangesAsync`; `credential.Id` is the EF int PK |
| `AuthController.cs` | `PasskeyRegisterCompleteAsync` response includes both `credentialId` and `newCredentialId` |
| `PasskeysPage.tsx` | `registerMutation` — no `beforeIds`, no re-fetch, no diff; uses `result.newCredentialId` |
| New test | `AuthContext.passkey.test.tsx` — 3 tests, real `AuthProvider`, all pass |

---

## Open Items / Notes for Next Round

1. **`LoginAuditPage` unused-var lint error** (`App.tsx` line 57) — pre-existing, flagged in
   Ripley's self-review. Needs a separate cleanup task; out of scope for this branch.

2. **Password login same 401 pattern** — `LoginController` returns 401 for wrong password via
   the same `Unauthorized(result)` pattern. This is currently suppressed by the interceptor's
   `pathname === '/login'` guard (password login is always shown on the `/login` page). If the
   password login form ever becomes a modal overlay, the same blocker would re-emerge. Recommend
   a tracking issue.

3. **6 pre-existing backend test failures** — `OrcaSlicerProfilesProviderTests` (×5) and
   `MmuToolheadRetroSyncTests` (×1) — all pre-date this branch. Not caused by any change here.

---

*Dallas out.*
