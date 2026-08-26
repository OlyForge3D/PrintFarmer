import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

// The mocks below are hoisted by Vitest before the SUT imports the services.
vi.mock('@/services/slicerProfilesService', () => ({
  slicerProfilesService: {
    listHierarchical: vi.fn(),
    listCustomProfiles: vi.fn(() => Promise.resolve({ profiles: [], totalCount: 0 })),
    setDefault: vi.fn(() => Promise.resolve()),
    importProfile: vi.fn(),
    bulkDelete: vi.fn(),
    cloneProfile: vi.fn(),
    uploadProfile: vi.fn(),
    updateCustomProfile: vi.fn(),
    deleteCustomProfile: vi.fn(),
  },
}));

vi.mock('@/services/slicerRegistry', () => ({
  slicerRegistry: {
    getSlicers: vi.fn(() => Promise.resolve([{ id: '1', name: 'orca-1', slicerType: 'OrcaSlicer', version: '2.3.1' }])),
  },
}));

vi.mock('@/services/catalogService', () => ({
  catalogService: {
    getModels: vi.fn(() => Promise.resolve([])),
  },
}));

vi.mock('@/features/slicer/orca', () => ({
  orcaProfilesService: {
    exportBundle: vi.fn(),
  },
}));

// The SignalR hub connection is a side-effect-only useEffect on the profiles
// page. A no-op stub keeps the test off the network and off the real transport
// registration path while preserving the render.
vi.mock('@/services/slicerRegistryHubConnection', () => ({
  createSlicerRegistryConnection: vi.fn(() => ({
    connection: {
      on: vi.fn(),
      start: vi.fn(() => Promise.resolve()),
      stop: vi.fn(() => Promise.resolve()),
    },
    dispose: vi.fn(() => Promise.resolve()),
  })),
}));

import { SlicerProfilesPage } from '../SlicerProfilesPage';
import { slicerProfilesService } from '@/services/slicerProfilesService';

// Minimal hierarchical fixture containing one machine profile row. The row is
// what renders the "Set Default" button that fires setDefaultMutation.
const machineProfileFixture = {
  id: 'machine-1',
  name: 'Prusa MK4 0.4 nozzle',
  slicerType: 'OrcaSlicer',
  isDefault: false,
  isSystem: true,
  isPublic: true,
  hash: 'hash-1',
  profileType: 'machine' as const,
  manufacturer: 'Prusa',
  nozzleDiameter: 0.4,
};

const hierarchyFixture = {
  byHierarchy: {
    Prusa: {
      name: 'Prusa',
      models: {
        MK4: {
          name: 'MK4',
          modelId: 'model-1',
          machineProfiles: [machineProfileFixture],
          filamentProfiles: [],
          processProfiles: [],
        },
      },
    },
  },
  machineProfiles: {},
  filamentProfiles: {},
  processProfiles: {},
};

function renderPage(queryClient: QueryClient) {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <SlicerProfilesPage />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

describe('SlicerProfilesPage — filtered cache invalidation (issue #2067)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(slicerProfilesService.listHierarchical).mockResolvedValue(hierarchyFixture);
  });

  // Regression guard for issue #2067: every mutation on this page must
  // invalidate the `slicerProfilesHierarchyFiltered` prefix key so the
  // machine-profile-filtered hierarchy view refetches immediately after any
  // mutation, not on the next `staleTime` expiry. `setDefaultMutation` is one
  // representative of the seven mutation onSuccess handlers listed in the
  // issue; the fix applies the same prefix invalidation across all nine sites
  // (seven mutations + one SignalR handler + the manual Refresh button).
  it('invalidates the slicerProfilesHierarchyFiltered prefix key after setDefault', async () => {
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false, gcTime: 0, staleTime: 0 },
        mutations: { retry: false },
      },
    });
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    renderPage(queryClient);

    const setDefaultButton = await screen.findByRole('button', { name: /set default/i });
    await userEvent.click(setDefaultButton);

    await waitFor(() => {
      expect(slicerProfilesService.setDefault).toHaveBeenCalledWith('machine-1');
    });

    // Assert prefix invalidation — no `selectedMachineProfileId` element, so
    // every cached filter variant refetches on next mount and the currently
    // selected filter refetches immediately.
    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith(
        expect.objectContaining({ queryKey: ['slicerProfilesHierarchyFiltered'] })
      );
    });
  });
});
