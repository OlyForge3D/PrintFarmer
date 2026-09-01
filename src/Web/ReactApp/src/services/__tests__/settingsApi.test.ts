import { describe, it, expect, vi, beforeEach } from 'vitest';
import {
  fetchSettingsMetadata,
  fetchSettingsGroups,
  fetchSettingsUnified,
  fetchSettingsValues,
  saveSettingsValues,
} from '../settingsApi';
import { client } from '../api/httpClient';

vi.mock('../api/httpClient', () => ({
  client: {
    get: vi.fn(),
    post: vi.fn(),
  },
}));

describe('settingsApi', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('fetchSettingsMetadata', () => {
    it('should fetch settings metadata', async () => {
      const mockMetadata = [
        {
          key: 'general',
          displayName: 'General Settings',
          description: 'General application settings',
          fields: [],
        },
      ];

      vi.mocked(client.get).mockResolvedValue({ data: mockMetadata });

      const result = await fetchSettingsMetadata();

      expect(client.get).toHaveBeenCalledWith('/settings/metadata');
      expect(result).toEqual(mockMetadata);
    });
  });

  describe('fetchSettingsGroups', () => {
    it('should fetch settings groups', async () => {
      const mockGroups = [
        {
          key: 'general',
          displayName: 'General',
          description: 'General settings',
          order: 1,
        },
        {
          key: 'printer',
          displayName: 'Printers',
          description: 'Printer settings',
          icon: 'printer',
          order: 2,
        },
      ];

      vi.mocked(client.get).mockResolvedValue({ data: mockGroups });

      const result = await fetchSettingsGroups();

      expect(client.get).toHaveBeenCalledWith('/settings/groups');
      expect(result).toEqual(mockGroups);
      expect(result).toHaveLength(2);
    });
  });

  describe('fetchSettingsUnified', () => {
    it('should fetch all settings unified', async () => {
      const mockSettings = {
        theme: 'dark',
        language: 'en',
        autoRefresh: true,
        refreshInterval: 5000,
      };

      vi.mocked(client.get).mockResolvedValue({ data: mockSettings });

      const result = await fetchSettingsUnified();

      expect(client.get).toHaveBeenCalledWith('/settings');
      expect(result).toEqual(mockSettings);
    });
  });

  describe('fetchSettingsValues', () => {
    it('should fetch settings values by key', async () => {
      const keyName = 'general';
      const mockValues = {
        theme: 'dark',
        language: 'en',
      };

      vi.mocked(client.get).mockResolvedValue({ data: mockValues });

      const result = await fetchSettingsValues(keyName);

      expect(client.get).toHaveBeenCalledWith(`/settings/${keyName}`);
      expect(result).toEqual(mockValues);
    });
  });

  describe('saveSettingsValues', () => {
    it('should save settings values by key', async () => {
      const keyName = 'general';
      const values = {
        theme: 'light',
        autoSave: true,
      };

      vi.mocked(client.post).mockResolvedValue({ data: undefined });

      await saveSettingsValues(keyName, values);

      expect(client.post).toHaveBeenCalledWith(`/settings/${keyName}`, values);
    });
  });
});
