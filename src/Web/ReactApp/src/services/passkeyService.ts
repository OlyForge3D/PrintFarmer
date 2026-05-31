import { apiClient } from '@/services/api';

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
  const response = await apiClient.request<PasskeyCredentialDto[]>({
    method: 'GET',
    url: '/auth/passkey/credentials',
  });
  return response;
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
 * Performs the full WebAuthn passkey registration ceremony:
 * 1. Requests creation options from the server.
 * 2. Calls the browser WebAuthn API to create a credential.
 * 3. Sends the attestation response back to the server for verification.
 */
export async function registerPasskey(): Promise<{ credentialId: string }> {
  const options = await apiClient.request<PublicKeyCredentialCreationOptions>({
    method: 'POST',
    url: '/auth/passkey/register/begin',
  });

  // The server returns base64url-encoded buffers that need to be decoded for the browser API.
  const publicKey = prepareCreationOptions(options);

  const credential = (await navigator.credentials.create({
    publicKey,
  })) as PublicKeyCredential | null;

  if (!credential) {
    throw new Error('Passkey registration was cancelled.');
  }

  const attestationResponse = credential.response as AuthenticatorAttestationResponse;

  const result = await apiClient.request<{ credentialId: string }>({
    method: 'POST',
    url: '/auth/passkey/register/complete',
    data: {
      id: credential.id,
      rawId: bufferToBase64Url(credential.rawId),
      type: credential.type,
      response: {
        attestationObject: bufferToBase64Url(attestationResponse.attestationObject),
        clientDataJSON: bufferToBase64Url(attestationResponse.clientDataJSON),
      },
    },
  });

  return result;
}

function prepareCreationOptions(
  options: PublicKeyCredentialCreationOptions,
): PublicKeyCredentialCreationOptions {
  return {
    ...options,
    challenge: base64UrlToBuffer(options.challenge as unknown as string),
    user: {
      ...options.user,
      id: base64UrlToBuffer(options.user.id as unknown as string),
    },
    excludeCredentials: options.excludeCredentials?.map((cred) => ({
      ...cred,
      id: base64UrlToBuffer(cred.id as unknown as string),
    })),
  };
}

function base64UrlToBuffer(base64url: string): ArrayBuffer {
  const base64 = base64url.replace(/-/g, '+').replace(/_/g, '/');
  const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '=');
  const binary = atob(padded);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes.buffer;
}

function bufferToBase64Url(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let binary = '';
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
