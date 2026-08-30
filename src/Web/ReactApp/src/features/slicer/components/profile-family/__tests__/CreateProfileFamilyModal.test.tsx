import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateProfileFamilyModal } from '../CreateProfileFamilyModal';
import {
  slicerProfilesService,
  type CloneProfileFamilyResponse,
  type OrcaProcessProfile,
} from '@/services/slicerProfilesService';
import { toast } from 'sonner';

function createProcessProfiles(machineName: string, count: number): OrcaProcessProfile[] {
  return Array.from({ length: count }, (_, index) => ({
    name: `${(index + 1) * 0.04}mm Voron process ${index + 1}`,
    quality: 'Standard',
    layerHeight: (index + 1) * 0.04,
    infillPercentage: 15,
    printSpeed: 60,
    supports: false,
    compatible_printers: [machineName],
  }));
}

const voronPointTwo = 'Voron 2.4 250 0.2 nozzle';
const voronPointFour = 'Voron 2.4 250 0.4 nozzle';
const voronPointSix = 'Voron 2.4 250 0.6 nozzle';

const mockWorkerHierarchy = {
  byHierarchy: {
    Voron: {
      name: 'Voron',
      models: {
        'Voron 2.4 250': {
          name: 'Voron 2.4 250',
          machineProfiles: [
            {
              name: voronPointTwo,
              manufacturer: 'Voron',
              nozzleDiameter: 0.2,
              printerModel: 'Voron 2.4 250',
              settings: {
                printable_area: '0x0,250x0,250x250,0x250',
                printable_height: 250,
              },
            },
            {
              name: voronPointFour,
              manufacturer: 'Voron',
              nozzleDiameter: 0.4,
              printerModel: 'Voron 2.4 250',
              settings: {
                printable_area: '0x0,250x0,250x250,0x250',
                printable_height: 250,
              },
            },
            {
              name: voronPointSix,
              manufacturer: 'Voron',
              nozzleDiameter: 0.6,
              printerModel: 'Voron 2.4 250',
              settings: {
                printable_area: '0x0,250x0,250x250,0x250',
                printable_height: 250,
              },
            },
          ],
          filamentProfiles: [
            {
              name: 'Generic PLA @Orca',
              material: 'PLA',
              manufacturer: 'OrcaFilamentLibrary',
              nozzleTemperature: 215,
              bedTemperature: 60,
              printSpeed: 60,
              compatible_printers: [voronPointTwo, voronPointFour, voronPointSix],
            },
          ],
          processProfiles: [
            ...createProcessProfiles(voronPointTwo, 5),
            ...createProcessProfiles(voronPointFour, 6),
            ...createProcessProfiles(voronPointSix, 6),
          ],
        },
      },
    },
    Prusa: {
      name: 'Prusa',
      models: {
        MK4: {
          name: 'MK4',
          machineProfiles: [
            {
              name: 'Prusa MK4 0.4 nozzle',
              manufacturer: 'Prusa',
              nozzleDiameter: 0.4,
              printerModel: 'MK4',
            },
          ],
          filamentProfiles: [],
          processProfiles: [],
        },
      },
    },
  },
};

const successResponse: CloneProfileFamilyResponse = {
  familyId: 'family-1',
  familyName: 'My Voron Family',
  targetPrinterModelId: 'target-model-1',
  renderStatus: 'Healthy',
  lastRenderedAt: '2026-08-26T00:00:00Z',
  machineProfiles: [
    { id: 'machine-1', name: 'My Voron Family 0.4 nozzle', nozzleDiameter: 0.4, sourceSystemPresetName: 'Voron 2.4 250 0.4 nozzle' },
    { id: 'machine-2', name: 'My Voron Family 0.6 nozzle', nozzleDiameter: 0.6, sourceSystemPresetName: 'Voron 2.4 250 0.6 nozzle' },
  ],
  processProfileCount: 1,
  filamentProfileCount: 1,
};

vi.mock('@/services/slicerProfilesService', () => ({
  slicerProfilesService: {
    getWorkerHierarchy: vi.fn(),
    listCustomProfiles: vi.fn(),
    cloneFamily: vi.fn(),
  },
}));

vi.mock('sonner', () => ({
  toast: {
    success: vi.fn(),
    error: vi.fn(),
    info: vi.fn(),
  },
}));

function createTestQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
}

function restoreOwnProperty(target: object, property: PropertyKey, descriptor: PropertyDescriptor | undefined) {
  if (descriptor) {
    Object.defineProperty(target, property, descriptor);
  } else if (!Reflect.deleteProperty(target, property)) {
    throw new Error(`Failed to restore inherited property ${String(property)}`);
  }
}

