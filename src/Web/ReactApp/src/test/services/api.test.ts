import { describe, it, expect, vi, beforeEach } from 'vitest';
import { ApiClient } from '@/services/api';
import { PrinterBackend } from '@/types/api';

// Mock axios
vi.mock('axios', () => ({
  default: {
    create: vi.fn(() => ({
      get: vi.fn(),
      post: vi.fn(),
      put: vi.fn(),
      delete: vi.fn(),
      patch: vi.fn(),
      request: vi.fn(),
      interceptors: {
        request: { use: vi.fn() },
        response: { use: vi.fn() },
      },
    })),
  },
}));

describe('ApiClient', () => {
  let apiClient: ApiClient;

  beforeEach(() => {
    apiClient = new ApiClient();
  });

  describe('constructor', () => {
    it('should create an instance', () => {
      expect(apiClient).toBeDefined();
      expect(apiClient).toBeInstanceOf(ApiClient);
    });
  });

  describe('getPrinters', () => {
    it('should call the correct endpoint', async () => {
      const mockResponse = {
        data: [
          {
            id: '1',
            name: 'Test Printer',
            serverUrl: 'http://test.local',
            notes: 'Test notes',
            isOnline: true,
            state: 'idle',
            backend: PrinterBackend.Moonraker,
          },
        ],
      };

      // Mock the get method
  const mockGet = vi.fn().mockResolvedValue(mockResponse);
  // access internal axios client for mocking; cast to index signature
  (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      const result = await apiClient.getPrinters();

  // Updated endpoint now uses the faster summary list endpoint
  expect(mockGet).toHaveBeenCalledWith('/printers/fast');
      expect(result).toEqual(mockResponse.data);
    });
  });

  describe('getHealthStatus', () => {
    it('should call the health endpoint', async () => {
      const mockResponse = {
        data: { status: 'ok' },
      };

  const mockGet = vi.fn().mockResolvedValue(mockResponse);
  (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      const result = await apiClient.getBasicHealth();

      expect(mockGet).toHaveBeenCalledWith('/healthz');
      expect(result).toEqual(mockResponse.data);
    });
  });

  describe('createPrinter', () => {
    it('should POST to the correct endpoint', async () => {
      const printerDto = {
        name: 'New Printer',
        serverUrl: 'http://new.local',
        backend: PrinterBackend.PrusaLink,
      };

      const mockResponse = {
        data: {
          id: '2',
          ...printerDto,
          isOnline: false,
          state: null,
        },
      };

  const mockPost = vi.fn().mockResolvedValue(mockResponse);
  (apiClient as unknown as { client: { post: typeof mockPost } }).client.post = mockPost;

      const result = await apiClient.createPrinter(printerDto);

      expect(mockPost).toHaveBeenCalledWith('/printers', printerDto);
      expect(result).toEqual(mockResponse.data);
    });
  });
});