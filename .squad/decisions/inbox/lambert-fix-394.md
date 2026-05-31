# Lambert Fix — PR #394 Passkey Endpoint Contract

**Date:** 2025-07-16  
**Author:** Lambert (lockout rule, Ripley locked out)  
**Branch:** `squad/355-passkey-login-ui`  
**PR:** #394

## Problem

Passkey frontend was calling wrong endpoints and sending wrong payload shapes, causing runtime 404/400 failures. Blocked Bishop + Hicks.

## Root Cause

- Backend controller routes used `/begin` (passkey/register/begin, passkey/login/begin)  
- Frontend (squad/355) was already updated to call `/start` — mismatch caused 404  
- Login complete sent bare `assertion` object; backend expected `{ username, assertionResponse }` — caused 400  
- Login start sent `{ usernameOrEmail }` but backend DTO uses `username` — caused 400  
- `PasskeyService.cs` had merge conflict with development stub; HEAD (full implementation) should be kept  

## Changes Made

### Backend (`src/api/Controllers/AuthController.cs`)
- Renamed routes: `passkey/register/begin` → `passkey/register/start`, `passkey/login/begin` → `passkey/login/start`  
- Renamed methods: `PasskeyRegisterBeginAsync` → `PasskeyRegisterStartAsync`, `PasskeyLoginBeginAsync` → `PasskeyLoginStartAsync`  
- Renamed record: `PasskeyLoginBeginRequest` → `PasskeyLoginStartRequest`  
- Register complete now returns `{ success: true, credentialId }` (was `{ message, credentialId }`)  

### Backend (`src/infra/Services/Authentication/PasskeyService.cs`)
- Resolved merge conflict; kept HEAD (squad/355) implementation with full credential persistence, AAGUID extraction, and JWT issuance  

### Frontend (`src/Web/ReactApp/src/features/auth/services/passkeyService.ts`)
- Login start body: `usernameOrEmail` → `username`  
- Login complete body: `assertion` → `{ username: usernameHint ?? '', assertionResponse: assertion }`  

### Frontend (`src/Web/ReactApp/src/features/auth/types/passkey.ts`)
- `PasskeyRegisterCompleteResponse.credentialId` is now required (non-optional) to match the corrected backend response  

### Tests
- Added 13 Vitest tests in `src/Web/ReactApp/src/test/features/auth/passkeyService.test.ts`  
- Updated `PasskeyControllerTests.cs` to use new Start method/record names  

## Validation

- `dotnet build ./farm-web.sln` — ✅ Build succeeded  
- `npm run build` — ✅ built in 21.97s  
- `npm run test:run` — ✅ 13/13 passkey tests pass; 4 pre-existing failures in unrelated files  
- `npm run lint` — ✅ clean  
