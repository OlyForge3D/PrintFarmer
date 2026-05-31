import { startAuthentication, startRegistration } from '@simplewebauthn/browser';
import { apiClient } from '@/services/api';
import type {
  PasskeyLoginStartResponse,
  PasskeyLoginCompleteResponse,
  PasskeyRegisterStartResponse,
  PasskeyRegisterCompleteResponse,
} from '@/features/auth/types/passkey';
import { classifyPasskeyError } from '@/features/auth/types/passkey';
import type { PasskeyError } from '@/features/auth/types/passkey';

/**
 * Checks if WebAuthn is supported in the current browser context.
 */
export function isPasskeySupported(): boolean {
  return (
    typeof window !== 'undefined' &&
    window.isSecureContext &&
    typeof window.PublicKeyCredential !== 'undefined'
  );
}

/**
 * Performs passkey (WebAuthn) login flow:
 * 1. Calls /api/auth/passkey/login/start to get assertion options
 * 2. Invokes browser WebAuthn ceremony via navigator.credentials.get()
 * 3. Posts the assertion result to /api/auth/passkey/login/complete
 * 4. Returns JWT + user on success
 */
export async function passkeyLogin(
  usernameHint?: string,
): Promise<{ success: true; token: string; user: PasskeyLoginCompleteResponse['user'] } | { success: false; error: PasskeyError }> {
  try {
    const startResponse = await apiClient.post<PasskeyLoginStartResponse>(
      '/auth/passkey/login/start',
      usernameHint ? { username: usernameHint } : {},
    );
    const options = startResponse.data;

    const assertion = await startAuthentication({
      optionsJSON: options as Parameters<typeof startAuthentication>[0]['optionsJSON'],
    });

    const completeResponse = await apiClient.post<PasskeyLoginCompleteResponse>(
      '/auth/passkey/login/complete',
      { username: usernameHint ?? '', assertionResponse: assertion },
    );

    const result = completeResponse.data;
    if (result.success && result.token) {
      return { success: true, token: result.token, user: result.user };
    }
    return { success: false, error: 'unknown' };
  } catch (err: unknown) {
    const classified = classifyPasskeyError(err);
    if (classified !== 'unknown') {
      return { success: false, error: classified };
    }
    if (isAxiosError(err) && err.response) {
      return { success: false, error: 'network' };
    }
    return { success: false, error: 'unknown' };
  }
}

/**
 * Performs passkey registration flow:
 * 1. Calls /api/auth/passkey/register/start
 * 2. Invokes browser WebAuthn ceremony via navigator.credentials.create()
 * 3. Posts the attestation result to /api/auth/passkey/register/complete
 */
export async function passkeyRegister(
  deviceName?: string,
): Promise<{ success: true; credentialId: string } | { success: false; error: PasskeyError }> {
  try {
    const startResponse = await apiClient.post<PasskeyRegisterStartResponse>(
      '/auth/passkey/register/start',
      {},
    );
    const options = startResponse.data;

    const attestation = await startRegistration({
      optionsJSON: options as Parameters<typeof startRegistration>[0]['optionsJSON'],
    });

    const completeResponse = await apiClient.post<PasskeyRegisterCompleteResponse>(
      '/auth/passkey/register/complete',
      { ...attestation, deviceName },
    );

    const result = completeResponse.data;
    if (result.success && result.credentialId) {
      return { success: true, credentialId: result.credentialId };
    }
    return { success: false, error: 'unknown' };
  } catch (err: unknown) {
    const classified = classifyPasskeyError(err);
    if (classified !== 'unknown') {
      return { success: false, error: classified };
    }
    if (isAxiosError(err) && err.response) {
      return { success: false, error: 'network' };
    }
    return { success: false, error: 'unknown' };
  }
}

function isAxiosError(err: unknown): err is { response?: { status: number } } {
  return typeof err === 'object' && err !== null && 'response' in err;
}
