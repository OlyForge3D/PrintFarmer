import '@testing-library/jest-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ProfileEditorModal } from '@/features/slicer/components/ProfileEditorModal';
import {
  slicerProfilesService,
  type OrcaFilamentProfile,
  type OrcaMachineProfile,
} from '@/services/slicerProfilesService';

vi.mock('@/services/slicerProfilesService', () => ({
  slicerProfilesService: {
    uploadProfile: vi.fn(),
  },
}));

function renderEditor(
  profileType: 'machine' | 'filament',
  profile: OrcaMachineProfile | OrcaFilamentProfile,
) {
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
        profileType={profileType}
        originalProfile={profile}
      />
    </QueryClientProvider>,
  );
}

function renderMachineEditor(profile: OrcaMachineProfile) {
  return renderEditor('machine', profile);
}

describe('ProfileEditorModal filament profiles', () => {
  beforeEach(() => {
    localStorage.setItem('printfarmer-slicer-viewmode', 'advanced');
    vi.clearAllMocks();
    vi.mocked(slicerProfilesService.uploadProfile).mockResolvedValue({
      id: 'custom-filament-id',
      name: 'Custom filament',
      profileType: 'filament',
      isSystem: false,
      createdAt: '2026-08-31T00:00:00Z',
      updatedAt: '2026-08-31T00:00:00Z',
    });
  });

  function renderFilamentEditor(settings: Record<string, unknown>) {
    renderEditor('filament', {
      name: 'Test PLA', material: 'PLA',
      nozzleTemperature: 200, bedTemperature: 60, printSpeed: 50, settings,
    });
  }

  it('hydrates native arrays and uploads edited temperature, boolean, percent and G-code vectors', async () => {
    const user = userEvent.setup();
    renderFilamentEditor({
      nozzle_temperature: ['200', '215'],
      filament_soluble: ['1', '0'],
      filament_shrink: ['100%', '99%'],
      filament_start_gcode: ['M117 X,Y\nG1 X1,Y2', 'M117 E2'],
      raw_only_setting: { nested: ['keep'] },
    });

    const soluble = screen.getByRole('checkbox', { name: 'Soluble material' });
    expect(soluble).toBeChecked();
    await user.click(soluble);
    const shrink = screen.getByRole('spinbutton', { name: 'Shrinkage (XY)' });
    expect(shrink).toHaveValue(100);
    fireEvent.change(shrink, { target: { value: '98.5' } });
    const temperature = screen.getByRole('spinbutton', { name: 'First layer / Other layers Other layers' });
    expect(temperature).toHaveValue(200);
    fireEvent.change(temperature, { target: { value: '221' } });

    await user.click(screen.getByRole('tab', { name: 'Advanced' }));
    const startGcode = screen.getByRole('textbox', { name: 'Start G-code' });
    expect(startGcode).toHaveValue('M117 X,Y\nG1 X1,Y2');
    fireEvent.change(startGcode, { target: { value: 'M117 NEW,X\nG1 Y2' } });
    await user.click(screen.getByRole('button', { name: 'Save as Custom Profile' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(slicerProfilesService.uploadProfile).toHaveBeenCalledOnce());
    const request = vi.mocked(slicerProfilesService.uploadProfile).mock.calls[0][0];
    expect(JSON.parse(request.rawJson)).toEqual({
      nozzle_temperature: ['221', '215'],
      filament_soluble: ['0', '0'],
      filament_shrink: ['98.5%', '99%'],
      filament_start_gcode: ['M117 NEW,X\nG1 Y2', 'M117 E2'],
      raw_only_setting: { nested: ['keep'] },
    });
  });

  it('keeps coFloats multi-extruder controls and serializes both edited entries', async () => {
    const user = userEvent.setup();
    renderFilamentEditor({ filament_flow_ratio: ['0.98', '0.97'] });
    const first = screen.getByRole('spinbutton', { name: 'Flow ratio Extruder 1' });
    const second = screen.getByRole('spinbutton', { name: 'Flow ratio Extruder 2' });
    expect(first).toHaveValue(0.98);
    expect(second).toHaveValue(0.97);
    fireEvent.change(second, { target: { value: '0.96' } });
    await user.click(screen.getByRole('button', { name: 'Save as Custom Profile' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(slicerProfilesService.uploadProfile).toHaveBeenCalledOnce());
    expect(JSON.parse(vi.mocked(slicerProfilesService.uploadProfile).mock.calls[0][0].rawJson))
      .toEqual({ filament_flow_ratio: ['0.98', '0.96'] });
  });

  it('resets an absent boolean without adding it to an unrelated save', async () => {
    const user = userEvent.setup();
    renderFilamentEditor({ nozzle_temperature: ['200', '215'] });
    await user.click(screen.getByRole('checkbox', { name: 'Soluble material' }));
    await user.click(screen.getByRole('button', {
      name: 'Reset Soluble material to original value',
    }));
    expect(screen.getByRole('button', { name: 'Save as Custom Profile' })).toBeDisabled();
    fireEvent.change(screen.getByRole('spinbutton', { name: 'First layer / Other layers Other layers' }), {
      target: { value: '221' },
    });
    await user.click(screen.getByRole('button', { name: 'Save as Custom Profile' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(slicerProfilesService.uploadProfile).toHaveBeenCalledOnce());
    expect(JSON.parse(vi.mocked(slicerProfilesService.uploadProfile).mock.calls[0][0].rawJson))
      .toEqual({ nozzle_temperature: ['221', '215'] });
  });
});

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
    await user.click(screen.getByRole('button', { name: 'Save' }));

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
    await user.click(screen.getByRole('button', { name: 'Save' }));

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
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(slicerProfilesService.uploadProfile).toHaveBeenCalledOnce());
    const request = vi.mocked(slicerProfilesService.uploadProfile).mock.calls[0][0];
    expect(JSON.parse(request.rawJson)).toEqual({
      machine_start_gcode: 'G28\nM117 Ready',
      raw_only_setting: 'keep',
    });
  });
});
