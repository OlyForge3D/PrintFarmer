import { describe, it, expect, vi, beforeEach } from 'vitest';
import { apiClient } from '@/services/api';
import { maintenanceService } from '@/services/maintenanceService';

vi.mock('@/services/api', () => ({
  apiClient: { get: vi.fn() },
}));

const get = apiClient.get as unknown as ReturnType<typeof vi.fn>;

describe('maintenanceService.getRecentPrinterMaintenanceLogs (#3297)', () => {
  beforeEach(() => vi.clearAllMocks());

  it('sends limit and scope as query params and returns the rows', async () => {
    get.mockResolvedValue({ data: [{ id: 'l-1' }] });

    const logs = await maintenanceService.getRecentPrinterMaintenanceLogs('p-1', { limit: 10, scope: 'printer' });

    expect(get).toHaveBeenCalledWith('/maintenance/printers/p-1/logs', {
      params: { limit: 10, scope: 'printer' },
    });
    expect(logs).toEqual([{ id: 'l-1' }]);
  });

  it('omits scope when not provided', async () => {
    get.mockResolvedValue({ data: [] });

    await maintenanceService.getRecentPrinterMaintenanceLogs('p-1', { limit: 10 });

    expect(get).toHaveBeenCalledWith('/maintenance/printers/p-1/logs', { params: { limit: 10 } });
  });
});
