import { startAuthentication, startRegistration } from '@simplewebauthn/browser';
import type {
  PublicKeyCredentialCreationOptionsJSON,
  PublicKeyCredentialRequestOptionsJSON,
} from '@simplewebauthn/browser';
import { apiClient } from '@/services/api';
import type { AuthenticationResult } from '@/types/api';

export interface PasskeyCredentialDto {
  id: number;
  deviceName: string | null;
  aaguidDescription: string | null;
  createdAt: string;
  lastUsedAt: string | null;
}

export interface RenamePasskeyRequest {
  deviceName: string;
}

export async function listPasskeys(): Promise<PasskeyCredentialDto[]> {
  return apiClient.request<PasskeyCredentialDto[]>({
    method: 'GET',
    url: '/auth/passkey/credentials',
  });
}

export async function deletePasskey(id: number): Promise<void> {
  await apiClient.request<void>({
    method: 'DELETE',
    url: `/auth/passkey/credentials/${id}`,
  });
}

export async function renamePasskey(id: number, deviceName: string): Promise<void> {
  await apiClient.request<void>({
    method: 'PATCH',
    url: `/auth/passkey/credentials/${id}`,
    data: { deviceName } satisfies RenamePasskeyRequest,
  });
}

/**
 * Performs the full WebAuthn passkey registration ceremony using @simplewebauthn/browser.
 * 1. Requests creation options from the server.
 * 2. Invokes the browser WebAuthn API via startRegistration().
 * 3. Sends the attestation response back to the server for verification.
 */
export async function registerPasskey(): Promise<{ credentialId: string }> {
  const optionsJSON = await apiClient.request<PublicKeyCredentialCreationOptionsJSON>({
    method: 'POST',
    url: '/auth/passkey/register/begin',
  });

  const attestationResponse = await startRegistration({ optionsJSON });

  return apiClient.request<{ credentialId: string }>({
    method: 'POST',
    url: '/auth/passkey/register/complete',
    data: attestationResponse,
  });
}

/**
 * Performs the full WebAuthn passkey authentication ceremony using @simplewebauthn/browser.
 * 1. Requests assertion options from the server with the given username hint.
 * 2. Invokes the browser WebAuthn API via startAuthentication().
 * 3. Sends the assertion response back to the server for verification.
 * 4. Returns the server's AuthenticationResult (success, token, user, error).
 *
 * @remarks Brady policy: username hint is required (fully discoverable credentials are out of scope).
 */
export async function loginWithPasskey(username: string): Promise<AuthenticationResult> {
  const optionsJSON = await apiClient.request<PublicKeyCredentialRequestOptionsJSON>({
    method: 'POST',
    url: '/auth/passkey/login/begin',
    data: { username },
  });

  const assertionResponse = await startAuthentication({ optionsJSON });

  return apiClient.request<AuthenticationResult>({
    method: 'POST',
    url: '/auth/passkey/login/complete',
    data: { username, assertionResponse },
  });
}
