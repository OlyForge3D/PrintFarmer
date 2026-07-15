/**
 * Monotonically increasing counter bumped on every authenticated-identity
 * transition (logout, or a successful login/passkey-login/registration).
 *
 * `clearSensitiveUserQueries` (see sensitiveQueryCache.ts) cancels and
 * removes in-flight *queries*, but React Query has no equivalent way to
 * cancel an in-flight *mutation*. A dirty-form save started by identity A
 * can still resolve after A has logged out (or after B has logged in), and
 * its `onSuccess` handler may try to write A's response straight into a
 * shared cache key (e.g. `useUpdateUserSettings` calling `setQueryData`).
 *
 * Sensitive mutations that write network responses directly into the query
 * cache must capture `getAuthEpoch()` when the mutation starts (via
 * `onMutate`) and compare it against the current epoch before writing in
 * `onSuccess`. If the epoch changed while the request was in flight, the
 * identity changed mid-save and the stale response must be discarded — see
 * #762.
 */
let epoch = 0;

export function getAuthEpoch(): number {
  return epoch;
}

export function bumpAuthEpoch(): number {
  epoch += 1;
  return epoch;
}
