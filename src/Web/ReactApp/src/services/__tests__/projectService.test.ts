import { beforeEach, describe, expect, it, vi } from 'vitest';
import { projectService } from '@/services/projectService';
import type { AddFileToProjectRequest, PrintProjectFileDto } from '@/types/api';
import { PrintProjectFileStatus } from '@/types/api';

const apiClient = vi.hoisted(() => ({ post: vi.fn(), genericPost: vi.fn() }));
vi.mock('@/services/api', () => ({
  apiClient,
}));

function projectFile(gcodeFileId: string): PrintProjectFileDto {
  return {
    id: `project-${gcodeFileId}`, gcodeFileId, fileName: `${gcodeFileId}.gcode`,
    printCount: 1, printedCount: 0, status: PrintProjectFileStatus.Pending, sortOrder: 0,
    isComplete: false, remainingPrints: 1,
  };
}

describe('projectService bulk add', () => {
  beforeEach(() => vi.resetAllMocks());

  it.each([1, 10, 50])('sends %i selections and every field in one POST', async (count) => {
    const requests: AddFileToProjectRequest[] = Array.from({ length: count }, (_, i) => ({
      gcodeFileId: `file-${i}`, printCount: i + 1,
      spoolmanFilamentId: i % 2 === 0 ? i : null,
      materialRequirement: 'PLA', notes: `part ${i}`,
      plateIndex: i, plateName: `Plate ${i}`,
    }));
    const added = requests.map((r) => projectFile(r.gcodeFileId)).reverse();
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: added });

    expect(await projectService.addFilesToProject('project-1', requests)).toBe(added);
    expect(apiClient.post).toHaveBeenCalledExactlyOnceWith('/projects/project-1/files', requests);
    expect(apiClient.genericPost).not.toHaveBeenCalled();
  });

  it('returns an empty result without a request for empty input', async () => {
    expect(await projectService.addFilesToProject('project-1', [])).toEqual([]);
    expect(apiClient.post).not.toHaveBeenCalled();
    expect(apiClient.genericPost).not.toHaveBeenCalled();
  });

  it.each([true, false])('returns only actual DTOs when IDs are skipped (all skipped: %s)', async (allSkipped) => {
    const requests = ['existing', 'new', 'new', 'missing'].map((gcodeFileId) => ({ gcodeFileId }));
    const added = allSkipped ? [] : [projectFile('new')];
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: added });

    expect(await projectService.addFilesToProject('project-1', requests)).toBe(added);
    expect(apiClient.post).toHaveBeenCalledExactlyOnceWith('/projects/project-1/files', requests);
  });

  it('propagates a batch failure without singleton retries or a partial-success result', async () => {
    const error = { message: 'Project no longer exists', statusCode: 404 };
    vi.mocked(apiClient.post).mockRejectedValueOnce(error);

    await expect(projectService.addFilesToProject('project-1', [
      { gcodeFileId: 'first' }, { gcodeFileId: 'second' },
    ])).rejects.toBe(error);
    expect(apiClient.post).toHaveBeenCalledTimes(1);
    expect(apiClient.genericPost).not.toHaveBeenCalled();
  });

  it('retains the singleton helper for single-file callers', async () => {
    const request = { gcodeFileId: 'single', printCount: 3 };
    const added = projectFile('single');
    vi.mocked(apiClient.genericPost).mockResolvedValueOnce([added]);

    expect(await projectService.addFileToProject('project-1', request)).toBe(added);
    expect(apiClient.genericPost).toHaveBeenCalledExactlyOnceWith('/projects/project-1/files', [request]);
    expect(apiClient.post).not.toHaveBeenCalled();
  });
});
