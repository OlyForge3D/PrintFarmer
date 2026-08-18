import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

// Regression test for issue #1686: when the printed-parts-inventory feature
// is disabled server-side, the page must not fire the SKUs/Bins requests
// alongside the reorder status probe. Unlike PartsInventoryPage.test.tsx,
// this test exercises the *real* tab components and hooks (nothing but the
// service layer is mocked) so it can assert on actual network call counts.

const listParts = vi.hoisted(() => vi.fn());
const listBins = vi.hoisted(() => vi.fn());
const listReorderCandidates = vi.hoisted(() => vi.fn());
const listMappings = vi.hoisted(() => vi.fn());

vi.mock('@/services/partsInventoryService', () => ({
  partsInventoryService: {
    listParts,
    listBins,
    listReorderCandidates,
    listMappings,
    getPart: vi.fn(),
    listAdjustments: vi.fn(),
    createPart: vi.fn(),
    updatePart: vi.fn(),
    deletePart: vi.fn(),
    adjustStock: vi.fn(),
    createMapping: vi.fn(),
    deleteMapping: vi.fn(),
    createBin: vi.fn(),
    updateBin: vi.fn(),
    deleteBin: vi.fn(),
    registerBinBarcode: vi.fn(),
  },
}));

import { PartsInventoryPage } from '../pages/PartsInventoryPage';
import { partsInventoryKeys } from '../hooks/usePartsInventory';

// Shape tolerated by `isFeatureDisabledError` (utils/problemDetails.ts):
// an axios-error-like object carrying a ProblemDetails `code` extension.
const FEATURE_DISABLED_ERROR = {
  response: { status: 404, data: { code: 'featureDisabled' } },
};

function makeQueryClient() {
  return new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
}

function renderPage(path = '/parts-inventory/skus', queryClient = makeQueryClient()) {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/parts-inventory" element={<PartsInventoryPage />} />
          <Route path="/parts-inventory/:tabId" element={<PartsInventoryPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>
  );
}

describe('PartsInventoryPage query gating when feature disabled', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.localStorage.clear();
  });

  it('does not request SKUs, Bins, or Mappings when the reorder probe reports the feature disabled', async () => {
    listReorderCandidates.mockRejectedValue(FEATURE_DISABLED_ERROR);
    listParts.mockResolvedValue([]);
    listBins.mockResolvedValue([]);
    listMappings.mockResolvedValue([]);

    renderPage('/parts-inventory/skus');

    await waitFor(() => {
      expect(
        screen.getByText(/parts inventory feature is currently disabled/i)
      ).toBeInTheDocument();
    });

    // The reorder probe is expected to fire (it's how we learn the feature
    // is disabled) but the SKUs/Bins/Mappings tab queries must never fire,
    // whichever tab happened to be the active/default one.
    expect(listReorderCandidates).toHaveBeenCalledTimes(1);
    expect(listParts).not.toHaveBeenCalled();
    expect(listBins).not.toHaveBeenCalled();
    expect(listMappings).not.toHaveBeenCalled();
  });

  it('still fetches the active tab data once the probe confirms the feature is enabled', async () => {
    listReorderCandidates.mockResolvedValue([]);
    listParts.mockResolvedValue([]);
    listBins.mockResolvedValue([]);
    listMappings.mockResolvedValue([]);

    renderPage('/parts-inventory/skus');

    await waitFor(() => {
      expect(listParts).toHaveBeenCalledTimes(1);
      expect(listBins).toHaveBeenCalledTimes(1);
    });
    expect(listReorderCandidates).toHaveBeenCalledTimes(1);
    expect(
      screen.queryByText(/parts inventory feature is currently disabled/i)
    ).not.toBeInTheDocument();
  });

  it('does not leak SKUs/Bins requests while a stale cached "enabled" reorder result is being revalidated', async () => {
    // Simulate a prior mount (earlier in the same SPA session) that saw the
    // feature enabled and cached an empty reorder-candidates success. Mark
    // it stale (older than the hook's 30s staleTime) so this mount triggers
    // react-query's default refetch-on-mount for stale data: the query
    // resolves synchronously from cache (`isLoading`/`isPending` false)
    // while a real network refetch is still in flight — the exact scenario
    // that could let a naive `isLoading`-only gate mount the tabs early.
    const queryClient = makeQueryClient();
    queryClient.setQueryData(partsInventoryKeys.reorder(), [], {
      updatedAt: Date.now() - 60_000,
    });

    // An admin disabled the feature in the meantime, so the background
    // refetch now comes back `featureDisabled`.
    listReorderCandidates.mockRejectedValue(FEATURE_DISABLED_ERROR);
    listParts.mockResolvedValue([]);
    listBins.mockResolvedValue([]);
    listMappings.mockResolvedValue([]);

    renderPage('/parts-inventory/skus', queryClient);

    await waitFor(() => {
      expect(
        screen.getByText(/parts inventory feature is currently disabled/i)
      ).toBeInTheDocument();
    });

    expect(listReorderCandidates).toHaveBeenCalledTimes(1);
    expect(listParts).not.toHaveBeenCalled();
    expect(listBins).not.toHaveBeenCalled();
  });
});
