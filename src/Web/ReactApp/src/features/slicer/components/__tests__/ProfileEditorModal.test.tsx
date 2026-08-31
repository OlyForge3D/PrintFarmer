import '@testing-library/jest-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ProfileEditorModal } from '@/features/slicer/components/ProfileEditorModal';
import {
  slicerProfilesService,
  type OrcaMachineProfile,
} from '@/services/slicerProfilesService';

vi.mock('@/services/slicerProfilesService', () => ({
  slicerProfilesService: {
    uploadProfile: vi.fn(),
  },
}));

function renderMachineEditor(profile: OrcaMachineProfile) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <ProfileEditorModal
        isOpen
        onClose={vi.fn()}
        profileType="machine"
        originalProfile={profile}
      />
    </QueryClientProvider>,
  );
}

describe('ProfileEditorModal machine profiles', () => {
  beforeEach(() => {
    localStorage.setItem('printfarmer-slicer-viewmode', 'simple');
    vi.clearAllMocks();
    vi.mocked(slicerProfilesService.uploadProfile).mockResolvedValue({
      id: 'custom-profile-id',
      name: 'Custom machine',
      profileType: 'machine',
      isSystem: false,
      createdAt: '2026-08-31T00:00:00Z',
      updatedAt: '2026-08-31T00:00:00Z',
    });
  });

  it('hydrates promoted values into friendly Simple-mode controls and reset clears dirty state', async () => {
    const user = userEvent.setup();
    renderMachineEditor({
      name: 'CoreXY',
      manufacturer: 'Test',
      nozzleDiameter: 0.4,
      maxHotendTemperature: 300,
      settings: { raw_only_setting: 'keep' },
    });

    expect(screen.getByRole('spinbutton', { name: 'Maximum hotend temperature' }))
      .toHaveValue(300);

    await user.click(screen.getByRole('tab', { name: 'Extruder' }));
    const nozzleInput = screen.getByRole('spinbutton', { name: 'Nozzle diameter' });
    const saveAsButton = screen.getByRole('button', { name: 'Save as Custom Profile' });
    expect(nozzleInput).toHaveValue(0.4);
    expect(saveAsButton).toBeDisabled();

    fireEvent.change(nozzleInput, { target: { value: '0.6' } });
    expect(saveAsButton).toBeEnabled();
    expect(screen.getByText('Settings modified')).toBeInTheDocument();

    await user.click(screen.getByRole('button', {
      name: 'Reset Nozzle diameter to original value',
    }));
    expect(nozzleInput).toHaveValue(0.4);
    expect(saveAsButton).toBeDisabled();
    expect(screen.queryByText('Settings modified')).not.toBeInTheDocument();
  });

  it('saves edited fields under Orca keys while preserving unrelated raw settings', async () => {
    const user = userEvent.setup();
    renderMachineEditor({
      name: 'CoreXY',
      manufacturer: 'Test',
      nozzleDiameter: 0.4,
      settings: {
        raw_only_setting: { nested: ['keep'] },
      },
    });

    await user.click(screen.getByRole('tab', { name: 'Extruder' }));
    fireEvent.change(
      screen.getByRole('spinbutton', { name: 'Nozzle diameter' }),
      { target: { value: '0.6' } },
    );
    await user.click(screen.getByRole('button', { name: 'Save as Custom Profile' }));
    await user.clear(screen.getByRole('textbox', { name: 'Custom Profile Name' }));
    await user.type(
      screen.getByRole('textbox', { name: 'Custom Profile Name' }),
      'My CoreXY',
    );
    await user.click(screen.getByRole('button', { name: 'Save', exact: true }));

    await waitFor(() => expect(slicerProfilesService.uploadProfile).toHaveBeenCalledOnce());
    const request = vi.mocked(slicerProfilesService.uploadProfile).mock.calls[0][0];
    expect(request.name).toBe('My CoreXY');
    expect(JSON.parse(request.rawJson)).toEqual({
      raw_only_setting: { nested: ['keep'] },
      nozzle_diameter: ['0.6'],
    });
  });

  it('keeps an absent promoted value out of an unrelated save', async () => {
    const user = userEvent.setup();
    renderMachineEditor({
      name: 'CoreXY',
      manufacturer: 'Test',
      maxHotendTemperature: 300,
      settings: { machine_start_gcode: 'G28' },
    });

    await user.click(screen.getByRole('tab', { name: 'Machine G-code' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Start G-code' }), {
      target: { value: 'G28\nM117 Ready' },
    });
    await user.click(screen.getByRole('button', { name: 'Save as Custom Profile' }));
    await user.click(screen.getByRole('button', { name: 'Save', exact: true }));

    await waitFor(() => expect(slicerProfilesService.uploadProfile).toHaveBeenCalledOnce());
    const request = vi.mocked(slicerProfilesService.uploadProfile).mock.calls[0][0];
    expect(JSON.parse(request.rawJson)).toEqual({
      machine_start_gcode: 'G28\nM117 Ready',
    });
  });

  it('marks an initially absent field modified and reset removes its ownership', async () => {
    const user = userEvent.setup();
    renderMachineEditor({
      name: 'CoreXY',
      manufacturer: 'Test',
      settings: {},
    });

    await user.click(screen.getByRole('tab', { name: 'Machine G-code' }));
    const startGcode = screen.getByRole('textbox', { name: 'Start G-code' });
    const saveAsButton = screen.getByRole('button', { name: 'Save as Custom Profile' });
    fireEvent.change(startGcode, { target: { value: 'G28' } });

    expect(saveAsButton).toBeEnabled();
    expect(screen.getByText('Settings modified')).toBeInTheDocument();

    await user.click(screen.getByRole('button', {
      name: 'Reset Start G-code to original value',
    }));
    expect(startGcode).toHaveValue('G28 ; home all axes\\nG1 Z5 F5000 ; lift nozzle\\n');
    expect(saveAsButton).toBeDisabled();
    expect(screen.queryByText('Settings modified')).not.toBeInTheDocument();
  });

  it('shows common G-code in Simple mode without writing untouched absent settings', async () => {
    const user = userEvent.setup();
    renderMachineEditor({
      name: 'CoreXY',
      manufacturer: 'Test',
      settings: {
        machine_start_gcode: 'G28',
        raw_only_setting: 'keep',
      },
    });

    await user.click(screen.getByRole('tab', { name: 'Machine G-code' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Start G-code' }), {
      target: { value: 'G28\nM117 Ready' },
    });
    await user.click(screen.getByRole('button', { name: 'Save as Custom Profile' }));
    await user.click(screen.getByRole('button', { name: 'Save', exact: true }));

    await waitFor(() => expect(slicerProfilesService.uploadProfile).toHaveBeenCalledOnce());
    const request = vi.mocked(slicerProfilesService.uploadProfile).mock.calls[0][0];
    expect(JSON.parse(request.rawJson)).toEqual({
      machine_start_gcode: 'G28\nM117 Ready',
      raw_only_setting: 'keep',
    });
  });
});
