import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { getApiBaseUrl, getAuthHeaders, getHubUrl } from '../apiUrlHelpers';

describe('apiUrlHelpers', () => {
  // Save original values
  const hadOriginalApiBaseUrl = Object.prototype.hasOwnProperty.call(import.meta.env, 'VITE_API_BASE_URL');
  const originalApiBaseUrl = import.meta.env.VITE_API_BASE_URL;
  const originalLocalStorage = global.localStorage;

  beforeEach(() => {
    // Reset localStorage
    const localStorageMock = {
      getItem: vi.fn(),
      setItem: vi.fn(),
      removeItem: vi.fn(),
      clear: vi.fn(),
      length: 0,
      key: vi.fn(),
    };
    Object.defineProperty(global, 'localStorage', {
      value: localStorageMock,
      writable: true,
    });
  });

  afterEach(() => {
    if (hadOriginalApiBaseUrl) {
      import.meta.env.VITE_API_BASE_URL = originalApiBaseUrl;
    } else {
      delete import.meta.env.VITE_API_BASE_URL;
    }

    // Restore original localStorage
    Object.defineProperty(global, 'localStorage', {
      value: originalLocalStorage,
      writable: true,
    });
    vi.clearAllMocks();
  });

  describe('getApiBaseUrl', () => {
    it('should return /api when no env variable is set', () => {
      import.meta.env.VITE_API_BASE_URL = '';
      expect(getApiBaseUrl()).toBe('/api');
    });

    it('should return /api when env variable is undefined', () => {
      delete import.meta.env.VITE_API_BASE_URL;
      expect(getApiBaseUrl()).toBe('/api');
    });

    it('should append /api to base URL without /api', () => {
      import.meta.env.VITE_API_BASE_URL = 'http://localhost:5245';
      expect(getApiBaseUrl()).toBe('http://localhost:5245/api');
    });

    it('should not duplicate /api if already present', () => {
      import.meta.env.VITE_API_BASE_URL = 'http://localhost:5245/api';
      expect(getApiBaseUrl()).toBe('http://localhost:5245/api');
    });

    it('should remove trailing slash before appending /api', () => {
      import.meta.env.VITE_API_BASE_URL = 'http://localhost:5245/';
      expect(getApiBaseUrl()).toBe('http://localhost:5245/api');
    });

    it('should handle base URL with /api/ (with trailing slash)', () => {
      import.meta.env.VITE_API_BASE_URL = 'http://localhost:5245/api/';
      expect(getApiBaseUrl()).toBe('http://localhost:5245/api');
    });

    it('should handle whitespace-only env variable', () => {
      import.meta.env.VITE_API_BASE_URL = '   ';
      expect(getApiBaseUrl()).toBe('/api');
    });
  });

  describe('getAuthHeaders', () => {
    it('should return empty object when no token is stored', () => {
      vi.mocked(localStorage.getItem).mockReturnValue(null);

      const headers = getAuthHeaders();

      expect(headers).toEqual({});
      expect(localStorage.getItem).toHaveBeenCalledWith('auth-token');
    });

    it('should return Authorization header with Bearer token', () => {
      const mockToken = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.test';
      vi.mocked(localStorage.getItem).mockReturnValue(mockToken);

      const headers = getAuthHeaders();

      expect(headers).toEqual({
        Authorization: `Bearer ${mockToken}`,
      });
      expect(localStorage.getItem).toHaveBeenCalledWith('auth-token');
    });

    it('should handle empty string token', () => {
      vi.mocked(localStorage.getItem).mockReturnValue('');

      const headers = getAuthHeaders();

      expect(headers).toEqual({});
    });
  });

  describe('getHubUrl', () => {
    it('should return hubPath as-is when no env variable is set', () => {
      import.meta.env.VITE_API_BASE_URL = '';

      const hubUrl = getHubUrl('/hubs/printers');

      expect(hubUrl).toBe('/hubs/printers');
    });

    it('should return hubPath as-is when env variable is undefined', () => {
      import.meta.env.VITE_API_BASE_URL = undefined;

      const hubUrl = getHubUrl('/hubs/printers');

      expect(hubUrl).toBe('/hubs/printers');
    });

    it('should prepend full URL to hubPath', () => {
      import.meta.env.VITE_API_BASE_URL = 'http://localhost:5245';

      const hubUrl = getHubUrl('/hubs/printers');

      expect(hubUrl).toBe('http://localhost:5245/hubs/printers');
    });

    it('should handle HTTPS URLs', () => {
      import.meta.env.VITE_API_BASE_URL = 'https://api.example.com';

      const hubUrl = getHubUrl('/hubs/slicer');

      expect(hubUrl).toBe('https://api.example.com/hubs/slicer');
    });

    it('should remove trailing slash from base URL', () => {
      import.meta.env.VITE_API_BASE_URL = 'http://localhost:5245/';

      const hubUrl = getHubUrl('/hubs/printers');

      expect(hubUrl).toBe('http://localhost:5245/hubs/printers');
    });

    it('should handle relative path env variable', () => {
      import.meta.env.VITE_API_BASE_URL = '/some/path';

      const hubUrl = getHubUrl('/hubs/printers');

      expect(hubUrl).toBe('/hubs/printers');
    });

    it('should handle whitespace-only env variable', () => {
      import.meta.env.VITE_API_BASE_URL = '   ';

      const hubUrl = getHubUrl('/hubs/printers');

      expect(hubUrl).toBe('/hubs/printers');
    });

    it('should handle different hub paths', () => {
      import.meta.env.VITE_API_BASE_URL = 'http://localhost:5245';

      expect(getHubUrl('/hubs/printers')).toBe('http://localhost:5245/hubs/printers');
      expect(getHubUrl('/hubs/slicer')).toBe('http://localhost:5245/hubs/slicer');
      expect(getHubUrl('/hubs/maintenance')).toBe('http://localhost:5245/hubs/maintenance');
    });
  });
});
