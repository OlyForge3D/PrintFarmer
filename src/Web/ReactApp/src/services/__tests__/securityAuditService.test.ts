import { describe, it, expect, vi, beforeEach } from 'vitest';
import { fetchLoginAudit } from '../securityAuditService';
import { apiClient } from '../api';

vi.mock('../api', () => ({
  apiClient: {
    get: vi.fn(),
  },
}));

const mockResponse = {
  items: [
    {
      id: 'entry-1',
      timestamp: '2026-05-26T17:20:00Z',
      username: 'admin',
      success: true,
      ipAddress: '10.0.0.42',
      userAgent: 'Mozilla/5.0',
      failureReason: null,
    },
    {
      id: 'entry-2',
      timestamp: '2026-05-26T17:21:00Z',
      username: 'badactor',
      success: false,
      ipAddress: '192.168.1.5',
      userAgent: 'curl/7.88.0',
      failureReason: 'Invalid credentials',
    },
  ],
  totalCount: 2,
  page: 1,
  pageSize: 50,
};

describe('securityAuditService', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('fetchLoginAudit', () => {
    it('fetches login audit with default params when no filters passed', async () => {
      vi.mocked(apiClient.get).mockResolvedValue({ data: mockResponse });

      const result = await fetchLoginAudit();

      expect(apiClient.get).toHaveBeenCalledWith('/admin/security/login-audit', {
        params: expect.objectContaining({ page: 1, pageSize: 50 }),
      });
      expect(result).toEqual(mockResponse);
    });

    it('sends username filter when provided', async () => {
      vi.mocked(apiClient.get).mockResolvedValue({ data: mockResponse });

      await fetchLoginAudit({ username: 'admin' });

      expect(apiClient.get).toHaveBeenCalledWith('/admin/security/login-audit', {
        params: expect.objectContaining({ username: 'admin' }),
      });
    });

    it('sends success=true filter when provided', async () => {
      vi.mocked(apiClient.get).mockResolvedValue({ data: mockResponse });

      await fetchLoginAudit({ success: true });

      expect(apiClient.get).toHaveBeenCalledWith('/admin/security/login-audit', {
        params: expect.objectContaining({ success: true }),
      });
    });

    it('sends success=false filter for failure-only queries', async () => {
      vi.mocked(apiClient.get).mockResolvedValue({ data: mockResponse });

      await fetchLoginAudit({ success: false });

      expect(apiClient.get).toHaveBeenCalledWith('/admin/security/login-audit', {
        params: expect.objectContaining({ success: false }),
      });
    });

    it('omits success param when not provided', async () => {
      vi.mocked(apiClient.get).mockResolvedValue({ data: mockResponse });

      await fetchLoginAudit({});

      const callArgs = vi.mocked(apiClient.get).mock.calls[0][1] as { params: Record<string, unknown> };
      expect(callArgs.params).not.toHaveProperty('success');
    });

    it('sends page and pageSize when specified', async () => {
      vi.mocked(apiClient.get).mockResolvedValue({ data: mockResponse });

      await fetchLoginAudit({ page: 3, pageSize: 100 });

      expect(apiClient.get).toHaveBeenCalledWith('/admin/security/login-audit', {
        params: expect.objectContaining({ page: 3, pageSize: 100 }),
      });
    });

    it('converts valid datetime-local from value to ISO 8601', async () => {
      vi.mocked(apiClient.get).mockResolvedValue({ data: mockResponse });

      await fetchLoginAudit({ from: '2026-05-26T10:00' });

      const callArgs = vi.mocked(apiClient.get).mock.calls[0][1] as { params: Record<string, unknown> };
      expect(typeof callArgs.params.from).toBe('string');
      // Should be a valid ISO string
      expect(() => new Date(callArgs.params.from as string).toISOString()).not.toThrow();
    });

    it('returns the response data from the API', async () => {
      vi.mocked(apiClient.get).mockResolvedValue({ data: mockResponse });

      const result = await fetchLoginAudit();

      expect(result.items).toHaveLength(2);
      expect(result.totalCount).toBe(2);
      expect(result.items[0].username).toBe('admin');
      expect(result.items[1].success).toBe(false);
    });
  });
});
