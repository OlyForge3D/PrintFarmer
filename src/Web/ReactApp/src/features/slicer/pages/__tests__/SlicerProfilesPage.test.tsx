import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

vi.mock('@/services/slicerProfilesService', () => ({
  slicerProfilesService: {
    getLibraryHierarchy: vi.fn(),
    getProcessProfilesForMachines: vi.fn(() => Promise.resolve([])),
    getFilamentProfilesForMachines: vi.fn(() => Promise.resolve([])),
    listCustomProfiles: vi.fn(() => Promise.resolve({ profiles: [], totalCount: 0 })),
    importProfile: vi.fn(),
    bulkDelete: vi.fn(() => Promise.resolve({
      machineProfilesDeleted: 0,
      processProfilesDeleted: 0,
      filamentProfilesDeleted: 0,
      totalDeleted: 1,
      notFound: 0,
    })),
    uploadProfile: vi.fn(),
    updateCustomProfile: vi.fn(),
    deleteCustomProfile: vi.fn(() => Promise.resolve()),
  },
}));

vi.mock('@/services/slicerRegistry', () => ({
  slicerRegistry: {
    getSlicers: vi.fn(() => Promise.resolve([
      { id: '1', name: 'orca-1', slicerType: 'OrcaSlicer', version: '2.3.1' },
    ])),
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

import { SlicerProfilesPage } from '@/features/slicer/pages/SlicerProfilesPage';
import { slicerProfilesService } from '@/services/slicerProfilesService';

const machineNames = [
  'PrintersForAnts Micron 180 0.4 nozzle',
  'PrintersForAnts Micron 180 0.6 nozzle',
];

const hierarchyFixture = {
  byHierarchy: {
    PrintersForAnts: {
      name: 'PrintersForAnts',
      models: {
        'Micron 180': {
          name: 'Micron 180',
          machineProfiles: machineNames.map((name, index) => ({
            name,
            manufacturer: 'PrintersForAnts',
            nozzleDiameter: index === 0 ? 0.4 : 0.6,
          })),
          filamentProfiles: [{
            name: 'Unrelated PLA @MK4S',
            material: 'PLA',
            nozzleTemperature: 210,
            bedTemperature: 60,
            printSpeed: 60,
          }],
          processProfiles: [{
            name: '0.20mm Standard @MK4S',
            quality: 'Standard',
            layerHeight: 0.2,
            infillPercentage: 15,
            printSpeed: 60,
            supports: false,
          }],
        },
      },
    },
  },
  machineProfiles: {},
  filamentProfiles: {},
  processProfiles: {},
};

const customProfile = {
  id: 'custom-1',
  name: 'My Micron process',
  profileType: 'process' as const,
  description: 'User owned',
  createdAt: '2026-08-01T00:00:00Z',
  updatedAt: '2026-08-02T00:00:00Z',
};

function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
      mutations: { retry: false },
    },
  });
}

function renderPage(queryClient = createQueryClient()) {
  return {
    queryClient,
    ...render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <SlicerProfilesPage />
        </MemoryRouter>
      </QueryClientProvider>,
    ),
  };
}

async function selectMicronModel(user = userEvent.setup()) {
  renderPage();
  await screen.findByRole('option', { name: 'PrintersForAnts' });
  await user.selectOptions(screen.getByLabelText('Select manufacturer'), 'PrintersForAnts');
  await screen.findByRole('option', { name: 'Micron 180' });
  await user.selectOptions(screen.getByLabelText('Select machine model'), 'Micron 180');
  return user;
}

