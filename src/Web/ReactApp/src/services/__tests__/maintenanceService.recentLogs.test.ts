import { describe, it, expect, vi, beforeEach } from 'vitest';
import { apiClient } from '@/services/api';
import { maintenanceService } from '@/services/maintenanceService';

vi.mock('@/services/api', () => ({
  apiClient: { get: vi.fn() },
}));

const get = apiClient.get as unknown as ReturnType<typeof vi.fn>;

describe('maintenanceService.getPrinterMaintenanceLogs (#3297)', () => {
  beforeEach(() => vi.clearAllMocks());

  it('sends limit as a query param and reads X-Total-Count', async () => {
    get.mockResolvedValue({ data: [{ id: 'l-1' }], headers: { 'x-total-count': '42' } });

    const page = await maintenanceService.getPrinterMaintenanceLogs('p-1', { limit: 10 });

    expect(get).toHaveBeenCalledWith('/maintenance/printers/p-1/logs', { params: { limit: 10 } });
    expect(page).toEqual({ logs: [{ id: 'l-1' }], totalCount: 42 });
  });

  it('sends no query params when no limit is given (full history, unchanged request)', async () => {
    get.mockResolvedValue({ data: [{ id: 'l-1' }, { id: 'l-2' }], headers: {} });

    const page = await maintenanceService.getPrinterMaintenanceLogs('p-1');

    expect(get).toHaveBeenCalledWith('/maintenance/printers/p-1/logs', undefined);
    expect(page).toEqual({ logs: [{ id: 'l-1' }, { id: 'l-2' }], totalCount: null });
  });

  it('reports a null total when the server omits the header', async () => {
    get.mockResolvedValue({ data: [{ id: 'l-1' }], headers: {} });

    const page = await maintenanceService.getPrinterMaintenanceLogs('p-1', { limit: 10 });

    expect(page.totalCount).toBeNull();
  });
});