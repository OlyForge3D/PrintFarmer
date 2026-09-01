import type { PropsWithChildren } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useMachineCompatibleProfiles } from '@/features/slicer/hooks/useMachineCompatibleProfiles';
import {
  slicerProfilesService,
  type OrcaFilamentProfile,
  type OrcaProcessProfile,
} from '@/services/slicerProfilesService';

const machineNames = ['PrintersForAnts Micron 180 0.4 nozzle'];

const summaryProcess: OrcaProcessProfile = {
  name: '0.20mm Standard @Micron 180',
  quality: 'Standard',
  layerHeight: 0.2,
  infillPercentage: 15,
  printSpeed: 60,
  supports: false,
};

const fullProcess: OrcaProcessProfile = {
  ...summaryProcess,
  settings: { filament_cost: 24.99 },
};

const summaryFilament: OrcaFilamentProfile = {
  name: 'Generic PLA @Micron 180',
  material: 'PLA',
  nozzleTemperature: 215,
  bedTemperature: 60,
  printSpeed: 60,
};

const fullFilament: OrcaFilamentProfile = {
  ...summaryFilament,
  settings: { start_gcode: 'M109 S215' },
};

describe('useMachineCompatibleProfiles cache isolation', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('keeps full settings after summary responses populate the same shared cache', async () => {
    vi.spyOn(slicerProfilesService, 'getProcessProfilesForMachines')
      .mockImplementation(async (_names, _engineVersion, view) => (
        view === 'summary' ? [summaryProcess] : [fullProcess]
      ));
    vi.spyOn(slicerProfilesService, 'getFilamentProfilesForMachines')
      .mockImplementation(async (_names, _engineVersion, view) => (
        view === 'summary' ? [summaryFilament] : [fullFilament]
      ));

    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
      },
    });
    const wrapper = ({ children }: PropsWithChildren) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    );

    const summaryResult = renderHook(
      () => useMachineCompatibleProfiles(machineNames, { enabled: true, summary: true }),
      { wrapper },
    );
    await waitFor(() => {
      expect(summaryResult.result.current.processProfilesQuery.data?.[0].settings).toBeUndefined();
      expect(summaryResult.result.current.filamentProfilesQuery.data?.[0].settings).toBeUndefined();
    });
    summaryResult.unmount();

    const fullResult = renderHook(
      () => useMachineCompatibleProfiles(machineNames, { enabled: true }),
      { wrapper },
    );
    await waitFor(() => {
      expect(fullResult.result.current.processProfilesQuery.data?.[0].settings)
        .toEqual({ filament_cost: 24.99 });
      expect(fullResult.result.current.filamentProfilesQuery.data?.[0].settings)
        .toEqual({ start_gcode: 'M109 S215' });
    });
  });
});
