const authenticationExpiredEvent = 'printfarmer:authentication-expired';

/** Notify the active document that its authenticated session is no longer valid. */
export function notifyAuthenticationExpired(): void {
  window.dispatchEvent(new Event(authenticationExpiredEvent));
}

/** Subscribe React authentication state to same-document session expiration. */
export function subscribeToAuthenticationExpiration(listener: () => void): () => void {
  window.addEventListener(authenticationExpiredEvent, listener);
  return () => window.removeEventListener(authenticationExpiredEvent, listener);
}
