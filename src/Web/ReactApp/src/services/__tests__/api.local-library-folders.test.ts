import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/services/api';
import { client } from '@/services/api/httpClient';
import type { UnifiedFilesQueryRequest } from '@/types/api';

describe('local library folder API contracts', () => {
  afterEach(() => vi.restoreAllMocks());

  it('creates model and G-code folders using their respective request contracts', async () => {
    const post = vi.spyOn(client, 'post').mockResolvedValue({ data: { success: true } });
    await apiClient.createModelFolder('/Parts');
    await apiClient.createGcodeDirectory('/Parts');
    expect(post.mock.calls).toEqual([
      ['/3d-models/folder', { path: '/Parts', folderType: 'models' }],
      ['/gcode-files/folder', { path: '/Parts' }],
    ]);
  });

  it('uses ids/targetFolderPath for models but modelIds/targetDirectoryId for G-code', async () => {
    const post = vi.spyOn(client, 'post').mockResolvedValue({ data: { success: true } });
    await apiClient.moveModel3dFiles(['model-1'], '/Parts');
    await apiClient.moveGcodeFiles(['gcode-1'], '/Parts');
    expect(post.mock.calls).toEqual([
      ['/3d-models/move', { ids: ['model-1'], targetFolderPath: '/Parts' }],
      ['/gcode-files/move', { modelIds: ['gcode-1'], targetDirectoryId: '/Parts' }],
    ]);
  });

  it.each([undefined, '/', '/Parts'])('forwards folder scope %j and cancellation to the unified query', async path => {
    const data = { items: [], page: 1, pageSize: 50, totalPages: 0, totalItems: 0, totalSize: 0 };
    const post = vi.spyOn(client, 'post').mockResolvedValue({ data });
    const signal = new AbortController().signal;
    const request: UnifiedFilesQueryRequest = {
      page: 1, pageSize: 50, sortBy: 'name', sortOrder: 'asc', filter: 'all', path,
    };
    expect(await apiClient.getUnifiedFiles(request, signal)).toBe(data);
    expect(post).toHaveBeenCalledExactlyOnceWith('/3d-models/files/query', request, { signal });
  });
});
