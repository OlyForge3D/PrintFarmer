# Decision: Passkey Management UI (#356)

**Date:** 2025-01-31
**Author:** Ripley (Frontend)
**Status:** Implemented

## Context

Issue #356 requires a passkey management UI under profile settings. Users need to list, rename, and revoke registered passkey credentials.

## Decisions

1. **Route:** `/profile/passkeys` — consistent with existing `/profile/api-keys` pattern.
2. **Backend endpoints:** Added to `AuthController` under `passkey/credentials` path:
   - `GET /api/auth/passkey/credentials` — list
   - `DELETE /api/auth/passkey/credentials/{id}` — revoke
   - `PATCH /api/auth/passkey/credentials/{id}` — rename
3. **Service layer:** Extended `IPasskeyService` / `PasskeyService` with `ListCredentialsAsync`, `DeleteCredentialAsync`, `RenameCredentialAsync`.
4. **Frontend service:** Standalone `passkeyService.ts` (mirroring `apiKeysService.ts` pattern) using `apiClient.request()`.
5. **Add passkey button:** Currently links to `/profile/passkeys/register` — will be connected to enrollment ceremony from #355.
6. **No "last passkey" guard yet:** Issue mentions "cannot remove last passkey when no password set" — deferred until password-status API is available.

## Tradeoffs

- Kept backend additions minimal (no separate controller file) since they naturally belong with existing passkey endpoints in `AuthController`.
- Used `int` ID for credential operations since the entity uses surrogate `int` PK.
