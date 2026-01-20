import { apiClient } from '@/services/api';

export interface ApiKeyDto {
  id: string;
  name: string;
  isActive: boolean;
  createdAt: string;
  expiresAt?: string;
}

export interface CreateApiKeyResponse {
  key: string;
  id: string;
}

export interface CreateApiKeyRequest {
  name: string;
}

export interface ToggleApiKeyResponse {
  id: string;
  isActive: boolean;
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
