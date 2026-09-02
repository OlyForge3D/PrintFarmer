import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi, beforeEach } from 'vitest';

// Regression coverage for issue #2377: FilesPage must not poll harvest
// operations while the harvest wizard modal — the sole consumer of that
// data — is closed, and must use the dedicated active-operations endpoint
// so completed history is never fetched.

const apiMocks = vi.hoisted(() => ({
  getAllActiveHarvests: vi.fn(),
}));

vi.mock('@/features/gcode/components/harvest/HarvestWizardModal', () => ({
  HarvestWizardModal: ({
    onClose,
    activeHarvests,
    isLoadingActiveHarvests,
  }: {
    onClose: () => void;
    activeHarvests: unknown[];
    isLoadingActiveHarvests?: boolean;
  }) => (
    <div role="dialog" aria-label="Harvest wizard mock">
      <span data-testid="active-harvest-count">{activeHarvests.length}</span>
      <span data-testid="is-loading">{String(Boolean(isLoadingActiveHarvests))}</span>
      <button type="button" onClick={onClose}>
        Close harvest wizard
      </button>
    </div>
  ),
}));

vi.mock('@/features/models3d/components/PrintablesBrowserModal', () => ({
  PrintablesBrowserModal: () => null,
}));
vi.mock('@/features/models3d/components/PrintablesImportModal', () => ({
  PrintablesImportModal: () => null,
}));
vi.mock('@/features/gcode/components/QueueGcodeModal', () => ({
  QueueGcodeModal: () => null,
}));
vi.mock('@/features/slicer/components/QuickSliceModal', () => ({
  QuickSliceModal: () => null,
}));

vi.mock('@/features/fileBrowser/components/FileBrowser', async () => {
  const ReactModule = await import('react');
  return {
    FileBrowser: ReactModule.forwardRef(function FileBrowserMock(_props, ref) {
      void ref;
      return <div />;
    }),
  };
});

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

vi.mock('@/common/hooks/useApi', () => ({
  usePrinters: () => ({ data: [] }),
}));

vi.mock('@/common/hooks/useViewModePreference', () => ({
  useViewModePreference: () => ({ viewMode: 'grid', setViewMode: vi.fn() }),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getAllActiveHarvests: apiMocks.getAllActiveHarvests,
    getUnifiedFiles: vi.fn().mockResolvedValue({
      items: [],
      totalItems: 0,
      totalSize: 0,
      page: 1,
      pageSize: 50,
      totalPages: 1,
    }),
  },
}));

import { FilesPage } from '../FilesPage';

describe('FilesPage harvest operations polling', () => {
  beforeEach(() => {
    apiMocks.getAllActiveHarvests.mockReset();
    apiMocks.getAllActiveHarvests.mockResolvedValue([]);
  });

  it('does not fetch harvest operations while the harvest wizard modal is closed', async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/files']}>
          <FilesPage />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    // Give any stray effects a chance to run, then confirm no request fired.
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(apiMocks.getAllActiveHarvests).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: 'Start Harvest' }));
    expect(await screen.findByRole('dialog', { name: 'Harvest wizard mock' })).toBeInTheDocument();

    await waitFor(() => expect(apiMocks.getAllActiveHarvests).toHaveBeenCalledTimes(1));
  });

  it('fetches only active operations via the dedicated endpoint when the modal opens', async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/files']}>
          <FilesPage />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole('button', { name: 'Start Harvest' }));

    await waitFor(() => expect(apiMocks.getAllActiveHarvests).toHaveBeenCalledTimes(1));
    // No narrowing arguments are needed - the dedicated endpoint only ever
    // returns running operations.
    expect(apiMocks.getAllActiveHarvests).toHaveBeenCalledWith();
  });

  it('stops polling once the harvest wizard modal is closed again', async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/files']}>
          <FilesPage />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole('button', { name: 'Start Harvest' }));
    await waitFor(() => expect(apiMocks.getAllActiveHarvests).toHaveBeenCalledTimes(1));

    await user.click(screen.getByRole('button', { name: 'Close harvest wizard' }));
    await waitFor(() =>
      expect(screen.queryByRole('dialog', { name: 'Harvest wizard mock' })).not.toBeInTheDocument(),
    );

    const callsAtClose = apiMocks.getAllActiveHarvests.mock.calls.length;
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(apiMocks.getAllActiveHarvests).toHaveBeenCalledTimes(callsAtClose);
  });
});
