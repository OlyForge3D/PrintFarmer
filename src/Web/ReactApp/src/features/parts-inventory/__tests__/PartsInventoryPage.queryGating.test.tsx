import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

// Regression tests for issue #1761: when the printed-parts-inventory
// feature is disabled server-side, the page must not fire the reorder
// probe (or the SKUs/Bins/Mappings tab queries) at all. The prior fix for
// issue #1686 kept the SKUs/Bins/Mappings queries from racing the reorder
// probe, but the probe itself — hitting `/api/parts-inventory/reorder` on
// every mount to *detect* the disabled state — still fired and 404'd. This
// suite exercises the *real* tab components and hooks (nothing but the
// service layer and system-capabilities hook are mocked) so it can assert
// on actual network call counts.

const listParts = vi.hoisted(() => vi.fn());
const listBins = vi.hoisted(() => vi.fn());
const listReorderCandidates = vi.hoisted(() => vi.fn());
const listMappings = vi.hoisted(() => vi.fn());
const useSystemCapabilitiesMock = vi.hoisted(() => vi.fn());

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

vi.mock('@/common/hooks/useSystemCapabilities', () => ({
  useSystemCapabilities: () => useSystemCapabilitiesMock(),
}));

import { PartsInventoryPage } from '../pages/PartsInventoryPage';

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

  it('does not request the reorder probe, SKUs, Bins, or Mappings when capabilities report the feature disabled', async () => {
    useSystemCapabilitiesMock.mockReturnValue({
      data: { operatorFeatures: { printedPartsInventoryEnabled: false } },
      status: 'success',
    });
    listReorderCandidates.mockResolvedValue([]);
    listParts.mockResolvedValue([]);
    listBins.mockResolvedValue([]);
    listMappings.mockResolvedValue([]);

    renderPage('/parts-inventory/skus');

    await waitFor(() => {
      expect(
        screen.getByText(/parts inventory feature is currently disabled/i)
      ).toBeInTheDocument();
    });

    // Neither the reorder probe nor any tab query should ever fire — the
    // disabled state is known up front from system capabilities, so no
    // parts-inventory API request (including reorder) is made at all.
    expect(listReorderCandidates).not.toHaveBeenCalled();
    expect(listParts).not.toHaveBeenCalled();
    expect(listBins).not.toHaveBeenCalled();
    expect(listMappings).not.toHaveBeenCalled();
  });

  it('still fetches the reorder probe and the active tab data once capabilities confirm the feature is enabled', async () => {
    useSystemCapabilitiesMock.mockReturnValue({
      data: { operatorFeatures: { printedPartsInventoryEnabled: true } },
      status: 'success',
    });
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

  it('treats a missing operatorFeatures flag as enabled (older API builds omit it entirely)', async () => {
    useSystemCapabilitiesMock.mockReturnValue({
      data: {},
      status: 'success',
    });
    listReorderCandidates.mockResolvedValue([]);
    listParts.mockResolvedValue([]);
    listBins.mockResolvedValue([]);
    listMappings.mockResolvedValue([]);

    renderPage('/parts-inventory/skus');

    await waitFor(() => {
      expect(listParts).toHaveBeenCalledTimes(1);
    });
    expect(
      screen.queryByText(/parts inventory feature is currently disabled/i)
    ).not.toBeInTheDocument();
  });

  it('shows a checking-status placeholder and fires no queries while capabilities are still pending', async () => {
    useSystemCapabilitiesMock.mockReturnValue({ data: undefined, status: 'pending' });
    listReorderCandidates.mockResolvedValue([]);
    listParts.mockResolvedValue([]);
    listBins.mockResolvedValue([]);
    listMappings.mockResolvedValue([]);

    renderPage('/parts-inventory/skus');

    expect(screen.getByText(/checking printed-parts inventory status/i)).toBeInTheDocument();
    expect(listReorderCandidates).not.toHaveBeenCalled();
    expect(listParts).not.toHaveBeenCalled();
    expect(listBins).not.toHaveBeenCalled();
  });
});
