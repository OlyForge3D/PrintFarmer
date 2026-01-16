import axios from 'axios';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';

const api = axios.create({
  baseURL: getApiBaseUrl(),
  timeout: 30000,
  headers: { 'Content-Type': 'application/json' },
});

// Add auth token interceptor
api.interceptors.request.use((config) => {
  const token = localStorage.getItem('auth-token');
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

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
  const response = await api.get(`/users/${userId}/apikeys`);
  return response.data;
}

export async function createApiKey(userId: string, request: CreateApiKeyRequest): Promise<CreateApiKeyResponse> {
  const response = await api.post(`/users/${userId}/apikeys`, request);
  return response.data;
}

export async function toggleApiKey(userId: string, keyId: string): Promise<ToggleApiKeyResponse> {
  const response = await api.patch(`/users/${userId}/apikeys/${keyId}/toggle`);
  return response.data;
}

export async function deleteApiKey(userId: string, keyId: string): Promise<void> {
  await api.delete(`/users/${userId}/apikeys/${keyId}`);
}

export async function rotateApiKey(userId: string, keyId: string): Promise<CreateApiKeyResponse> {
  const response = await api.post(`/users/${userId}/apikeys/${keyId}/rotate`);
  return response.data;
}
