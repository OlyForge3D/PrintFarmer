import React from 'react';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';

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

function renderFilesPage() {
  const queryClient = new QueryClient({
    // Mirror src/services/queryClient.ts's production defaults (30s
    // staleTime) rather than react-query's own default of 0. Several of the
    // tests below specifically exist to prove the epoch-keyed query still
    // refetches and re-arms the loading gate on reopen *despite* this
    // staleTime - if the test client didn't share it, those tests would
    // pass even against a broken implementation that only worked by luck of
    // an unrealistic staleTime: 0.
    defaultOptions: { queries: { retry: false, staleTime: 30_000 } },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/files']}>
        <FilesPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('FilesPage harvest operations polling', () => {
  beforeEach(() => {
    apiMocks.getAllActiveHarvests.mockReset();
    apiMocks.getAllActiveHarvests.mockResolvedValue([]);
  });

  it('does not fetch harvest operations while the harvest wizard modal is closed', async () => {
    const user = userEvent.setup();
    renderFilesPage();

    // Give any stray effects a chance to run, then confirm no request fired.
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(apiMocks.getAllActiveHarvests).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: 'Start Harvest' }));
    expect(await screen.findByRole('dialog', { name: 'Harvest wizard mock' })).toBeInTheDocument();

    await waitFor(() => expect(apiMocks.getAllActiveHarvests).toHaveBeenCalledTimes(1));
  });

  it('fetches only active operations via the dedicated endpoint when the modal opens', async () => {
    const user = userEvent.setup();
    renderFilesPage();

    await user.click(screen.getByRole('button', { name: 'Start Harvest' }));

    await waitFor(() => expect(apiMocks.getAllActiveHarvests).toHaveBeenCalledTimes(1));
    // No narrowing arguments are needed - the dedicated endpoint only ever
    // returns running operations.
    expect(apiMocks.getAllActiveHarvests).toHaveBeenCalledWith();
  });

  describe('with fake timers', () => {
    afterEach(() => {
      vi.useRealTimers();
    });

    it('polls every 5s while open and stops immediately once the modal is closed', async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true });
      const user = userEvent.setup();
      renderFilesPage();

      await user.click(screen.getByRole('button', { name: 'Start Harvest' }));
      await waitFor(() => expect(apiMocks.getAllActiveHarvests).toHaveBeenCalledTimes(1));

      // Advance past two 5s poll ticks while the modal stays open.
      await act(async () => {
        await vi.advanceTimersByTimeAsync(11_000);
      });
      const callsWhileOpen = apiMocks.getAllActiveHarvests.mock.calls.length;
      expect(callsWhileOpen).toBeGreaterThanOrEqual(3);

      await user.click(screen.getByRole('button', { name: 'Close harvest wizard' }));
      await waitFor(() =>
        expect(screen.queryByRole('dialog', { name: 'Harvest wizard mock' })).not.toBeInTheDocument(),
      );

      // Advance well past two more poll intervals - no further requests should
      // fire now that the modal (the query's sole consumer) is closed.
      await act(async () => {
        await vi.advanceTimersByTimeAsync(15_000);
      });
      expect(apiMocks.getAllActiveHarvests).toHaveBeenCalledTimes(callsWhileOpen);
    });

    it('re-arms the loading gate and fetches fresh active harvests every time the modal is reopened, even within the global staleTime window', async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true });
      const user = userEvent.setup();
      renderFilesPage();

      await user.click(screen.getByRole('button', { name: 'Start Harvest' }));
      await waitFor(() => expect(apiMocks.getAllActiveHarvests).toHaveBeenCalledTimes(1));
      expect(await screen.findByTestId('is-loading')).toHaveTextContent('false');

      await user.click(screen.getByRole('button', { name: 'Close harvest wizard' }));
      await waitFor(() =>
        expect(screen.queryByRole('dialog', { name: 'Harvest wizard mock' })).not.toBeInTheDocument(),
      );

      // Reopen well within the test client's 30s staleTime (mirroring
      // production). A query keyed on the *same* cache entry as the first
      // open would neither refetch nor show loading here - proving only the
      // call count increased is not enough, since a stale refetch that
      // resolves instantly can bump the count while `isLoading` never
      // re-arms (exactly the round-1 bug). Defer the second response so we
      // can observe the loading gate re-engage before it resolves.
      let resolveSecondFetch: (value: unknown[]) => void = () => {};
      apiMocks.getAllActiveHarvests.mockImplementationOnce(
        () => new Promise((resolve) => {
          resolveSecondFetch = resolve;
        }),
      );

      await act(async () => {
        await vi.advanceTimersByTimeAsync(2_000);
      });

      await user.click(screen.getByRole('button', { name: 'Start Harvest' }));

      await waitFor(() => expect(apiMocks.getAllActiveHarvests).toHaveBeenCalledTimes(2));
      // The gate must be re-armed immediately on reopen, before the fresh
      // fetch resolves - this is what the epoch-keyed query cache entry
      // (rather than reusing the previous open's cached "success" state)
      // exists to guarantee.
      expect(await screen.findByTestId('is-loading')).toHaveTextContent('true');

      await act(async () => {
        resolveSecondFetch([]);
      });
      await waitFor(() => expect(screen.getByTestId('is-loading')).toHaveTextContent('false'));
    });
  });
});