function renderModal(queryClient = createTestQueryClient(), onSuccess = vi.fn(), onClose = vi.fn()) {
  const result = render(
    <QueryClientProvider client={queryClient}>
      <CreateProfileFamilyModal
        isOpen
        onClose={onClose}
        targetPrinterModelId="target-model-1"
        targetPrinterModelName="Fantastic Doodle"
        defaultNozzleDiameter={0.4}
        slicerEngineVersion="2.4.2"
        onSuccess={onSuccess}
      />
    </QueryClientProvider>
  );
  return { ...result, queryClient, onSuccess, onClose };
}

async function selectVoronSource(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: /Voron 2.4 250/i }));
  await user.click(screen.getByRole('button', { name: 'Next' }));
}

async function fillNameAndAdvance(user: ReturnType<typeof userEvent.setup>, name = 'My Voron Family') {
  await user.type(screen.getByLabelText(/Family name/i), name);
  await user.click(screen.getByRole('button', { name: 'Next' }));
}

async function advanceToReview(user: ReturnType<typeof userEvent.setup>) {
  await selectVoronSource(user);
  await fillNameAndAdvance(user);
  await user.click(screen.getByRole('button', { name: 'Next' }));
  await user.click(screen.getByRole('button', { name: 'Next' }));
}

async function advanceToConfirm(user: ReturnType<typeof userEvent.setup>) {
  await advanceToReview(user);
  await user.click(screen.getByRole('button', { name: 'Next' }));
}

