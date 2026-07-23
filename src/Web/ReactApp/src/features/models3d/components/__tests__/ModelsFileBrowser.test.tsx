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

/** Renders with a caller-supplied (reusable) QueryClient so a `rerender` with new props
 * exercises the same React Query cache, matching how `ModelsPage` mounts a single
 * `ModelsFileBrowser` instance while `collectionModelIds` changes underneath it. */
function renderBrowserWithClient(
  queryClient: QueryClient,
  props: Partial<ComponentProps<typeof ModelsFileBrowser>> = {}
) {
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

  it('fetches collection members once they load, instead of reusing the empty-collection cache entry', async () => {
    // Mirrors ModelsPage: collectionModelIds starts as [] while useModelCollectionMembers
    // is still loading, then resolves to the real membership ids on the same mount.
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { rerender } = renderBrowserWithClient(queryClient, { collectionModelIds: [] });

    await waitFor(() => expect(screen.getByRole('region', { name: /file browser/i })).toBeInTheDocument());
    expect(apiClient.get3DModelsQuery).not.toHaveBeenCalled();

    vi.mocked(apiClient.get3DModelsQuery).mockResolvedValueOnce(makeResponse([makeModel('c')], 1, 1));

    rerender(
      <QueryClientProvider client={queryClient}>
        <ModelsFileBrowser collectionModelIds={['c']} />
      </QueryClientProvider>
    );

    await waitFor(() => expect(apiClient.get3DModelsQuery).toHaveBeenCalledTimes(1));
    expect(await screen.findByText('c')).toBeInTheDocument();
  });

  it('renders a safe empty result instead of crashing when the query response is a bare array (e.g. slicer module disabled)', async () => {
    // Some deployments return the endpoint's stub `[]` instead of the paged
    // `{ models, totalPages, ... }` envelope; the collection-filtering path must
    // normalize this before iterating instead of crashing on `response.models`.
    vi.mocked(apiClient.get3DModelsQuery).mockResolvedValueOnce([]);

    renderBrowser({ collectionModelIds: ['c'] });

    await waitFor(() => expect(apiClient.get3DModelsQuery).toHaveBeenCalledTimes(1));
    expect(await screen.findByText('No files found')).toBeInTheDocument();
    expect(screen.queryByText('c')).not.toBeInTheDocument();
  });

  it('does not reuse another collection\'s cached results when switching the active collection', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    vi.mocked(apiClient.get3DModelsQuery).mockResolvedValueOnce(makeResponse([makeModel('a')], 1, 1));

    const { rerender } = renderBrowserWithClient(queryClient, { collectionModelIds: ['a'] });
    expect(await screen.findByText('a')).toBeInTheDocument();
    expect(apiClient.get3DModelsQuery).toHaveBeenCalledTimes(1);

    vi.mocked(apiClient.get3DModelsQuery).mockResolvedValueOnce(makeResponse([makeModel('b')], 1, 1));

    rerender(
      <QueryClientProvider client={queryClient}>
        <ModelsFileBrowser collectionModelIds={['b']} />
      </QueryClientProvider>
    );

    await waitFor(() => expect(apiClient.get3DModelsQuery).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('b')).toBeInTheDocument();
    expect(screen.queryByText('a')).not.toBeInTheDocument();
  });
});
