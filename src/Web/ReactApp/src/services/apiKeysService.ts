import { apiClient } from '@/services/api';

export type ApiKeyPurpose = 'OctoPrint' | 'Desktop';

/**
 * Canonical, individually selectable API key scope names.
 *
 * These map 1:1 to single bits of the backend `ApiKeyScope` flags enum. The legacy `scopes`
 * string field can render the exact value 7 as the single name `All`, which reads as "every
 * privilege" but actually means only the three model/library scopes — so the UI must always work
 * from these individual names via `scopeNames`, never from `scopes`.
 */
export type ApiKeyScope =
  | 'ModelRead'
  | 'ModelWrite'
  | 'LibrarySync'
  | 'CalibrationRead'
  | 'CalibrationCreate'
  | 'CalibrationUpdate'
  | 'CalibrationDelete'
  | 'CalibrationGenerate'
  | 'CalibrationPublish'
  | 'SlicingSubmit'
  | 'SlicingReadArtifact'
  | 'QueueRead'
  | 'QueueWrite'
  | 'QueueStart'
  | 'QueueCancel'
  | 'QueueAcknowledgeBedClear';

export interface ApiKeyDto {
  id: string;
  name: string;
  isActive: boolean;
  createdAt: string;
  expiresAt?: string;
  purpose: ApiKeyPurpose;
  /** Legacy flags rendering. Prefer `scopeNames`. */
  scopes: string;
  /** Canonical individual scope names. Always present on responses from a current server. */
  scopeNames?: string[];
  isExpired: boolean;
}

export interface CreateApiKeyResponse {
  key: string;
  id: string;
  purpose?: ApiKeyPurpose;
  scopes?: string;
  scopeNames?: string[];
  expiresAt?: string;
}

export interface CreateApiKeyRequest {
  name: string;
  purpose?: ApiKeyPurpose;
  /**
   * Canonical scope names. Preferred over the legacy `scopes` field; the server rejects
   * requests that set both.
   */
  scopeNames?: ApiKeyScope[];
  /**
   * @deprecated Legacy packed flags string (e.g. `"ModelRead,LibrarySync"`). Still accepted by
   * the server for existing clients, but it renders the exact value 7 as the misleading name
   * `All`. Prefer `scopeNames`; setting both is rejected with a 400.
   */
  scopes?: string;
  expiresAt?: string;
}

/**
 * Resolves an API key's individual scope names, falling back to parsing the legacy comma-separated
 * `scopes` field for responses from an older server. `All` is expanded to the three model/library
 * scopes it actually means, so it is never displayed as if it granted every privilege.
 */
export function resolveScopeNames(key: Pick<ApiKeyDto, 'scopes' | 'scopeNames'>): string[] {
  if (key.scopeNames && key.scopeNames.length > 0) {
    return key.scopeNames;
  }
  if (!key.scopes) {
    return [];
  }
  return key.scopes
    .split(',')
    .map((scope) => scope.trim())
    .filter((scope) => scope !== '' && scope !== 'None')
    .flatMap((scope) => (scope === 'All' ? ['ModelRead', 'ModelWrite', 'LibrarySync'] : [scope]));
}

export interface ToggleApiKeyResponse {
  id: string;
  isActive: boolean;
}

export interface RevealApiKeyResponse {
  key: string;
}

export interface ApiKeySettingsResponse {
  hashingEnabled: boolean;
}

export async function listApiKeys(userId: string): Promise<ApiKeyDto[]> {
  const response = await apiClient.listUserApiKeys(userId);
  return response as ApiKeyDto[];
}

export async function createApiKey(userId: string, request: CreateApiKeyRequest): Promise<CreateApiKeyResponse> {
  const response = await apiClient.createUserApiKey(userId, request);
  return response as CreateApiKeyResponse;
}

export async function toggleApiKey(userId: string, keyId: string): Promise<ToggleApiKeyResponse> {
  const response = await apiClient.toggleUserApiKey(userId, keyId);
  return response as ToggleApiKeyResponse;
}

export async function deleteApiKey(userId: string, keyId: string): Promise<void> {
  await apiClient.deleteUserApiKey(userId, keyId);
}

export async function rotateApiKey(userId: string, keyId: string): Promise<CreateApiKeyResponse> {
  const response = await apiClient.rotateUserApiKey(userId, keyId);
  return response as CreateApiKeyResponse;
}

export async function revealApiKey(userId: string, keyId: string): Promise<RevealApiKeyResponse> {
  const response = await apiClient.revealUserApiKey(userId, keyId);
  return response as RevealApiKeyResponse;
}

export async function getApiKeySettings(): Promise<ApiKeySettingsResponse> {
  const response = await apiClient.getApiKeySettings();
  return response as ApiKeySettingsResponse;
}
