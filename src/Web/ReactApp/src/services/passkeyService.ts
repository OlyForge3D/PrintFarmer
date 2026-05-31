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
