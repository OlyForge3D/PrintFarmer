## Kane revision — #355 LoginModal integration test (round 3 blocker)

Commit: `f38803360`

### Blocker addressed

Bishop's round-3 blocker: `LoginModal.passkey.integration.test.tsx` mocked
`@/services/passkeyService`, short-circuiting the seam at the service boundary
instead of the HTTP layer.  The real `LoginModal → AuthContext → apiClient`
chain was not exercised.

### HTTP stubbing approach

**Custom axios adapter** — same pattern established by `api.interceptor.test.ts`.

The singleton `apiClient` exposes an internal `AxiosInstance` at the private
`client` field.  A URL-dispatch adapter is swapped onto
`axiosInstance.defaults.adapter` in `beforeEach` and removed in `afterEach`.
The adapter matches request URLs by substring and returns either a resolved
response object (2xx) or a rejected `AxiosError` (4xx), which the real
response interceptor then processes.

**Why this approach and not MSW:**
The project has no MSW dependency (`grep -E "msw|setupServer"` found nothing in
`package.json`, `test/`, or `src/`).  The interceptor test already established
the axios adapter pattern as the project convention.  Adding MSW would be a
new dependency for no benefit when the existing pattern covers the requirement
cleanly.

### What is mocked at the browser boundary

`@simplewebauthn/browser` → `startAuthentication` is mocked to return a fake
assertion object.

`startAuthentication` wraps `navigator.credentials.get()`.  jsdom does not
implement the WebAuthn browser API, so `startAuthentication` would throw
unconditionally in any test environment without a mock.  This is the correct
seam: it represents the physical hardware/platform boundary (authenticator
device or platform biometrics).  Everything above it — `passkeyService`,
`ApiClient`, the 401 interceptor with `skipAuthRedirect=true`, `AuthContext`,
`LoginModal` — is real and fully exercised.

### Test coverage

**Negative path** (`shows inline alert when /login/complete returns 401`):
- Adapter stubs `/auth/passkey/login/begin` → 200 (challenge options)
- Adapter stubs `/auth/passkey/login/complete` → 401 `{ error: 'Credential ID not found' }`
- Real interceptor sees `skipAuthRedirect=true` → does NOT redirect, does NOT
  clear token → normalises `AxiosError` to `ApiError` with `details`
- `ApiError` propagates through `AuthContext.loginWithPasskey` (no catch there)
  → caught in `LoginModal.handlePasskeyLogin` → `setPasskeyError`
- Asserts: `role="alert"` contains the details text, `window.location.href`
  unchanged, `localStorage` has no token, `onClose` not called.

**Positive path** (`closes modal and stores token when /login/complete returns 200`):
- Adapter stubs both passkey routes with success responses
- Real `AuthContext` stores the token and calls `onClose`
- Asserts: `onClose` called once, `localStorage['auth-token']` set to the
  stubbed token, no `role="alert"` present.

### Pre-existing baseline

7 test files were already failing on `3a568f640` before this change (verified
by stash-reverting and re-running the suite).  My change introduces no new
failures: 3 files / 7 tests pass in the targeted run; full suite remains at
7 failed / 191 passed.

### Build / test / lint / conflict scan

- `npm run build`: ✅ passed
- `npm run test:run` (targeted — 3 files): ✅ 7/7 passed
- `npm run test:run` (full suite): ✅ same 7 pre-existing failures, 0 new
- `npm run lint`: pre-existing `LoginAuditPage` unused-var error in `App.tsx`,
  unrelated to this change
- Anchored conflict marker scan (`^(<<<<<<<|=======|>>>>>>>)`): ✅ empty
