import { describe, it, expect, vi, beforeEach } from 'vitest';
import { maintenanceService } from '../maintenanceService';
import { apiClient } from '../api';
import type { PrinterToolheadOdometer } from '@/types/maintenance';

vi.mock('../api', () => ({
  apiClient: {
    get: vi.fn(),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
  },
}));

const printerId = 'printer-abc';
const url = `/maintenance/printers/${printerId}/toolhead-odometers`;

const sampleOdometer: PrinterToolheadOdometer = {
  toolheadId: 'th-1',
  toolheadName: 'T0',
  nozzleHours: 42.5,
  hotendHours: 42.5,
  nextDueLabel: 'Nozzle change',
  dueState: 'upcoming',
};

describe('MaintenanceService.getPrinterToolheadOdometers', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('returns the array payload as-is when the API responds 200', async () => {
    (apiClient.get as unknown as ReturnType<typeof vi.fn>).mockResolvedValueOnce({
      data: [sampleOdometer],
    });

    const result = await maintenanceService.getPrinterToolheadOdometers(printerId);

    expect(apiClient.get).toHaveBeenCalledWith(url);
    expect(result).toEqual([sampleOdometer]);
  });

  it('returns [] when the API returns an empty list', async () => {
    (apiClient.get as unknown as ReturnType<typeof vi.fn>).mockResolvedValueOnce({ data: [] });
    await expect(maintenanceService.getPrinterToolheadOdometers(printerId)).resolves.toEqual([]);
  });

  it('returns [] defensively when the payload is not an array', async () => {
    (apiClient.get as unknown as ReturnType<typeof vi.fn>).mockResolvedValueOnce({
      data: { not: 'an array' },
    });
    await expect(maintenanceService.getPrinterToolheadOdometers(printerId)).resolves.toEqual([]);
  });

  it('swallows 404 (via statusCode) so the page renders when #711 has not shipped', async () => {
    const err: Error & { statusCode?: number } = new Error('Not found');
    err.statusCode = 404;
    (apiClient.get as unknown as ReturnType<typeof vi.fn>).mockRejectedValueOnce(err);

    await expect(maintenanceService.getPrinterToolheadOdometers(printerId)).resolves.toEqual([]);
  });

  it('swallows 404 (via response.status) for axios-shaped errors', async () => {
    const err: Error & { response?: { status?: number } } = new Error('Not found');
    err.response = { status: 404 };
    (apiClient.get as unknown as ReturnType<typeof vi.fn>).mockRejectedValueOnce(err);

    await expect(maintenanceService.getPrinterToolheadOdometers(printerId)).resolves.toEqual([]);
  });

  it('rethrows non-404 errors so the caller can surface them', async () => {
    const err: Error & { statusCode?: number } = new Error('server exploded');
    err.statusCode = 500;
    (apiClient.get as unknown as ReturnType<typeof vi.fn>).mockRejectedValueOnce(err);

    await expect(
      maintenanceService.getPrinterToolheadOdometers(printerId)
    ).rejects.toThrow('server exploded');
  });
});
