import { describe, it, expect, vi, beforeEach } from 'vitest';
import { partsInventoryService } from '../partsInventoryService';
import { apiClient } from '../api';

vi.mock('../api', () => ({
  apiClient: {
    get: vi.fn(),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
  },
}));

const mockGet = apiClient.get as unknown as ReturnType<typeof vi.fn>;
const mockPost = apiClient.post as unknown as ReturnType<typeof vi.fn>;
const mockPut = apiClient.put as unknown as ReturnType<typeof vi.fn>;
const mockDelete = apiClient.delete as unknown as ReturnType<typeof vi.fn>;

function ok<T>(data: T, status = 200) {
  return { data, status, statusText: 'OK', headers: {}, config: {} as never };
}

describe('partsInventoryService', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('parts CRUD', () => {
    it('lists parts and passes includeInactive=false by default', async () => {
      mockGet.mockResolvedValueOnce(ok([]));
      await partsInventoryService.listParts();
      expect(mockGet).toHaveBeenCalledWith('/parts-inventory', {
        params: { includeInactive: false },
      });
    });

    it('lists parts with includeInactive=true when requested', async () => {
      mockGet.mockResolvedValueOnce(ok([]));
      await partsInventoryService.listParts({ includeInactive: true });
      expect(mockGet).toHaveBeenCalledWith('/parts-inventory', {
        params: { includeInactive: true },
      });
    });

    it('encodes SKU segment on getPart', async () => {
      mockGet.mockResolvedValueOnce(ok({}));
      await partsInventoryService.getPart('SKU/with slash');
      expect(mockGet).toHaveBeenCalledWith('/parts-inventory/SKU%2Fwith%20slash');
    });

    it('POSTs createPart body verbatim (camelCase)', async () => {
      mockPost.mockResolvedValueOnce(ok({}));
      const req = {
        sku: 'BRK-001',
        displayName: 'Bracket',
        reorderPoint: 10,
        defaultBinCode: 'A1',
      };
      await partsInventoryService.createPart(req);
      expect(mockPost).toHaveBeenCalledWith('/parts-inventory', req);
    });

    it('PUTs updatePart to the encoded SKU URL', async () => {
      mockPut.mockResolvedValueOnce(ok({}));
      await partsInventoryService.updatePart('BRK 1', { displayName: 'x' });
      expect(mockPut).toHaveBeenCalledWith('/parts-inventory/BRK%201', { displayName: 'x' });
    });

    it('DELETEs part by encoded SKU', async () => {
      mockDelete.mockResolvedValueOnce(ok(undefined, 204));
      await partsInventoryService.deletePart('BRK 1');
      expect(mockDelete).toHaveBeenCalledWith('/parts-inventory/BRK%201');
    });
  });

  describe('adjust ledger', () => {
    it('POSTs adjust request with string reason token and operationKey', async () => {
      mockPost.mockResolvedValueOnce(ok({}));
      const req = {
        delta: -2,
        reason: 'qc-reject' as const,
        operationKey: '00000000-0000-4000-8000-000000000001',
        notes: 'chipped',
      };
      await partsInventoryService.adjustStock('BRK-1', req);
      expect(mockPost).toHaveBeenCalledWith('/parts-inventory/BRK-1/adjust', req);
    });

    it.each(['harvest', 'qc-reject', 'manual'] as const)(
      'accepts wire reason token %s',
      async (reason) => {
        mockPost.mockResolvedValueOnce(ok({}));
        await partsInventoryService.adjustStock('S', {
          delta: 1,
          reason,
          operationKey: 'k',
        });
        expect(mockPost.mock.calls[0][1]).toMatchObject({ reason });
      }
    );

    it('lists adjustments with default limit', async () => {
      mockGet.mockResolvedValueOnce(ok([]));
      await partsInventoryService.listAdjustments('S');
      expect(mockGet).toHaveBeenCalledWith('/parts-inventory/S/adjustments', {
        params: { limit: 100 },
      });
    });

    it('lists adjustments with custom limit', async () => {
      mockGet.mockResolvedValueOnce(ok([]));
      await partsInventoryService.listAdjustments('S', 25);
      expect(mockGet).toHaveBeenCalledWith('/parts-inventory/S/adjustments', {
        params: { limit: 25 },
      });
    });
  });

  describe('reorder', () => {
    it('GETs reorder endpoint', async () => {
      mockGet.mockResolvedValueOnce(ok([]));
      await partsInventoryService.listReorderCandidates();
      expect(mockGet).toHaveBeenCalledWith('/parts-inventory/reorder');
    });
  });

  describe('mappings', () => {
    it('lists mappings without sku filter', async () => {
      mockGet.mockResolvedValueOnce(ok([]));
      await partsInventoryService.listMappings();
      expect(mockGet).toHaveBeenCalledWith('/parts-inventory/mappings', {
        params: undefined,
      });
    });

    it('lists mappings filtered by sku', async () => {
      mockGet.mockResolvedValueOnce(ok([]));
      await partsInventoryService.listMappings('BRK-1');
      expect(mockGet).toHaveBeenCalledWith('/parts-inventory/mappings', {
        params: { sku: 'BRK-1' },
      });
    });

    it('POSTs mapping body with gcodeFileId', async () => {
      mockPost.mockResolvedValueOnce(ok({}));
      const req = { sku: 'S', quantity: 2, gcodeFileId: 'g1' };
      await partsInventoryService.createMapping(req);
      expect(mockPost).toHaveBeenCalledWith('/parts-inventory/mappings', req);
    });

    it('POSTs mapping body with printProjectFileId', async () => {
      mockPost.mockResolvedValueOnce(ok({}));
      const req = { sku: 'S', quantity: 1, printProjectFileId: 'p1' };
      await partsInventoryService.createMapping(req);
      expect(mockPost).toHaveBeenCalledWith('/parts-inventory/mappings', req);
    });

    it('DELETEs mapping by id', async () => {
      mockDelete.mockResolvedValueOnce(ok(undefined, 204));
      await partsInventoryService.deleteMapping('m1');
      expect(mockDelete).toHaveBeenCalledWith('/parts-inventory/mappings/m1');
    });
  });

  describe('bins', () => {
    it('lists bins with includeInactive=false by default', async () => {
      mockGet.mockResolvedValueOnce(ok([]));
      await partsInventoryService.listBins();
      expect(mockGet).toHaveBeenCalledWith('/bins', {
        params: { includeInactive: false },
      });
    });

    it('encodes bin code on getBin', async () => {
      mockGet.mockResolvedValueOnce(ok({}));
      await partsInventoryService.getBin('A/1');
      expect(mockGet).toHaveBeenCalledWith('/bins/A%2F1');
    });

    it('POSTs createBin body verbatim', async () => {
      mockPost.mockResolvedValueOnce(ok({}));
      const req = { code: 'A1', displayName: 'A1', location: 'Rack A' };
      await partsInventoryService.createBin(req);
      expect(mockPost).toHaveBeenCalledWith('/bins', req);
    });

    it('PUTs updateBin body', async () => {
      mockPut.mockResolvedValueOnce(ok({}));
      await partsInventoryService.updateBin('A1', { displayName: 'A1 renamed' });
      expect(mockPut).toHaveBeenCalledWith('/bins/A1', { displayName: 'A1 renamed' });
    });

    it('DELETEs bin by encoded code', async () => {
      mockDelete.mockResolvedValueOnce(ok(undefined, 204));
      await partsInventoryService.deleteBin('A 1');
      expect(mockDelete).toHaveBeenCalledWith('/bins/A%201');
    });

    it('registerBinBarcode returns wasCreated=true on HTTP 201', async () => {
      mockPost.mockResolvedValueOnce(ok({ id: 'b1', code: 'X' }, 201));
      const result = await partsInventoryService.registerBinBarcode({ code: 'X' });
      expect(mockPost).toHaveBeenCalledWith('/bins/register', { code: 'X' });
      expect(result.wasCreated).toBe(true);
      expect(result.bin.code).toBe('X');
    });

    it('registerBinBarcode returns wasCreated=false on HTTP 200', async () => {
      mockPost.mockResolvedValueOnce(ok({ id: 'b1', code: 'X' }, 200));
      const result = await partsInventoryService.registerBinBarcode({ code: 'X' });
      expect(result.wasCreated).toBe(false);
    });
  });

  describe('error propagation', () => {
    it('bubbles apiClient rejection unchanged', async () => {
      const err = Object.assign(new Error('conflict'), {
        response: {
          status: 409,
          data: {
            type: 'about:blank',
            title: 'Wrong bin',
            code: 'wrongBin',
            mismatches: [],
          },
        },
      });
      mockPost.mockRejectedValueOnce(err);
      await expect(
        partsInventoryService.adjustStock('S', {
          delta: 1,
          reason: 'harvest',
          operationKey: 'k',
        })
      ).rejects.toBe(err);
    });
  });
});
