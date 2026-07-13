import { describe, it, expect, beforeEach, vi } from 'vitest';
import {
  harvestJob,
  listParts,
  listMappings,
  toHarvestError,
  configurePartsHarvestClient,
  generateHarvestOperationKey,
  HarvestServiceError,
} from '../partsHarvest';

interface StubClient {
  get: ReturnType<typeof vi.fn>;
  post: ReturnType<typeof vi.fn>;
}

function makeStubClient(): StubClient {
  return {
    get: vi.fn(),
    post: vi.fn(),
  };
}

/**
 * Build an axios error whose `response.data` is a canonical ProblemDetails
 * body. `axios.isAxiosError` inspects `isAxiosError: true`, so we set that.
 */
function problemAxiosError(status: number, data: unknown): Error {
  const error = Object.assign(new Error('AxiosError'), {
    isAxiosError: true,
    response: { status, data, headers: {}, statusText: '', config: {} },
    config: {},
  });
  return error;
}

function networkAxiosError(message = 'Network Error'): Error {
  return Object.assign(new Error(message), {
    isAxiosError: true,
    response: undefined,
    config: {},
  });
}

describe('partsHarvest service', () => {
  let stub: StubClient;

  beforeEach(() => {
    stub = makeStubClient();
    configurePartsHarvestClient(stub);
  });

  describe('toHarvestError', () => {
    it('maps 409 wrongBin ProblemDetails to wrongBin error with mismatches', () => {
      const err = problemAxiosError(409, {
        code: 'wrongBin',
        detail: 'Scanned bin does not match expected bin.',
        mismatches: [
          { partSku: 'SKU-A', expectedBinCode: 'BIN-1', scannedBinCode: 'BIN-9' },
          { partSku: 'SKU-B', scannedBinCode: 'BIN-9' },
        ],
      });
      const info = toHarvestError(err);
      expect(info.kind).toBe('wrongBin');
      if (info.kind !== 'wrongBin') return;
      expect(info.mismatches).toHaveLength(2);
      expect(info.mismatches[0].partSku).toBe('SKU-A');
      expect(info.mismatches[1].expectedBinCode).toBeNull();
    });

    it('maps 409 partMappingRequired to partMappingRequired error with details', () => {
      const err = problemAxiosError(409, {
        code: 'partMappingRequired',
        detail: 'This job has no printed-part mapping.',
        jobId: 'job-123',
        projectFileId: 'proj-1',
        gcodeFileId: null,
        guidance: 'Create a mapping first, or enter outputs manually.',
      });
      const info = toHarvestError(err);
      expect(info.kind).toBe('partMappingRequired');
      if (info.kind !== 'partMappingRequired') return;
      expect(info.details.jobId).toBe('job-123');
      expect(info.details.projectFileId).toBe('proj-1');
      expect(info.details.gcodeFileId).toBeNull();
      expect(info.details.guidance).toMatch(/Create a mapping/);
    });

    it('maps 404 featureDisabled ProblemDetails to featureDisabled', () => {
      const err = problemAxiosError(404, {
        code: 'featureDisabled',
        detail: 'Printed-parts inventory is not enabled on this server.',
      });
      const info = toHarvestError(err);
      expect(info.kind).toBe('featureDisabled');
    });

    it('maps 404 without explicit code but with feature messaging to featureDisabled', () => {
      const err = problemAxiosError(404, {
        detail: 'The requested feature is not enabled on this server.',
      });
      const info = toHarvestError(err);
      expect(info.kind).toBe('featureDisabled');
    });

    it('maps 409 non-canonical body with "not completed" text to jobNotCompleted', () => {
      const err = problemAxiosError(409, {
        detail: 'Job is not completed and cannot be harvested.',
      });
      const info = toHarvestError(err);
      expect(info.kind).toBe('jobNotCompleted');
    });

    it('maps 400 to invalidRequest by default and binNotFound when bin is mentioned', () => {
      expect(toHarvestError(problemAxiosError(400, { detail: 'Payload invalid.' })).kind).toBe(
        'invalidRequest',
      );
      expect(
        toHarvestError(problemAxiosError(400, { detail: 'Bin BIN-9 not found.' })).kind,
      ).toBe('binNotFound');
    });

    it('returns network error when no response is attached', () => {
      const info = toHarvestError(networkAxiosError());
      expect(info.kind).toBe('network');
    });

    it('returns unknown for non-axios errors', () => {
      const info = toHarvestError(new Error('boom'));
      expect(info.kind).toBe('unknown');
      expect(info.message).toBe('boom');
    });

    it('classifies unmapped status codes as unknown with status carried', () => {
      const info = toHarvestError(problemAxiosError(500, { detail: 'boom' }));
      expect(info.kind).toBe('unknown');
      if (info.kind !== 'unknown') return;
      expect(info.status).toBe(500);
    });

    it('is guaranteed to be an axios-like error type after guard', () => {
      // Our forged shape carries `isAxiosError: true`, which is what the
      // internal guard checks; verify the mapper accepts it as such.
      const info = toHarvestError(problemAxiosError(400, {}));
      expect(info.kind).not.toBe('unknown');
    });
  });

  describe('harvestJob', () => {
    it('posts to the canonical harvest endpoint and returns the response body', async () => {
      const body = {
        printJobId: 'job-1',
        harvestedAt: '2026-01-01T00:00:00Z',
        alreadyHarvested: false,
        adjustments: [],
        outputs: [],
      };
      stub.post.mockResolvedValueOnce({ data: body });
      const result = await harvestJob('job-1', { operationKey: 'key' });
      expect(stub.post).toHaveBeenCalledWith('/job-queue/job-1/harvest', { operationKey: 'key' });
      expect(result).toEqual(body);
    });

    it('URL-encodes the job id', async () => {
      stub.post.mockResolvedValueOnce({ data: {} });
      await harvestJob('job with spaces', {});
      expect(stub.post).toHaveBeenCalledWith('/job-queue/job%20with%20spaces/harvest', {});
    });

    it('throws HarvestServiceError with a wrongBin info payload on 409 wrongBin', async () => {
      stub.post.mockRejectedValueOnce(
        problemAxiosError(409, {
          code: 'wrongBin',
          detail: 'Bad bin.',
          mismatches: [{ partSku: 'S', expectedBinCode: 'A', scannedBinCode: 'B' }],
        }),
      );
      let caught: unknown;
      try {
        await harvestJob('job-1', {});
      } catch (error) {
        caught = error;
      }
      expect(caught).toBeInstanceOf(HarvestServiceError);
      if (caught instanceof HarvestServiceError) {
        expect(caught.info.kind).toBe('wrongBin');
      }
    });
  });

  describe('listParts / listMappings', () => {
    it('lists parts without inactive by default', async () => {
      stub.get.mockResolvedValueOnce({ data: [] });
      await listParts();
      expect(stub.get).toHaveBeenCalledWith('/parts-inventory', {
        params: { includeInactive: false },
      });
    });

    it('lists mappings filtered by sku', async () => {
      stub.get.mockResolvedValueOnce({ data: [] });
      await listMappings({ sku: 'SKU-A' });
      expect(stub.get).toHaveBeenCalledWith('/parts-inventory/mappings', {
        params: { sku: 'SKU-A' },
      });
    });

    it('translates service errors uniformly', async () => {
      stub.get.mockRejectedValueOnce(networkAxiosError());
      await expect(listParts()).rejects.toBeInstanceOf(HarvestServiceError);
    });
  });

  describe('generateHarvestOperationKey', () => {
    it('returns a UUID-like string', () => {
      const key = generateHarvestOperationKey();
      expect(key).toMatch(/^[0-9a-f-]{8,}$/i);
    });

    it('produces distinct values across calls', () => {
      const a = generateHarvestOperationKey();
      const b = generateHarvestOperationKey();
      expect(a).not.toBe(b);
    });
  });
});
