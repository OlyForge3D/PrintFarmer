import { describe, it, expect, vi, beforeEach } from 'vitest';
import { apiClient } from '@/services/api';
import { maintenanceService } from '@/services/maintenanceService';

vi.mock('@/services/api', () => ({
  apiClient: { get: vi.fn() },
}));

const get = apiClient.get as unknown as ReturnType<typeof vi.fn>;

describe('maintenanceService.getRecentPrinterMaintenanceLogs (#3297)', () => {
  beforeEach(() => vi.clearAllMocks());

  it('sends limit and scope as query params and reads X-Total-Count', async () => {
    get.mockResolvedValue({ data: [{ id: 'l-1' }], headers: { 'x-total-count': '42' } });

    const page = await maintenanceService.getRecentPrinterMaintenanceLogs('p-1', { limit: 10, scope: 'printer' });

    expect(get).toHaveBeenCalledWith('/maintenance/printers/p-1/logs', {
      params: { limit: 10, scope: 'printer' },
    });
    expect(page).toEqual({ logs: [{ id: 'l-1' }], totalCount: 42 });
  });

  it('omits scope when not provided', async () => {
    get.mockResolvedValue({ data: [], headers: { 'x-total-count': '0' } });

    await maintenanceService.getRecentPrinterMaintenanceLogs('p-1', { limit: 10 });

    expect(get).toHaveBeenCalledWith('/maintenance/printers/p-1/logs', { params: { limit: 10 } });
  });

  it('reports a null total when the server omits the header', async () => {
    get.mockResolvedValue({ data: [{ id: 'l-1' }], headers: {} });

    const page = await maintenanceService.getRecentPrinterMaintenanceLogs('p-1', { limit: 10 });

    expect(page.totalCount).toBeNull();
  });
});
