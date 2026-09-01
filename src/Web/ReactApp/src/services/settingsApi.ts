import { client } from '@/services/api/httpClient';
import type { SettingMetadata } from '@/common/components/SettingsPagelet';

/**
 * Settings API — calls the shared axios client directly (see
 * `services/api/httpClient.ts`) rather than delegating to the `ApiClient`
 * monolith, so this module (statically imported by the first-run setup
 * wizard) stays out of that monolith's eager import graph. See issue #2343.
 */

/** Group metadata for sidebar organization */
export interface SettingGroupMetadata {
  key: string;
  displayName: string;
  description?: string;
  icon?: string;
  order: number;
}

export async function fetchSettingsMetadata(): Promise<SettingMetadata[]> {
  const res = await client.get('/settings/metadata');
  return res.data;
}

export async function fetchSettingsGroups(): Promise<SettingGroupMetadata[]> {
  const res = await client.get('/settings/groups');
  return res.data;
}

export async function fetchSettingsUnified(): Promise<Record<string, unknown>> {
  const res = await client.get('/settings');
  return res.data;
}

export async function fetchSettingsValues<T = Record<string, unknown>>(keyName: string): Promise<T> {
  const res = await client.get(`/settings/${keyName}`);
  return res.data;
}

export async function saveSettingsValues<T = Record<string, unknown>>(keyName: string, values: T): Promise<void> {
  await client.post(`/settings/${keyName}`, values);
}
