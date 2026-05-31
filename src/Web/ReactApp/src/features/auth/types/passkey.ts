/**
 * TypeScript types for WebAuthn/passkey API interactions.
 * Matches the camelCase JSON contract from Fido2NetLib backend endpoints.
 */

export interface PasskeyLoginStartResponse {
  challenge: string;
  timeout?: number;
  rpId?: string;
  allowCredentials?: PublicKeyCredentialDescriptorJson[];
  userVerification?: string;
  extensions?: Record<string, unknown>;
}

export interface PublicKeyCredentialDescriptorJson {
  type: string;
  id: string;
  transports?: string[];
}

export interface PasskeyLoginCompleteRequest {
  id: string;
  rawId: string;
  type: string;
  response: {
    authenticatorData: string;
    clientDataJSON: string;
    signature: string;
    userHandle?: string;
  };
  authenticatorAttachment?: string;
}

export interface PasskeyLoginCompleteResponse {
  success: boolean;
  token?: string;
  user?: {
    id: string;
    username: string;
    email: string;
    firstName?: string;
    lastName?: string;
    isActive: boolean;
    emailConfirmed: boolean;
    lastLogin?: string;
    createdAt: string;
    roles: string[];
    permissions: string[];
  };
  error?: string;
}

export interface PasskeyRegisterStartResponse {
  rp: { name: string; id: string };
  user: { id: string; name: string; displayName: string };
  challenge: string;
  pubKeyCredParams: Array<{ type: string; alg: number }>;
  timeout?: number;
  excludeCredentials?: PublicKeyCredentialDescriptorJson[];
  authenticatorSelection?: {
    authenticatorAttachment?: string;
    residentKey?: string;
    requireResidentKey?: boolean;
    userVerification?: string;
  };
  attestation?: string;
  extensions?: Record<string, unknown>;
}

export interface PasskeyRegisterCompleteRequest {
  id: string;
  rawId: string;
  type: string;
  response: {
    attestationObject: string;
    clientDataJSON: string;
    transports?: string[];
  };
  authenticatorAttachment?: string;
  deviceName?: string;
}

export interface PasskeyRegisterCompleteResponse {
  success: boolean;
  credentialId?: string;
  error?: string;
}

export type PasskeyError =
  | 'not-supported'
  | 'not-allowed'
  | 'cancelled'
  | 'no-credentials'
  | 'network'
  | 'unknown';

export function classifyPasskeyError(err: unknown): PasskeyError {
  if (err instanceof DOMException) {
    switch (err.name) {
      case 'NotAllowedError':
        return 'cancelled';
      case 'NotSupportedError':
        return 'not-supported';
      case 'InvalidStateError':
        return 'no-credentials';
      case 'SecurityError':
        return 'not-supported';
      default:
        return 'unknown';
    }
  }
  if (err instanceof TypeError) {
    return 'not-supported';
  }
  return 'unknown';
}

export function getPasskeyErrorMessage(error: PasskeyError): string {
  switch (error) {
    case 'not-supported':
      return 'Passkeys are not supported in this browser. Ensure you are using HTTPS.';
    case 'cancelled':
      return 'Passkey authentication was cancelled.';
    case 'not-allowed':
      return 'Passkey authentication was not allowed by the browser.';
    case 'no-credentials':
      return 'No passkey found for this account. Try signing in with your password.';
    case 'network':
      return 'Network error. Please check your connection and try again.';
    case 'unknown':
    default:
      return 'An unexpected error occurred during passkey authentication.';
  }
}
