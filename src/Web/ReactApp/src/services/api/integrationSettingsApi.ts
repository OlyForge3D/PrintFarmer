import { client } from '@/services/api/httpClient';
import type { SpoolmanSettings } from '@/types/SpoolmanSettings';

export interface HomeAssistantSettings {
  enabled: boolean;
  baseUrl: string;
  tokenMasked: string;
}

export async function fetchSpoolmanSettings(): Promise<SpoolmanSettings> {
  const response = await client.get<SpoolmanSettings>('/spoolman/config');
  // An unconfigured integration returns 204, not an error.
  return response.status === 204 ? { baseUrl: '' } : response.data;
}

export async function fetchHomeAssistantSettings(): Promise<HomeAssistantSettings> {
  const response = await client.get<HomeAssistantSettings>('/admin/integrations/home-assistant/settings');
  return response.data;
}

export async function saveHomeAssistantSettings(
  settings: Pick<HomeAssistantSettings, 'enabled' | 'baseUrl'> & { token: string },
): Promise<HomeAssistantSettings> {
  const response = await client.put<HomeAssistantSettings>('/admin/integrations/home-assistant/settings', settings);
  return response.data;
}
