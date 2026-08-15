/**
 * Name of the `window` CustomEvent dispatched when a user session is established
 * (password login, passkey login, or an approved registration).
 *
 * Long-lived singletons that load authenticated data at module-import time — i.e.
 * before any auth token exists — listen for this event so they can re-load once a
 * credential is available. Without it, an initial fetch that fails closed (401)
 * against an authenticated endpoint would leave the singleton on fallback defaults
 * for the whole session, since the initial load never runs again.
 *
 * Keep this in sync with the literal asserted in the SignalR service tests.
 */
export const AUTH_SESSION_ESTABLISHED_EVENT = 'printfarmer:auth-session-established';

/**
 * Whether a stored auth token exists right now.
 *
 * Long-lived singletons that load authenticated data at module-import time (e.g. the
 * SignalR services) must not fire that request when no session exists yet — an
 * anonymous request against an authenticated endpoint fails closed (401) and is pure
 * console/network noise on signed-out pages such as `/login`. Callers should gate
 * their initial load on this check and rely on {@link AUTH_SESSION_ESTABLISHED_EVENT}
 * to trigger the real load once a session is established.
 */
export const hasStoredAuthToken = (): boolean =>
  Boolean(localStorage.getItem('auth-token'));
