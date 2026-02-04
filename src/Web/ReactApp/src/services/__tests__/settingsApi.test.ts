import { describe, it, expect, vi, beforeEach } from 'vitest';
import {
  fetchSettingsMetadata,
  fetchSettingsGroups,
  fetchSettingsUnified,
  saveAllSettings,
  fetchSettingsValues,
  saveSettingsValues,
} from '../settingsApi';
import { apiClient } from '../api';

vi.mock('../api', () => ({
  apiClient: {
    getSettingsMetadata: vi.fn(),
    getSettingsGroups: vi.fn(),
    getAllSettings: vi.fn(),
    saveAllSettings: vi.fn(),
    getSettings: vi.fn(),
    saveSettings: vi.fn(),
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

      vi.mocked(apiClient.getSettingsMetadata).mockResolvedValue(mockMetadata);

      const result = await fetchSettingsMetadata();

      expect(apiClient.getSettingsMetadata).toHaveBeenCalled();
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

      vi.mocked(apiClient.getSettingsGroups).mockResolvedValue(mockGroups);

      const result = await fetchSettingsGroups();

      expect(apiClient.getSettingsGroups).toHaveBeenCalled();
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

      vi.mocked(apiClient.getAllSettings).mockResolvedValue(mockSettings);

      const result = await fetchSettingsUnified();

      expect(apiClient.getAllSettings).toHaveBeenCalled();
      expect(result).toEqual(mockSettings);
    });
  });

  describe('saveAllSettings', () => {
    it('should save all settings', async () => {
      const settings = {
        theme: 'light',
        language: 'es',
        autoRefresh: false,
      };

      vi.mocked(apiClient.saveAllSettings).mockResolvedValue(undefined);

      await saveAllSettings(settings);

      expect(apiClient.saveAllSettings).toHaveBeenCalledWith(settings);
    });
  });

  describe('fetchSettingsValues', () => {
    it('should fetch settings values by key', async () => {
      const keyName = 'general';
      const mockValues = {
        theme: 'dark',
        language: 'en',
      };

      vi.mocked(apiClient.getSettings).mockResolvedValue(mockValues);

      const result = await fetchSettingsValues(keyName);

      expect(apiClient.getSettings).toHaveBeenCalledWith(keyName);
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

      vi.mocked(apiClient.saveSettings).mockResolvedValue(undefined);

      await saveSettingsValues(keyName, values);

      expect(apiClient.saveSettings).toHaveBeenCalledWith(keyName, values);
    });
  });
});