describe('CreateProfileFamilyModal', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    vi.mocked(slicerProfilesService.getWorkerHierarchy).mockResolvedValue(mockWorkerHierarchy);
    vi.mocked(slicerProfilesService.listCustomProfiles).mockResolvedValue({
      profiles: [],
      totalCount: 0,
      machineProfileCount: 0,
      processProfileCount: 0,
      filamentProfileCount: 0,
    });
    vi.mocked(slicerProfilesService.cloneFamily).mockResolvedValue(successResponse);
  });

  it('renders step 1 with grouped, searchable source models from the worker hierarchy', async () => {
    const user = userEvent.setup();
    renderModal();

    expect(await screen.findByRole('heading', { name: 'Choose source machine model' })).toBeInTheDocument();
    expect(await screen.findByRole('heading', { name: 'Voron' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Prusa' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Voron 2.4 250/i })).toHaveTextContent('3 nozzle variants: 0.2, 0.4, 0.6');

    await user.type(screen.getByLabelText('Search source models'), 'voron');
    expect(screen.getByRole('button', { name: /Voron 2.4 250/i })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /MK4/i })).not.toBeInTheDocument();
  });

  it('advances through all 6 steps and calls cloneFamily once with the expected request', async () => {
    const user = userEvent.setup();
    renderModal();

    await advanceToConfirm(user);
    await user.click(screen.getByRole('button', { name: 'Create family' }));

    await waitFor(() => expect(slicerProfilesService.cloneFamily).toHaveBeenCalledTimes(1));
    expect(slicerProfilesService.cloneFamily).toHaveBeenCalledWith({
      familyName: 'My Voron Family',
      targetPrinterModelId: 'target-model-1',
      sourceManufacturer: 'Voron',
      sourceMachineModelName: 'Voron 2.4 250',
      nozzleDiameters: [0.2, 0.4, 0.6],
      familyOverrides: {
        printable_area: '0x0,250x0,250x250,0x250',
        printable_height: 250,
      },
      slicerEngineVersion: '2.4.2',
      slicerDistribution: 'OrcaSlicer',
    });
  });

  it('estimates each nozzle process count and excludes Orca library filaments', async () => {
    const user = userEvent.setup();
    renderModal();

    await advanceToReview(user);

    const expectedProcessCounts = new Map([
      ['0.2', '5 estimated'],
      ['0.4', '6 estimated'],
      ['0.6', '6 estimated'],
    ]);
    for (const [nozzle, processCount] of expectedProcessCounts) {
      const card = screen.getByText(`My Voron Family ${nozzle} nozzle`).closest('[data-pf-card]');
      expect(card).not.toBeNull();
      expect(within(card as HTMLElement).getByText(processCount)).toBeInTheDocument();
      expect(within(card as HTMLElement).getByText('0 estimated')).toBeInTheDocument();
    }
  });

  it('fires success callback and invalidates the required query keys on 201 success', async () => {
    const user = userEvent.setup();
    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');
    const onSuccess = vi.fn();
    renderModal(queryClient, onSuccess);

    await advanceToConfirm(user);
    await user.click(screen.getByRole('button', { name: 'Create family' }));

    await waitFor(() => expect(onSuccess).toHaveBeenCalledWith(successResponse));
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['customProfiles'] });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['machineProfilesForModel', 'target-model-1', '2.4.2'] });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['slicerProfilesExtended'] });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['slicerProfilesHierarchy'] });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['slicerProfilesWorkerHierarchy'] });
    expect(toast.success).toHaveBeenCalledWith(expect.stringContaining("Family 'My Voron Family' created"));
  });

  it('shows a 422 source_preset_unavailable detail verbatim on the review step', async () => {
    const user = userEvent.setup();
    vi.mocked(slicerProfilesService.cloneFamily).mockRejectedValue({
      statusCode: 422,
      message: 'Missing bundle',
      data: { code: 'source_preset_unavailable', detail: 'Missing parent P' },
    });
    renderModal();

    await advanceToConfirm(user);
    await user.click(screen.getByRole('button', { name: 'Create family' }));

    expect(await screen.findByRole('heading', { name: 'Review generated variants' })).toBeInTheDocument();
    expect(screen.getByText('Missing parent P')).toBeInTheDocument();
  });

  it('returns to step 2 with an inline name error on 409 profile_family_name_conflict', async () => {
    const user = userEvent.setup();
    vi.mocked(slicerProfilesService.cloneFamily).mockRejectedValue({
      statusCode: 409,
      message: 'Conflict',
      data: { code: 'profile_family_name_conflict', detail: 'Family name already exists.' },
    });
    renderModal();

    await advanceToConfirm(user);
    await user.click(screen.getByRole('button', { name: 'Create family' }));

    expect(await screen.findByRole('heading', { name: 'Name family and confirm target' })).toBeInTheDocument();
    expect(screen.getByText('Family name already exists.')).toBeInTheDocument();
    expect(screen.getByLabelText(/Family name/i)).toHaveAttribute('aria-invalid', 'true');
  });

  it('rejects identity keys in advanced overrides and disables Next', async () => {
    const user = userEvent.setup();
    renderModal();
    await selectVoronSource(user);
    await fillNameAndAdvance(user);
    await user.click(screen.getByRole('button', { name: 'Next' }));

    await user.click(screen.getByText('Add advanced override'));
    await user.click(screen.getByRole('button', { name: 'Add override' }));
    const row = screen.getByPlaceholderText('slow_down_layer_time').closest('div');
    expect(row).not.toBeNull();
    await user.type(screen.getByLabelText('Orca key'), 'nozzle_diameter');

    expect(screen.getByText('“nozzle_diameter” is an identity key and cannot be sent.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });

  it('adds advanced overrides when crypto.randomUUID is unavailable', async () => {
    const originalRandomUUIDDescriptor = Object.getOwnPropertyDescriptor(crypto, 'randomUUID');
    Object.defineProperty(crypto, 'randomUUID', {
      value: undefined,
      configurable: true,
      writable: true,
    });
    let fillValue = 0x10;
    const getRandomValuesSpy = vi.spyOn(crypto, 'getRandomValues').mockImplementation((array) => {
      new Uint8Array(array.buffer, array.byteOffset, array.byteLength).fill(++fillValue);
      return array;
    });

    try {
      const user = userEvent.setup();
      renderModal();
      await selectVoronSource(user);
      await fillNameAndAdvance(user);
      await user.click(screen.getByRole('button', { name: 'Next' }));
      await user.click(screen.getByText('Add advanced override'));

      await user.click(screen.getByRole('button', { name: 'Add override' }));
      await user.click(screen.getByRole('button', { name: 'Add override' }));

      const keyInputs = screen.getAllByLabelText('Orca key');
      expect(keyInputs).toHaveLength(2);
      expect(keyInputs[0]).toHaveAttribute('id', 'advanced-key-11111111-1111-4111-9111-111111111111');
      expect(keyInputs[1]).toHaveAttribute('id', 'advanced-key-12121212-1212-4212-9212-121212121212');
    } finally {
      getRandomValuesSpy.mockRestore();
      restoreOwnProperty(crypto, 'randomUUID', originalRandomUUIDDescriptor);
    }

    expect(Object.getOwnPropertyDescriptor(crypto, 'randomUUID')).toEqual(originalRandomUUIDDescriptor);
  });

  it('disables Next when the nozzle selection is empty on step 3', async () => {
    const user = userEvent.setup();
    renderModal();
    await selectVoronSource(user);
    await fillNameAndAdvance(user);

    await user.click(screen.getByRole('button', { name: 'None' }));
    expect(screen.getByText('Select at least one nozzle size to continue.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });
});
