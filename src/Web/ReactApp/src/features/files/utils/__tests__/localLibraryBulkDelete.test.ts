import { describe, expect, it, vi } from 'vitest';
import type { BulkDeleteItemResult } from '@/types/api';
import {
  deleteLocalLibraryFiles,
  describeLocalLibraryDelete,
  type LocalLibraryDeleteClient,
} from '../localLibraryBulkDelete';

const M1 = '11111111-1111-1111-1111-111111111111';
const M2 = 'cccccccc-2222-2222-2222-222222222222';
const G1 = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const G2 = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';

function parse(value: string) {
  const [source, actualId] = value.split(':');
  return (source === 'model' || source === 'gcode') && actualId ? { source, actualId } as const : null;
}

function results(ids: string[], status: BulkDeleteItemResult['status'] = 'Deleted'): BulkDeleteItemResult[] {
  return ids.map((id) => ({ id, status }));
}

function client(overrides: Partial<LocalLibraryDeleteClient> = {}) {
  return {
    deleteModel3dFiles: vi.fn(async (ids: string[]) => ({
      deleted: ids.length, errors: 0, total: ids.length, results: results(ids),
    })),
    deleteGcodeFiles: vi.fn(async (fileIds: string[]) => ({
      deleted: fileIds.length, totalRequested: fileIds.length, results: results(fileIds),
    })),
    ...overrides,
  };
}

