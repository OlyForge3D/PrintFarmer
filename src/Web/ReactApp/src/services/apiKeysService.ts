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
  const response = await apiClient.get(`/users/${userId}/apikeys`);
  return response.data;
}

export async function createApiKey(userId: string, request: CreateApiKeyRequest): Promise<CreateApiKeyResponse> {
  const response = await apiClient.post(`/users/${userId}/apikeys`, request);
  return response.data;
}

export async function toggleApiKey(userId: string, keyId: string): Promise<ToggleApiKeyResponse> {
  const response = await apiClient.patch(`/users/${userId}/apikeys/${keyId}/toggle`);
  return response.data;
}

export async function deleteApiKey(userId: string, keyId: string): Promise<void> {
  await apiClient.delete(`/users/${userId}/apikeys/${keyId}`);
}

export async function rotateApiKey(userId: string, keyId: string): Promise<CreateApiKeyResponse> {
  const response = await apiClient.post(`/users/${userId}/apikeys/${keyId}/rotate`);
  return response.data;
}