describe('SlicerProfilesPage worker-backed library', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(slicerProfilesService.getLibraryHierarchy).mockResolvedValue(hierarchyFixture);
    vi.mocked(slicerProfilesService.getProcessProfilesForMachines).mockResolvedValue([]);
    vi.mocked(slicerProfilesService.getFilamentProfilesForMachines).mockResolvedValue([]);
    vi.mocked(slicerProfilesService.listCustomProfiles).mockResolvedValue({ profiles: [], totalCount: 0 });
  });

  it('pins the admin library request to all scope and renders its attributed manufacturer', async () => {
    renderPage();

    expect(await screen.findByText(machineNames[0])).toBeInTheDocument();
    expect(slicerProfilesService.getLibraryHierarchy).toHaveBeenCalledWith('all');

    const manufacturer = screen.getByLabelText<HTMLSelectElement>('Select manufacturer');
    expect(within(manufacturer).getByRole('option', { name: 'PrintersForAnts' })).toBeInTheDocument();
    expect(within(manufacturer).queryByRole('option', { name: 'Custom' })).not.toBeInTheDocument();
    expect(within(manufacturer).queryByRole('option', { name: /PrintFarmer-/ })).not.toBeInTheDocument();
  });

  it('does not request compatible profiles until a model is selected', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('tab', { name: /Processes/ }));

    expect(screen.getByText('Select a printer model to see compatible processes.')).toBeInTheDocument();
    expect(slicerProfilesService.getProcessProfilesForMachines).not.toHaveBeenCalled();
    expect(slicerProfilesService.getFilamentProfilesForMachines).not.toHaveBeenCalled();
  });

  it('queries all model machines, then narrows to a selected variant', async () => {
    const user = await selectMicronModel();

    await waitFor(() => {
      expect(slicerProfilesService.getProcessProfilesForMachines)
        .toHaveBeenCalledWith(machineNames, undefined, 'summary');
      expect(slicerProfilesService.getFilamentProfilesForMachines)
        .toHaveBeenCalledWith(machineNames, undefined, 'summary');
    });

    await user.selectOptions(screen.getByLabelText('Select machine'), machineNames[0]);

    await waitFor(() => {
      expect(slicerProfilesService.getProcessProfilesForMachines)
        .toHaveBeenLastCalledWith([machineNames[0]], undefined, 'summary');
      expect(slicerProfilesService.getFilamentProfilesForMachines)
        .toHaveBeenLastCalledWith([machineNames[0]], undefined, 'summary');
    });
  });

  it('renders generated Micron processes returned by for-machines', async () => {
    vi.mocked(slicerProfilesService.getProcessProfilesForMachines).mockResolvedValue([{
      name: '0.20mm Standard @Micron 180 0.4 nozzle',
      quality: 'Standard',
      layerHeight: 0.2,
      infillPercentage: 15,
      printSpeed: 60,
      supports: false,
      compatible_printers: [machineNames[0]],
    }]);
    const user = await selectMicronModel();

    await user.click(screen.getByRole('tab', { name: /Processes/ }));

    expect(await screen.findByText('0.20mm Standard @Micron 180 0.4 nozzle')).toBeInTheDocument();
  });

  it('does not fall back to unrelated hierarchy process or filament buckets', async () => {
    const user = await selectMicronModel();

    await user.click(screen.getByRole('tab', { name: /Processes/ }));
    expect(screen.queryByText('0.20mm Standard @MK4S')).not.toBeInTheDocument();
    expect(screen.getByText('No compatible process profiles found for the selected machines.')).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: /Filaments/ }));
    expect(screen.queryByText('Unrelated PLA @MK4S')).not.toBeInTheDocument();
    expect(screen.getByText('No compatible filament profiles found for the selected machines.')).toBeInTheDocument();
    expect(screen.getByText('Includes universal library filaments.')).toBeInTheDocument();
  });

  it('hides ID-based actions and bulk selection on worker tabs', async () => {
    renderPage();

    expect(await screen.findByText(machineNames[0])).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Set Default/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Export$/i })).not.toBeInTheDocument();
    expect(screen.queryByTitle('Clone to My Profiles')).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/Select all profiles/i)).not.toBeInTheDocument();
  });

  it('keeps My Profiles actions and bulk deletion functional', async () => {
    vi.mocked(slicerProfilesService.listCustomProfiles).mockResolvedValue({
      profiles: [customProfile],
      totalCount: 1,
    });
    const user = userEvent.setup();
    const { queryClient } = renderPage();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');
    vi.spyOn(window, 'confirm').mockReturnValue(true);

    await user.click(await screen.findByRole('tab', { name: /My Profiles/ }));
    expect(await screen.findByRole('button', { name: 'Edit' })).toBeInTheDocument();
    await user.click(screen.getByLabelText(`Select ${customProfile.name}`));
    expect(screen.getByRole('button', { name: 'Delete Selected' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      expect(slicerProfilesService.deleteCustomProfile).toHaveBeenCalledWith(customProfile.id);
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['slicerProfilesLibraryHierarchy'] });
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['processProfilesForMachines'] });
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['filamentProfilesForMachines'] });
    });
  });

  it('shows a degraded banner while My Profiles remains usable', async () => {
    vi.mocked(slicerProfilesService.getLibraryHierarchy).mockRejectedValue(
      Object.assign(new Error('Service unavailable'), { statusCode: 503 }),
    );
    vi.mocked(slicerProfilesService.listCustomProfiles).mockResolvedValue({
      profiles: [customProfile],
      totalCount: 1,
    });
    const user = userEvent.setup();
    renderPage();

    expect(await screen.findByText(
      'OrcaSlicer worker unavailable; the profile library cannot be listed. My Profiles is still available.',
    )).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: /My Profiles/ }));
    expect(await screen.findByText(customProfile.name)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Edit' })).toBeInTheDocument();
  });

  it('renders non-503 failures as request errors rather than worker degradation', async () => {
    vi.mocked(slicerProfilesService.getLibraryHierarchy).mockRejectedValue(
      Object.assign(new Error('Invalid library scope'), { statusCode: 400 }),
    );
    renderPage();

    expect(await screen.findByText('Invalid library scope')).toBeInTheDocument();
    expect(screen.getByText('Unable to load profile library')).toBeInTheDocument();
    expect(screen.queryByText(/OrcaSlicer worker unavailable/)).not.toBeInTheDocument();
    expect(screen.queryByText('No profiles match your filters.')).not.toBeInTheDocument();
  });
});
