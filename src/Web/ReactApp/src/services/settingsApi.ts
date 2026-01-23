import { apiClient } from '@/services/api';
import type { SettingMetadata } from '@/components/SettingsPagelet';

/**
 * Settings API - delegated to apiClient singleton
 * apiClient handles authentication, correlation IDs, and error handling automatically
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
  return apiClient.getSettingsMetadata();
}

export async function fetchSettingsGroups(): Promise<SettingGroupMetadata[]> {
  return apiClient.getSettingsGroups();
}

export async function fetchSettingsUnified(): Promise<Record<string, unknown>> {
  return apiClient.getAllSettings();
}

export async function saveAllSettings(values: Record<string, unknown>): Promise<void> {
  return apiClient.saveAllSettings(values);
}

export async function fetchSettingsValues(keyName: string): Promise<Record<string, unknown>> {
  return apiClient.getSettings(keyName);
}

export async function saveSettingsValues(keyName: string, values: Record<string, unknown>): Promise<void> {
  return apiClient.saveSettings(keyName, values);
}
