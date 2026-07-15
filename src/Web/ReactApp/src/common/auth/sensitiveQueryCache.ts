import type { QueryClient, QueryKey } from '@tanstack/react-query';

/**
 * Query key prefixes that hold data owned by a single authenticated identity.
 *
 * These must never remain readable (or writable, via a dirty form saving a
 * stale snapshot) after the authenticated identity changes — see #762.
 *
 * This is an explicit allow-list, not a blanket cache clear, so unrelated
 * public/shared caches (printers, catalog, farm-wide settings, etc.) survive
 * a logout/login transition untouched.
 *
 * Keep this list in sync with any new user-owned sensitive query hooks:
 * - notifications: covers the notification list (['notifications']), unread
 *   count (['notifications', 'unread-count']) and preferences
 *   (['notifications', 'preferences']) — see useNotificationPreferences.ts
 *   and common/hooks/useApi.ts.
 * - settings/user: per-account settings (useUserSettings.ts). Note this is
 *   NOT the same prefix as the shared ['settings', 'farm'] cache, which must
 *   be preserved.
 * - passkeys: the signed-in user's WebAuthn credentials (PasskeysPage.tsx).
 * - apiKeys: the signed-in user's API keys (ApiKeysPage.tsx). Already scoped
 *   by userId, but cleared here too since it is plainly sensitive per-user
 *   data and there is no reason to keep a previous identity's entries around.
 */
const SENSITIVE_QUERY_KEY_PREFIXES: QueryKey[] = [
  ['notifications'],
  ['settings', 'user'],
  ['passkeys'],
  ['apiKeys'],
];

function matchesSensitivePrefix(queryKey: QueryKey): boolean {
  return SENSITIVE_QUERY_KEY_PREFIXES.some((prefix) =>
    prefix.every((part, index) => queryKey[index] === part),
  );
}

/**
 * Synchronously purge all user-owned sensitive query cache entries.
 *
 * Must be invoked on every identity transition — logout, and before a
 * successful login/passkey-login/registration hands control to the
 * authenticated UI — so the next identity can never read or save over the
 * previous identity's cached data.
 *
 * In-flight fetches for these keys are cancelled first. Without this,
 * `removeQueries` alone would not stop an in-flight response from
 * repopulating the cache with the previous identity's data immediately
 * after removal.
 */
export async function clearSensitiveUserQueries(queryClient: QueryClient): Promise<void> {
  const predicate = (query: { queryKey: QueryKey }) => matchesSensitivePrefix(query.queryKey);
  await queryClient.cancelQueries({ predicate });
  queryClient.removeQueries({ predicate });
}
