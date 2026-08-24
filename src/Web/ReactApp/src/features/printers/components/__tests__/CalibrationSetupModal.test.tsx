import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CalibrationSetupModal } from '../CalibrationSetupModal';
import type { CalibrationContextDto } from '@/types/api';

const mockGetCalibrationContext = vi.fn();
const mockUpdateCalibrationSetup = vi.fn();
const mockDetectPrinterFirmware = vi.fn();
const mockListExtended = vi.fn();

vi.mock('@/services/slicerProfilesService', () => ({
  slicerProfilesService: {
    listExtended: (...args: unknown[]) => mockListExtended(...args),
  },
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getCalibrationContext: (...args: unknown[]) => mockGetCalibrationContext(...args),
    updateCalibrationSetup: (...args: unknown[]) => mockUpdateCalibrationSetup(...args),
    detectPrinterFirmware: (...args: unknown[]) => mockDetectPrinterFirmware(...args),
  },
}));

vi.mock('@/common/components/modals/Modal', () => ({
  Modal: ({
    isOpen,
    title,
    footer,
    children,
  }: {
    isOpen: boolean;
    title: string;
    footer?: React.ReactNode;
    children: React.ReactNode;
  }) =>
    isOpen ? (
      <div data-testid="modal">
        <h1>{title}</h1>
        {children}
        {footer}
      </div>
    ) : null,
}));

function createContext(overrides: Partial<CalibrationContextDto> = {}): CalibrationContextDto {
  return {
    id: 'printer-1',
    eligible: false,
    missingInputs: ['calibrationHardwareVerifiedAtUtc'],
    rejectionReasons: [],
    activeToolheadIndex: 0,
    excludedRegions: [],
    supportsPressureAdvance: false,
    supportsFirmwareRetraction: false,
    calibrationHardwareVerifiedAtUtc: null,
    firmware: {
      family: 'Klipper',
      gcodeDialect: 'Marlin',
      detectionSource: 'profile',
      version: '1.2.3',
      detectionVersion: null,
      detectionConfidence: null,
      detectedAtUtc: null,
      verified: false,
    },
    toolheads: [
      {
        id: 'toolhead-1',
        index: 0,
        name: 'Extruder',
        isPrimary: true,
        offset: { x: 0, y: 0, z: 0 },
        nozzleDiameter: 0.4,
        nozzleType: 'brass',
        nozzleMaterial: null,
        nozzleMaxTemperature: 280,
        nozzleIsHardened: null,
        hotendMaxTemperature: 280,
        maxVolumetricFlow: null,
        driveType: null,
        isDirectDrive: null,
        extruderGearRatio: null,
        supportedMaterials: null,
      },
    ],
    ...overrides,
  } as CalibrationContextDto;
}

function renderModal(overrides: Partial<CalibrationContextDto> = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onClose = vi.fn();
  mockGetCalibrationContext.mockResolvedValue(createContext(overrides));

  return {
    onClose,
    ...render(
      <QueryClientProvider client={queryClient}>
        <CalibrationSetupModal
          isOpen
          onClose={onClose}
          printerId="printer-1"
          printerName="Test Printer"
          rowVersion="printer-v1"
        />
      </QueryClientProvider>
    ),
  };
}

