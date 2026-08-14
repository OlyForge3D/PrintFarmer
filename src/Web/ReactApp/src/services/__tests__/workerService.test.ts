import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '../api';
import { workerService } from '../workerService';

vi.mock('../api', () => ({
  apiClient: {
    request: vi.fn(),
  },
}));

describe('workerService', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiClient.request).mockResolvedValue([]);
  });

  it('requests the canonical worker collection route', async () => {
    await workerService.getAllWorkers();

    expect(apiClient.request).toHaveBeenCalledWith({ method: 'GET', url: '/workers/' });
  });

  it('preserves the canonical route when adding pagination', async () => {
    await workerService.getAllWorkers(50, 10);

    expect(apiClient.request).toHaveBeenCalledWith({
      method: 'GET',
      url: '/workers/?limit=50&offset=10',
    });
  });
});
