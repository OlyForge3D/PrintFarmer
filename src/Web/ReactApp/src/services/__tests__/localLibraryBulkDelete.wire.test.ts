import { afterEach, describe, expect, it } from 'vitest';
import type { AxiosAdapter, AxiosInstance, InternalAxiosRequestConfig } from 'axios';
import { apiClient } from '../api';

/**
 * Wire-level contract for the local-library bulk delete routes (issue #3579): the real axios
 * instance serializes the body, so a wrong property name (the old `{ modelIds }`) fails here.
 */
describe('apiClient local-library bulk delete wire contract', () => {
  const axiosClient = (apiClient as unknown as { client: AxiosInstance }).client;
  const originalAdapter = axiosClient.defaults.adapter;

  afterEach(() => {
    axiosClient.defaults.adapter = originalAdapter;
  });

  function capture(responseBody: unknown) {
    const requests: InternalAxiosRequestConfig[] = [];
    const adapter: AxiosAdapter = async (config) => {
      requests.push(config);
      return { data: responseBody, status: 200, statusText: 'OK', headers: {}, config };
    };
    axiosClient.defaults.adapter = adapter;
    return requests;
  }

  it('sends models as { ids } to DELETE /3d-models and returns the typed response', async () => {
    const body = { deleted: 1, errors: 0, total: 1, results: [{ id: 'm1', status: 'Deleted' }] };
    const requests = capture(body);
    await expect(apiClient.deleteModel3dFiles(['m1', 'm2'])).resolves.toEqual(body);
    expect(requests).toHaveLength(1);
    expect(requests[0].method).toBe('delete');
    expect(requests[0].url).toBe('/3d-models');
    expect(JSON.parse(requests[0].data as string)).toEqual({ ids: ['m1', 'm2'] });
  });

  it('sends G-code as { fileIds } to DELETE /gcode-files and returns the typed response', async () => {
    const body = { deleted: 0, totalRequested: 1, results: [{ id: 'g1', status: 'Blocked', reason: 'ActiveJobs' }] };
    const requests = capture(body);
    await expect(apiClient.deleteGcodeFiles(['g1'])).resolves.toEqual(body);
    expect(requests).toHaveLength(1);
    expect(requests[0].method).toBe('delete');
    expect(requests[0].url).toBe('/gcode-files');
    expect(JSON.parse(requests[0].data as string)).toEqual({ fileIds: ['g1'] });
  });
});
