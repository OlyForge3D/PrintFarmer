import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ComponentProps } from 'react';
import { ModelsFileBrowser } from '../ModelsFileBrowser';
import type { Model, Model3DSearchResponse } from '@/types/models';

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    get3DModelsQuery: vi.fn(),
    deleteModel3dFile: vi.fn(),
  },
}));

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

import { apiClient } from '@/services/api';

function makeModel(id: string): Model {
  return {
    id,
    path: `/${id}.stl`,
    name: id,
    fileName: `${id}.stl`,
    fileSize: 1024,
    fileType: 'stl',
    uploadedAt: '2026-01-01T00:00:00Z',
  };
}

function makeResponse(models: Model[], page = 1, totalPages = 1): Model3DSearchResponse {
  return { models, totalCount: models.length, page, pageSize: 500, totalPages };
}

function renderBrowser(props: Partial<ComponentProps<typeof ModelsFileBrowser>> = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <ModelsFileBrowser {...props} />
    </QueryClientProvider>
  );
}

describe('ModelsFileBrowser - collection member filtering (#846)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('skips the network request entirely when the selected collection has no members', async () => {
    renderBrowser({ collectionModelIds: [] });

    await waitFor(() => expect(screen.getByRole('region', { name: /file browser/i })).toBeInTheDocument());
    expect(apiClient.get3DModelsQuery).not.toHaveBeenCalled();
  });

  it('pages through the full result set to find collection members beyond the first server page', async () => {
    vi.mocked(apiClient.get3DModelsQuery)
      .mockResolvedValueOnce(makeResponse([makeModel('a'), makeModel('b')], 1, 2))
      .mockResolvedValueOnce(makeResponse([makeModel('c')], 2, 2));

    renderBrowser({ collectionModelIds: ['c'] });

    await waitFor(() => expect(apiClient.get3DModelsQuery).toHaveBeenCalledTimes(2));
    // ModelsFileBrowser maps Model.name (not Model.fileName) to the grid item's display name.
    expect(await screen.findByText('c')).toBeInTheDocument();
    expect(screen.queryByText('a')).not.toBeInTheDocument();
  });
});
