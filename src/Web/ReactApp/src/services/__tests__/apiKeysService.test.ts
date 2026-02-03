import { describe, it, expect, vi, beforeEach } from 'vitest';
import {
  listApiKeys,
  createApiKey,
  toggleApiKey,
  deleteApiKey,
  rotateApiKey,
  revealApiKey,
  getApiKeySettings,
  CreateApiKeyRequest,
} from '../apiKeysService';
import { apiClient } from '../api';

vi.mock('../api', () => ({
  apiClient: {
    listUserApiKeys: vi.fn(),
    createUserApiKey: vi.fn(),
    toggleUserApiKey: vi.fn(),
    deleteUserApiKey: vi.fn(),
    rotateUserApiKey: vi.fn(),
    revealUserApiKey: vi.fn(),
    getApiKeySettings: vi.fn(),
  },
}));

describe('apiKeysService', () => {
  const userId = 'user-123';

  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('listApiKeys', () => {
    it('should list all API keys for a user', async () => {
      const mockKeys = [
        {
          id: 'key-1',
          name: 'Production Key',
          isActive: true,
          createdAt: '2024-01-01T00:00:00Z',
          expiresAt: '2025-01-01T00:00:00Z',
        },
        {
          id: 'key-2',
          name: 'Development Key',
          isActive: false,
          createdAt: '2024-02-01T00:00:00Z',
        },
      ];

      vi.mocked(apiClient.listUserApiKeys).mockResolvedValue(mockKeys);

      const result = await listApiKeys(userId);

      expect(apiClient.listUserApiKeys).toHaveBeenCalledWith(userId);
      expect(result).toEqual(mockKeys);
      expect(result).toHaveLength(2);
    });

    it('should return empty array when no keys exist', async () => {
      vi.mocked(apiClient.listUserApiKeys).mockResolvedValue([]);

      const result = await listApiKeys(userId);

      expect(result).toEqual([]);
    });
  });

  describe('createApiKey', () => {
    it('should create a new API key', async () => {
      const request: CreateApiKeyRequest = {
        name: 'New API Key',
      };
      const mockResponse = {
        key: 'pk_test_1234567890abcdef',
        id: 'key-new',
      };

      vi.mocked(apiClient.createUserApiKey).mockResolvedValue(mockResponse);

      const result = await createApiKey(userId, request);

      expect(apiClient.createUserApiKey).toHaveBeenCalledWith(userId, request);
      expect(result).toEqual(mockResponse);
      expect(result.key).toBeDefined();
      expect(result.id).toBeDefined();
    });
  });

  describe('toggleApiKey', () => {
    it('should toggle API key to inactive', async () => {
      const keyId = 'key-toggle';
      const mockResponse = {
        id: keyId,
        isActive: false,
      };

      vi.mocked(apiClient.toggleUserApiKey).mockResolvedValue(mockResponse);

      const result = await toggleApiKey(userId, keyId);

      expect(apiClient.toggleUserApiKey).toHaveBeenCalledWith(userId, keyId);
      expect(result.isActive).toBe(false);
    });

    it('should toggle API key to active', async () => {
      const keyId = 'key-toggle-on';
      const mockResponse = {
        id: keyId,
        isActive: true,
      };

      vi.mocked(apiClient.toggleUserApiKey).mockResolvedValue(mockResponse);

      const result = await toggleApiKey(userId, keyId);

      expect(result.isActive).toBe(true);
    });
  });

  describe('deleteApiKey', () => {
    it('should delete an API key', async () => {
      const keyId = 'key-delete';

      vi.mocked(apiClient.deleteUserApiKey).mockResolvedValue(undefined);

      await deleteApiKey(userId, keyId);

      expect(apiClient.deleteUserApiKey).toHaveBeenCalledWith(userId, keyId);
    });
  });

  describe('rotateApiKey', () => {
    it('should rotate an API key and return new key', async () => {
      const keyId = 'key-rotate';
      const mockResponse = {
        key: 'pk_test_new_rotated_key',
        id: keyId,
      };

      vi.mocked(apiClient.rotateUserApiKey).mockResolvedValue(mockResponse);

      const result = await rotateApiKey(userId, keyId);

      expect(apiClient.rotateUserApiKey).toHaveBeenCalledWith(userId, keyId);
      expect(result).toEqual(mockResponse);
      expect(result.key).toContain('pk_test_');
    });
  });

  describe('revealApiKey', () => {
    it('should reveal the full API key', async () => {
      const keyId = 'key-reveal';
      const mockResponse = {
        key: 'pk_test_revealed_full_key',
      };

      vi.mocked(apiClient.revealUserApiKey).mockResolvedValue(mockResponse);

      const result = await revealApiKey(userId, keyId);

      expect(apiClient.revealUserApiKey).toHaveBeenCalledWith(userId, keyId);
      expect(result.key).toBeDefined();
    });
  });

  describe('getApiKeySettings', () => {
    it('should get API key settings with hashing enabled', async () => {
      const mockSettings = {
        hashingEnabled: true,
      };

      vi.mocked(apiClient.getApiKeySettings).mockResolvedValue(mockSettings);

      const result = await getApiKeySettings();

      expect(apiClient.getApiKeySettings).toHaveBeenCalled();
      expect(result.hashingEnabled).toBe(true);
    });

    it('should get API key settings with hashing disabled', async () => {
      const mockSettings = {
        hashingEnabled: false,
      };

      vi.mocked(apiClient.getApiKeySettings).mockResolvedValue(mockSettings);

      const result = await getApiKeySettings();

      expect(result.hashingEnabled).toBe(false);
    });
  });
});
