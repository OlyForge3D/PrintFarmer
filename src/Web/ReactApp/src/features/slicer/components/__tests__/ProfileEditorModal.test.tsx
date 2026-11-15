import '@testing-library/jest-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ProfileEditorModal } from '@/features/slicer/components/ProfileEditorModal';
import {
  slicerProfilesService,
  type CustomProfile,
  type OrcaFilamentProfile,
  type OrcaMachineProfile,
} from '@/services/slicerProfilesService';
import { getFilamentBedContext, type FilamentBedContext } from '@/features/slicer/utils/bedSurfaceTemperature';

vi.mock('@/services/slicerProfilesService', () => ({
  slicerProfilesService: {
    uploadProfile: vi.fn(),
    updateCustomProfile: vi.fn(),
  },
}));

function renderEditor(
  profileType: 'machine' | 'filament',
  profile: OrcaMachineProfile | OrcaFilamentProfile,
  filamentBedContext?: FilamentBedContext,
  customProfile?: CustomProfile,
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
        filamentBedContext={filamentBedContext}
        customProfile={customProfile}
      />
    </QueryClientProvider>,
  );
}

function renderMachineEditor(profile: OrcaMachineProfile) {
  return renderEditor('machine', profile);
}

describe.each(['machine', 'filament'] as const)('ProfileEditorModal %s save identity', profileType => {
  const key = profileType === 'machine' ? 'machine_start_gcode' : 'filament_start_gcode';
  const raw = {
    inherits: 'Native base',
    [key]: profileType === 'machine' ? 'G28' : ['G28', 'M117 second extruder'],
    raw_only_setting: { nested: ['keep'] },
    textured_plate_temp: ['60', '55'],
  };
  const custom: CustomProfile = {
    id: `${profileType}-owned-id`, name: `My ${profileType}`, profileType, isSystem: false,
    createdAt: '2026-11-03T00:00:00Z', rawJson: JSON.stringify(raw),
    ...(profileType === 'machine' ? { printerModelId: 'model-id' } : { compatiblePrinters: ['Micron'] }),
  };
  const original = profileType === 'machine'
    ? { name: 'Wrong library source', manufacturer: 'Test', settings: {} }
    : { name: 'Wrong library source', material: 'PLA', nozzleTemperature: 200, bedTemperature: 0, printSpeed: 50, settings: {} };

  beforeEach(() => {
    localStorage.setItem('printfarmer-slicer-viewmode', 'advanced');
    vi.clearAllMocks();
    vi.mocked(slicerProfilesService.updateCustomProfile).mockResolvedValue(custom);
    vi.mocked(slicerProfilesService.uploadProfile).mockResolvedValue({ ...custom, id: 'copy-id' });
  });

  it.each([false, true])('updates the owned ID in place (edited: %s)', async edited => {
    renderEditor(profileType, original, undefined, custom);
    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled();
    fireEvent.click(screen.getByRole('tab', { name: profileType === 'machine' ? 'Machine G-code' : 'Advanced' }));
    expect(screen.getByRole('textbox', { name: 'Start G-code' })).toHaveValue('G28');
    if (edited) fireEvent.change(screen.getByRole('textbox', { name: 'Start G-code' }), { target: { value: 'G28\nM117 updated' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(slicerProfilesService.updateCustomProfile).toHaveBeenCalledOnce());
    const [id, request] = vi.mocked(slicerProfilesService.updateCustomProfile).mock.calls[0];
    expect(id).toBe(custom.id);
    expect(Object.keys(request)).toEqual(['rawJson']);
    expect(JSON.parse(request.rawJson!)).toEqual({
      ...raw,
      [key]: edited ? (profileType === 'machine' ? 'G28\nM117 updated' : ['G28\nM117 updated', 'M117 second extruder']) : raw[key],
    });
    expect(slicerProfilesService.uploadProfile).not.toHaveBeenCalled();
  });

  it('creates an unchanged named copy with associations and rejects a blank name', async () => {
    renderEditor(profileType, original, undefined, custom);
    fireEvent.click(screen.getByRole('button', { name: 'Save as' }));
    const name = screen.getByRole('textbox', { name: 'Custom Profile Name' });
    fireEvent.change(name, { target: { value: '   ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(screen.getByText('Profile name is required')).toBeInTheDocument();
    expect(slicerProfilesService.uploadProfile).not.toHaveBeenCalled();
    fireEvent.change(name, { target: { value: '  My new copy  ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(slicerProfilesService.uploadProfile).toHaveBeenCalledOnce());
    const request = vi.mocked(slicerProfilesService.uploadProfile).mock.calls[0][0];
    expect(request).toMatchObject({
      name: 'My new copy', profileType,
      ...(profileType === 'machine' ? { printerModelId: 'model-id' } : { compatiblePrinters: ['Micron'] }),
    });
    expect(JSON.parse(request.rawJson)).toEqual(raw);
    expect(slicerProfilesService.updateCustomProfile).not.toHaveBeenCalled();
  });

  it('retains the editor and changes when an in-place save fails', async () => {
    vi.mocked(slicerProfilesService.updateCustomProfile).mockRejectedValueOnce(new Error('Update rejected'));
    renderEditor(profileType, original, undefined, custom);
    fireEvent.click(screen.getByRole('tab', { name: profileType === 'machine' ? 'Machine G-code' : 'Advanced' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Start G-code' }), { target: { value: 'M117 retain me' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Update rejected')).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Start G-code' })).toHaveValue('M117 retain me');
    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled();
    expect(slicerProfilesService.uploadProfile).not.toHaveBeenCalled();
  });

  it('copies unchanged system profiles without issuing an update', async () => {
    renderEditor(profileType, original);
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(slicerProfilesService.uploadProfile).not.toHaveBeenCalled();
    expect(screen.getByRole('textbox', { name: 'Custom Profile Name' })).toHaveValue('Wrong library source (Custom)');
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(slicerProfilesService.uploadProfile).toHaveBeenCalledOnce());
    expect(slicerProfilesService.updateCustomProfile).not.toHaveBeenCalled();
  });

  it('reports invalid owned JSON and never saves it', async () => {
    renderEditor(profileType, original, undefined, { ...custom, rawJson: '[]' });
    expect(await screen.findByText(/Cannot load custom profile/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(slicerProfilesService.updateCustomProfile).not.toHaveBeenCalled();
    expect(slicerProfilesService.uploadProfile).not.toHaveBeenCalled();
  });

  it('never updates an explicitly system-owned database profile', async () => {
    renderEditor(profileType, original, undefined, { ...custom, isSystem: true });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(screen.getByRole('textbox', { name: 'Custom Profile Name' })).toHaveValue(`${custom.name} (Custom)`);
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(slicerProfilesService.uploadProfile).toHaveBeenCalledOnce());
    expect(slicerProfilesService.updateCustomProfile).not.toHaveBeenCalled();
  });
});

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

  it('shows only the active textured pair for a single-bed machine and preserves all hidden settings when saving', async () => {
    const raw = {
      inherits: 'Micron filament base',
      hot_plate_temp_initial_layer: ['0', '0'], hot_plate_temp: ['0', '0'],
      textured_plate_temp_initial_layer: ['65', '61'], textured_plate_temp: ['60', '56'],
      cool_plate_temp_initial_layer: ['35'], cool_plate_temp: ['30'],
    };
    renderEditor('filament', {
      name: 'Micron PLA', material: 'PLA', nozzleTemperature: 210, bedTemperature: 0, printSpeed: 60, settings: raw,
    }, { supportMultiBedTypes: false, bedType: 'Textured PEI Plate' });
    expect(screen.getByTitle('Textured Plate first layer')).toHaveValue(65);
    expect(screen.getByTitle('Textured Plate other layers')).toHaveValue(60);
    expect(screen.queryByTitle('Smooth PEI / High Temp Plate first layer')).not.toBeInTheDocument();
    expect(screen.queryByTitle('Cool Plate first layer')).not.toBeInTheDocument();
    expect(screen.getByText('Bed surface: Textured PEI Plate. Hidden surface values are preserved.')).toBeInTheDocument();
    fireEvent.change(screen.getByTitle('Textured Plate first layer'), { target: { value: '67' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(slicerProfilesService.uploadProfile).toHaveBeenCalledOnce());
    expect(JSON.parse(vi.mocked(slicerProfilesService.uploadProfile).mock.calls[0][0].rawJson))
      .toEqual({ ...raw, textured_plate_temp_initial_layer: ['67', '61'] });
  });

  it.each([true, undefined])('shows every native bed pair for multi-bed/unknown support %s', supportMultiBedTypes => {
    renderEditor('filament', {
      name: 'PLA', material: 'PLA', nozzleTemperature: 210, bedTemperature: 0, printSpeed: 60, settings: {},
    }, { supportMultiBedTypes, bedType: 'Textured PEI Plate' });
    expect(screen.getByTitle('Textured Plate first layer')).toBeInTheDocument();
    expect(screen.getByTitle('Smooth PEI / High Temp Plate first layer')).toBeInTheDocument();
    expect(screen.getByTitle('Cool Plate first layer')).toBeInTheDocument();
    expect(screen.queryByText(/Hidden surface values are preserved/)).not.toBeInTheDocument();
  });

  it.each([
    { manufacturer: 'BBL', printer_model: 'Bambu Lab X1 Carbon' },
    { manufacturer: 'Bambu', printer_model: 'Bambu Lab P1S' },
    { manufacturer: '', printer_model: ' Bambu Lab A1' },
  ])('shows all bed pairs for Bambu machine context %j', machine => {
    renderEditor('filament', {
      name: 'PLA', material: 'PLA', nozzleTemperature: 210, bedTemperature: 0, printSpeed: 60, settings: {},
    }, getFilamentBedContext({
      ...machine, support_multi_bed_types: '0', curr_bed_type: 'Textured PEI Plate',
    }, {}, '', machine.manufacturer));
    for (const plate of ['Cool Plate', 'Engineering Plate', 'Smooth PEI / High Temp Plate', 'Textured Plate']) {
      expect(screen.getByTitle(`${plate} first layer`)).toBeInTheDocument();
      expect(screen.getByTitle(`${plate} other layers`)).toBeInTheDocument();
    }
    expect(screen.getByText('Bed surface: Textured PEI Plate. Showing all bed-surface settings.')).toBeInTheDocument();
  });

  it('explains why all surfaces are shown without selected machine context', () => {
    renderFilamentEditor({});
    expect(screen.getByText('No selected machine context. Showing all bed-surface settings.')).toBeInTheDocument();
    expect(screen.getByTitle('Smooth PEI / High Temp Plate first layer')).toBeInTheDocument();
    expect(screen.getByTitle('Textured Plate first layer')).toBeInTheDocument();
  });

  it('retains edited hidden surfaces when machine context toggles between multi-bed and single-bed', async () => {
    const profile: OrcaFilamentProfile = {
      name: 'Micron PLA', material: 'PLA', nozzleTemperature: 210, bedTemperature: 0, printSpeed: 60,
      settings: {
        inherits: 'Micron base',
        hot_plate_temp_initial_layer: ['0', '0'], hot_plate_temp: ['0', '0'],
        textured_plate_temp_initial_layer: ['65', '61'], textured_plate_temp: ['60', '56'],
      },
    };
    const queryClient = new QueryClient();
    const editor = (supportMultiBedTypes: boolean) => (
      <QueryClientProvider client={queryClient}>
        <ProfileEditorModal isOpen onClose={vi.fn()} profileType="filament" originalProfile={profile}
          filamentBedContext={{ supportMultiBedTypes, bedType: 'Textured PEI Plate' }} />
      </QueryClientProvider>
    );
    const { rerender } = render(editor(true));
    fireEvent.change(screen.getByTitle('Smooth PEI / High Temp Plate other layers'), { target: { value: '42' } });
    rerender(editor(false));
    expect(screen.queryByTitle('Smooth PEI / High Temp Plate other layers')).not.toBeInTheDocument();
    fireEvent.change(screen.getByTitle('Textured Plate other layers'), { target: { value: '62' } });
    rerender(editor(true));
    expect(screen.getByTitle('Smooth PEI / High Temp Plate other layers')).toHaveValue(42);
    expect(screen.getByTitle('Textured Plate other layers')).toHaveValue(62);
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(slicerProfilesService.uploadProfile).toHaveBeenCalledOnce());
    expect(JSON.parse(vi.mocked(slicerProfilesService.uploadProfile).mock.calls[0][0].rawJson)).toEqual({
      ...profile.settings, hot_plate_temp: ['42', '0'], textured_plate_temp: ['62', '56'],
    });
  });

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
    await user.click(screen.getByRole('button', { name: 'Save' }));
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
    await user.click(screen.getByRole('button', { name: 'Save' }));
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
    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled();
    fireEvent.change(screen.getByRole('spinbutton', { name: 'First layer / Other layers Other layers' }), {
      target: { value: '221' },
    });
    await user.click(screen.getByRole('button', { name: 'Save' }));
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
    const saveAsButton = screen.getByRole('button', { name: 'Save' });
    expect(nozzleInput).toHaveValue(0.4);
    expect(saveAsButton).toBeEnabled();

    fireEvent.change(nozzleInput, { target: { value: '0.6' } });
    expect(saveAsButton).toBeEnabled();
    expect(screen.getByText('Settings modified')).toBeInTheDocument();

    await user.click(screen.getByRole('button', {
      name: 'Reset Nozzle diameter to original value',
    }));
    expect(nozzleInput).toHaveValue(0.4);
    expect(saveAsButton).toBeEnabled();
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
    await user.click(screen.getByRole('button', { name: 'Save' }));
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
    await user.click(screen.getByRole('button', { name: 'Save' }));
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
    const saveAsButton = screen.getByRole('button', { name: 'Save' });
    fireEvent.change(startGcode, { target: { value: 'G28' } });

    expect(saveAsButton).toBeEnabled();
    expect(screen.getByText('Settings modified')).toBeInTheDocument();

    await user.click(screen.getByRole('button', {
      name: 'Reset Start G-code to original value',
    }));
    expect(startGcode).toHaveValue('G28 ; home all axes\\nG1 Z5 F5000 ; lift nozzle\\n');
    expect(saveAsButton).toBeEnabled();
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
    await user.click(screen.getByRole('button', { name: 'Save' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(slicerProfilesService.uploadProfile).toHaveBeenCalledOnce());
    const request = vi.mocked(slicerProfilesService.uploadProfile).mock.calls[0][0];
    expect(JSON.parse(request.rawJson)).toEqual({
      machine_start_gcode: 'G28\nM117 Ready',
      raw_only_setting: 'keep',
    });
  });
});