describe('CalibrationSetupModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockListExtended.mockResolvedValue({
      machineProfiles: [
        { id: 'machine-1', name: 'Qidi Plus 4 0.4 nozzle', manufacturer: 'Qidi', profileType: 'machine' },
      ],
      processProfiles: [{ id: 'process-1', name: '0.2mm Standard', profileType: 'process' }],
      filamentProfiles: [{ id: 'filament-1', name: 'Generic PLA', profileType: 'filament' }],
    });
    mockUpdateCalibrationSetup.mockResolvedValue({
      printerId: 'printer-1',
      configurationRevision: 2,
      rowVersion: 'printer-v2',
      activeToolheadIndex: 0,
      excludedRegions: [],
      supportsPressureAdvance: false,
      supportsFirmwareRetraction: false,
      calibrationHardwareVerifiedAtUtc: null,
      firmware: createContext().firmware,
      toolheads: [],
    });
    mockDetectPrinterFirmware.mockResolvedValue({
      succeeded: true,
      failure: 'None',
      family: 'Klipper',
      version: 'v0.12.0-321',
      detectionConfidence: 1,
      detectedAtUtc: new Date().toISOString(),
      identityVerified: false,
    });
  });

  it('does not render when closed', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <CalibrationSetupModal
          isOpen={false}
          onClose={vi.fn()}
          printerId="printer-1"
          printerName="Test Printer"
          rowVersion="printer-v1"
        />
      </QueryClientProvider>
    );
    expect(screen.queryByTestId('modal')).not.toBeInTheDocument();
  });

  it('loads and displays calibration context, including missing inputs', async () => {
    renderModal();
    await waitFor(() => expect(mockGetCalibrationContext).toHaveBeenCalledWith('printer-1'));
    expect(await screen.findByText(/Not yet eligible/i)).toBeInTheDocument();
    expect(screen.getByText(/calibrationHardwareVerifiedAtUtc/)).toBeInTheDocument();
  });

  it('does not render any editable input for firmware family, version, or gcodeDialect — only a confirm button', async () => {
    renderModal();
    await screen.findByText('Klipper');

    // Firmware facts must appear as text, never inside an <input>/<select>.
    expect(screen.queryByDisplayValue('Klipper')).not.toBeInTheDocument();
    expect(screen.queryByDisplayValue('Marlin')).not.toBeInTheDocument();
    expect(screen.queryByDisplayValue('1.2.3')).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/firmware family/i)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/firmware version/i)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/gcode dialect/i)).not.toBeInTheDocument();

    // The firmware facts themselves stay uneditable. The two firmware controls are both
    // actions, not fields: one re-probes the printer, one attests the result.
    expect(screen.getByRole('button', { name: /mark firmware verified/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /re-probe firmware/i })).toBeInTheDocument();
  });

  it('re-probing firmware calls the detect endpoint and does not write through the setup endpoint', async () => {
    const user = userEvent.setup();
    renderModal();
    await screen.findByText('Klipper');

    await user.click(screen.getByRole('button', { name: /re-probe firmware/i }));

    await waitFor(() => expect(mockDetectPrinterFirmware).toHaveBeenCalledWith('printer-1'));

    // Detection is a probe, not an attestation. Routing it through the setup endpoint would
    // let a re-probe silently set firmwareIdentityVerified, which must stay a human act.
    expect(mockUpdateCalibrationSetup).not.toHaveBeenCalled();
  });

  it('marking firmware verified still goes to the setup endpoint, not the probe', async () => {
    const user = userEvent.setup();
    renderModal();
    await screen.findByText('Klipper');

    await user.click(screen.getByRole('button', { name: /mark firmware verified/i }));

    await waitFor(() => expect(mockUpdateCalibrationSetup).toHaveBeenCalled());

    // Control for the test above: the same modal, the adjacent button, the opposite routing.
    // Without this, "detect does not call setup" could hold simply because nothing is wired.
    expect(mockDetectPrinterFirmware).not.toHaveBeenCalled();
  });

  it('confirming firmware verified submits only firmwareIdentityVerified, no override fields', async () => {
    const user = userEvent.setup();
    renderModal();
    await screen.findByText('Klipper');

    await user.click(screen.getByRole('button', { name: /mark firmware verified/i }));

    await waitFor(() => expect(mockUpdateCalibrationSetup).toHaveBeenCalled());
    const [, request] = mockUpdateCalibrationSetup.mock.calls[0];
    expect(request).toEqual({ firmwareIdentityVerified: true });
  });

  it('marking hardware verified submits a current UTC timestamp', async () => {
    const user = userEvent.setup();
    renderModal();
    await screen.findByText(/Not verified/);

    await user.click(screen.getByRole('button', { name: /mark hardware verified now/i }));

    await waitFor(() => expect(mockUpdateCalibrationSetup).toHaveBeenCalled());
    const [printerId, request, rowVersion] = mockUpdateCalibrationSetup.mock.calls[0];
    expect(printerId).toBe('printer-1');
    expect(rowVersion).toBe('printer-v1');
    expect(typeof request.calibrationHardwareVerifiedAtUtc).toBe('string');
    expect(new Date(request.calibrationHardwareVerifiedAtUtc).toString()).not.toBe('Invalid Date');
  });

  it('saving with no excluded regions explicitly submits an empty array, not omitted', async () => {
    const user = userEvent.setup();
    renderModal({ excludedRegions: [{ name: 'old region', polygon: [{ x: 1, y: 1 }] }] });
    await screen.findByDisplayValue('old region');

    await user.click(screen.getByRole('button', { name: /remove region/i }));
    await user.click(screen.getByRole('button', { name: /save calibration setup/i }));

    await waitFor(() => expect(mockUpdateCalibrationSetup).toHaveBeenCalled());
    const [, request] = mockUpdateCalibrationSetup.mock.calls[0];
    expect(request.excludedRegions).toEqual([]);
  });

  it('adding an excluded region and saving submits the region with its point', async () => {
    const user = userEvent.setup();
    renderModal();
    await screen.findByText('Klipper');

    await user.click(screen.getByRole('button', { name: /add excluded region/i }));
    await user.type(screen.getByLabelText(/region 1 name/i), 'skirt keepout');
    await user.click(screen.getByRole('button', { name: /save calibration setup/i }));

    await waitFor(() => expect(mockUpdateCalibrationSetup).toHaveBeenCalled());
    const [, request] = mockUpdateCalibrationSetup.mock.calls[0];
    expect(request.excludedRegions).toEqual([
      { name: 'skirt keepout', polygon: [{ x: 0, y: 0 }] },
    ]);
  });

  it('edits per-toolhead metrology fields and includes them in the save payload', async () => {
    const user = userEvent.setup();
    renderModal();
    await screen.findByText(/Toolhead metrology — Extruder/);

    const offsetXInput = screen.getByLabelText(/offset x/i);
    await user.clear(offsetXInput);
    await user.type(offsetXInput, '12.5');

    await user.click(screen.getByRole('button', { name: /save calibration setup/i }));

    await waitFor(() => expect(mockUpdateCalibrationSetup).toHaveBeenCalled());
    const [, request] = mockUpdateCalibrationSetup.mock.calls[0];
    expect(request.toolheads).toEqual([
      expect.objectContaining({ id: 'toolhead-1', offsetX: 12.5 }),
    ]);
  });

  it('editing an unrelated field preserves unanswered tri-state flags as null, never coercing them to false', async () => {
    // Regression test: isDirectDrive/nozzleIsHardened/supportsPressureAdvance/
    // supportsFirmwareRetraction are bool? domain fields where null means
    // "operator hasn't answered yet" — a state distinct from an explicit false.
    // Editing an unrelated field (here, offsetX) must not silently coerce any of
    // these to false in the save payload.
    const user = userEvent.setup();
    renderModal({ supportsPressureAdvance: null, supportsFirmwareRetraction: null });
    await screen.findByText(/Toolhead metrology — Extruder/);

    const offsetXInput = screen.getByLabelText(/offset x/i);
    await user.clear(offsetXInput);
    await user.type(offsetXInput, '3');

    await user.click(screen.getByRole('button', { name: /save calibration setup/i }));

    await waitFor(() => expect(mockUpdateCalibrationSetup).toHaveBeenCalled());
    const [, request] = mockUpdateCalibrationSetup.mock.calls[0];
    expect(request.supportsPressureAdvance).toBeNull();
    expect(request.supportsFirmwareRetraction).toBeNull();
    expect(request.toolheads).toEqual([
      expect.objectContaining({
        id: 'toolhead-1',
        offsetX: 3,
        isDirectDrive: null,
        nozzleIsHardened: null,
      }),
    ]);
  });

  it('lets an operator explicitly set a tri-state capability flag to Yes or No, distinct from leaving it unknown', async () => {
    const user = userEvent.setup();
    renderModal();
    await screen.findByText(/Toolhead metrology — Extruder/);

    await user.selectOptions(screen.getByLabelText(/supports pressure advance/i), 'true');
    await user.selectOptions(screen.getByLabelText(/is direct drive/i), 'false');

    await user.click(screen.getByRole('button', { name: /save calibration setup/i }));

    await waitFor(() => expect(mockUpdateCalibrationSetup).toHaveBeenCalled());
    const [, request] = mockUpdateCalibrationSetup.mock.calls[0];
    expect(request.supportsPressureAdvance).toBe(true);
    expect(request.toolheads).toEqual([
      expect.objectContaining({ id: 'toolhead-1', isDirectDrive: false }),
    ]);
  });

  it('binds all three slicer profiles and submits their ids', async () => {
    // The gap this section closes: a printer with no machine profile bound reports
    // ~19 missing calibration inputs, and until now no client wrote these ids at all.
    const user = userEvent.setup();
    renderModal();
    await screen.findByText(/Toolhead metrology — Extruder/);

    await user.selectOptions(screen.getByLabelText(/machine profile/i), 'machine-1');
    await user.selectOptions(screen.getByLabelText(/process profile/i), 'process-1');
    await user.selectOptions(screen.getByLabelText(/filament profile/i), 'filament-1');

    await user.click(screen.getByRole('button', { name: /save calibration setup/i }));

    await waitFor(() => expect(mockUpdateCalibrationSetup).toHaveBeenCalled());
    const [, request] = mockUpdateCalibrationSetup.mock.calls[0];
    expect(request.machineProfileId).toBe('machine-1');
    expect(request.processProfileId).toBe('process-1');
    expect(request.filamentProfileId).toBe('filament-1');
  });

  it('preselects the currently bound profiles from the calibration context', async () => {
    renderModal({
      slicer: {
        engine: 'OrcaSlicer',
        distribution: 'upstream',
        version: '2.0.0',
        profileFormat: 'orca-json',
        machineProfileId: 'machine-1',
        processProfileId: 'process-1',
        filamentProfileId: 'filament-1',
      },
    });

    await waitFor(() => expect(screen.getByLabelText(/machine profile/i)).toHaveValue('machine-1'));
    expect(screen.getByLabelText(/process profile/i)).toHaveValue('process-1');
    expect(screen.getByLabelText(/filament profile/i)).toHaveValue('filament-1');
  });

  it('submits the clear sentinel rather than omitting an unbound profile', async () => {
    // Control for the binding test: the endpoint treats an omitted id as "leave
    // unchanged", so clearing a selection has to be an explicit all-zero Guid or
    // the operator's unbind would be silently discarded.
    const user = userEvent.setup();
    renderModal({
      slicer: {
        engine: 'OrcaSlicer',
        distribution: 'upstream',
        version: '2.0.0',
        profileFormat: 'orca-json',
        machineProfileId: 'machine-1',
        processProfileId: 'process-1',
        filamentProfileId: 'filament-1',
      },
    });
    await waitFor(() => expect(screen.getByLabelText(/machine profile/i)).toHaveValue('machine-1'));

    await user.selectOptions(screen.getByLabelText(/machine profile/i), '');
    await user.click(screen.getByRole('button', { name: /save calibration setup/i }));

    await waitFor(() => expect(mockUpdateCalibrationSetup).toHaveBeenCalled());
    const [, request] = mockUpdateCalibrationSetup.mock.calls[0];
    expect(request.machineProfileId).toBe('00000000-0000-0000-0000-000000000000');
    expect(request.processProfileId).toBe('process-1');
  });
});