describe('deleteLocalLibraryFiles', () => {
  it('sends no request for an unparseable selection', async () => {
    const api = client();
    const summary = await deleteLocalLibraryFiles(['bogus', 'printer:x'], api, parse);
    expect(api.deleteModel3dFiles).not.toHaveBeenCalled();
    expect(api.deleteGcodeFiles).not.toHaveBeenCalled();
    expect(summary.counts.Failed).toBe(2);
    expect([...summary.retainedIds]).toEqual(['bogus', 'printer:x']);
  });

  it('sends exactly one request for a single-source selection', async () => {
    const api = client();
    await deleteLocalLibraryFiles([`gcode:${G1}`, `gcode:${G2}`], api, parse);
    expect(api.deleteGcodeFiles).toHaveBeenCalledTimes(1);
    expect(api.deleteGcodeFiles).toHaveBeenCalledWith([G1, G2]);
    expect(api.deleteModel3dFiles).not.toHaveBeenCalled();
  });

  it('groups a mixed selection into one request per source and collapses duplicates', async () => {
    const api = client();
    const summary = await deleteLocalLibraryFiles(
      [`model:${M1}`, `gcode:${G1}`, `model:${M1}`, `model:${M2.toUpperCase()}`, `model:${M2}`, `gcode:${G1}`],
      api,
      parse,
    );
    expect(api.deleteModel3dFiles).toHaveBeenCalledTimes(1);
    expect(api.deleteModel3dFiles).toHaveBeenCalledWith([M1, M2.toUpperCase()]);
    expect(api.deleteGcodeFiles).toHaveBeenCalledTimes(1);
    expect(api.deleteGcodeFiles).toHaveBeenCalledWith([G1]);
    expect(summary.counts.Deleted).toBe(3);
    // Every alias, including case variants, is resolved so none stays falsely selected.
    expect([...summary.removedIds].sort()).toEqual(
      [`gcode:${G1}`, `model:${M1}`, `model:${M2.toUpperCase()}`, `model:${M2}`].sort(),
    );
  });

  it('maps mixed per-ID outcomes and retains blocked, failed and unknown items', async () => {
    const missing = '33333333-3333-3333-3333-333333333333';
    const api = client({
      deleteGcodeFiles: vi.fn(async () => ({
        deleted: 1, totalRequested: 2,
        results: [{ id: G1, status: 'Deleted' as const }, { id: G2, status: 'Blocked' as const, reason: 'ActiveJobs' }],
      })),
      deleteModel3dFiles: vi.fn(async () => ({
        deleted: 1, errors: 1, total: 3,
        results: [
          { id: M1, status: 'DeletedCleanupIncomplete' as const, reason: 'ThumbnailCleanupFailed' },
          { id: M2, status: 'Failed' as const, reason: 'StorageFailed' },
        ],
      })),
    });
    const summary = await deleteLocalLibraryFiles(
      [`gcode:${G1}`, `gcode:${G2}`, `model:${M1}`, `model:${M2}`, `model:${missing}`],
      api,
      parse,
    );
    expect(summary.outcomes.get(`gcode:${G2}`)).toEqual({ status: 'Blocked', reason: 'ActiveJobs' });
    expect(summary.outcomes.get(`model:${missing}`)).toEqual({ status: 'Unknown', reason: 'MissingResult' });
    expect([...summary.removedIds].sort()).toEqual([`gcode:${G1}`, `model:${M1}`].sort());
    expect([...summary.retainedIds].sort()).toEqual([`gcode:${G2}`, `model:${M2}`, `model:${missing}`].sort());
    expect(describeLocalLibraryDelete(summary)).toEqual({
      level: 'warning',
      message: 'Deleted 2 files, storage cleanup incomplete for 1, 1 in use by active jobs, 1 failed, '
        + '1 with an unknown outcome — check the refreshed list',
    });
  });

  it('treats NotFound as removed and reports it separately', async () => {
    const api = client({
      deleteGcodeFiles: vi.fn(async () => ({
        deleted: 0, totalRequested: 1, results: [{ id: G1, status: 'NotFound' as const }],
      })),
    });
    const summary = await deleteLocalLibraryFiles([`gcode:${G1}`], api, parse);
    expect(summary.removedIds.has(`gcode:${G1}`)).toBe(true);
    expect(describeLocalLibraryDelete(summary)).toEqual({ level: 'success', message: '1 already removed' });
  });

  it('keeps each source independent and never replays singles after a transport failure', async () => {
    const api = client({
      deleteModel3dFiles: vi.fn(async () => { throw { statusCode: 500, message: 'Network Error' }; }),
    });
    const summary = await deleteLocalLibraryFiles([`model:${M1}`, `gcode:${G1}`], api, parse);
    expect(api.deleteModel3dFiles).toHaveBeenCalledTimes(1);
    expect(summary.outcomes.get(`model:${M1}`)).toEqual({ status: 'Unknown', reason: 'TransportOutcomeUnknown' });
    expect(summary.outcomes.get(`gcode:${G1}`)).toEqual({ status: 'Deleted' });
    expect(describeLocalLibraryDelete(summary).level).toBe('warning');
  });

  it('reports a 4xx rejection as failed rather than unknown', async () => {
    const api = client({
      deleteGcodeFiles: vi.fn(async () => { throw { statusCode: 403, message: 'Forbidden' }; }),
    });
    const summary = await deleteLocalLibraryFiles([`gcode:${G1}`], api, parse);
    expect(summary.outcomes.get(`gcode:${G1}`)).toEqual({ status: 'Failed', reason: 'RequestRejected' });
    expect(describeLocalLibraryDelete(summary)).toEqual({
      level: 'error',
      message: 'Failed to delete selected files: 1 failed',
    });
  });

  it('treats a response without per-ID results as unknown', async () => {
    const api = client({
      deleteGcodeFiles: vi.fn(async () => ({ deleted: 1, totalRequested: 1 }) as never),
    });
    const summary = await deleteLocalLibraryFiles([`gcode:${G1}`], api, parse);
    expect(summary.outcomes.get(`gcode:${G1}`)?.status).toBe('Unknown');
  });

  it('reports full success', async () => {
    const summary = await deleteLocalLibraryFiles([`gcode:${G1}`, `model:${M1}`], client(), parse);
    expect(describeLocalLibraryDelete(summary)).toEqual({ level: 'success', message: 'Deleted 2 files' });
  });
});
