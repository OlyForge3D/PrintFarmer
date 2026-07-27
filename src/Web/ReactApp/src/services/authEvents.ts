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
