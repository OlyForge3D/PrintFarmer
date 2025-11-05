import axios from 'axios';
import type { SettingMetadata } from '../components/SettingsPagelet';

// Get API base URL with proper environment handling
const getApiBaseUrl = (): string => {
  const rawBase = import.meta.env.VITE_API_BASE_URL as string | undefined;
  if (!rawBase || rawBase.trim() === '') return '/api';
  const trimmed = rawBase.replace(/\/$/, '');
  if (/\/(api)(\/|$)/.test(trimmed)) return trimmed;
  return `${trimmed}/api`;
};

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

export async function fetchSettingsMetadata(): Promise<SettingMetadata[]> {
  const resp = await api.get('/settings/metadata');
  return resp.data;
}

export async function fetchSettingsUnified(): Promise<Record<string, unknown>> {
  const resp = await api.get('/settings');
  return resp.data;
}

export async function saveAllSettings(values: Record<string, unknown>): Promise<void> {
  await api.post('/settings', values);
}

export async function fetchSettingsValues(keyName: string): Promise<Record<string, unknown>> {
  const resp = await api.get(`/settings/${encodeURIComponent(keyName)}`);
  return resp.data;
}

export async function saveSettingsValues(keyName: string, values: Record<string, unknown>): Promise<void> {
  await api.post(`/settings/${encodeURIComponent(keyName)}`, values);
}
