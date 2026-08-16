import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CalibrationSetupModal } from '../CalibrationSetupModal';
import type { CalibrationContextDto } from '@/types/api';

const mockGetCalibrationContext = vi.fn();
const mockUpdateCalibrationSetup = vi.fn();

vi.mock('@/services/api', () => ({
  apiClient: {
    getCalibrationContext: (...args: unknown[]) => mockGetCalibrationContext(...args),
    updateCalibrationSetup: (...args: unknown[]) => mockUpdateCalibrationSetup(...args),
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

    // The only firmware-related control is the confirm-only verify button.
    expect(screen.getByRole('button', { name: /mark firmware verified/i })).toBeInTheDocument();
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
});
