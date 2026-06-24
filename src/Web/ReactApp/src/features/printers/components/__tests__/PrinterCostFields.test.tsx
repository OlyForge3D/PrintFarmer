import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { EditPrinterModal } from '../EditPrinterModal';

const mockUsePrinterDetails = vi.fn();
const mockUseUpdatePrinter = vi.fn();

vi.mock('@/common/hooks/useApi', () => ({
  usePrinterDetails: (...args: unknown[]) => mockUsePrinterDetails(...args),
  useUpdatePrinter: (...args: unknown[]) => mockUseUpdatePrinter(...args),
  useManufacturers: () => ({ data: [] }),
  useModels: () => ({ data: [] }),
  useFilamentTypes: () => ({ data: [] }),
  useModelDefaultCapabilities: () => ({ data: undefined, isLoading: false }),
  useHotendModels: () => ({ data: [] }),
  useExtruderModels: () => ({ data: [] }),
  useToolheadModels: () => ({ data: [] }),
  useNozzleModels: () => ({ data: [] }),
}));

vi.mock('@/hooks/useSlicer', () => ({
  useSlicer: () => ({ isSlicerAvailable: false }),
}));

vi.mock('@/features/cameras/hooks/usePrinterCameras', () => ({
  usePrinterCameras: () => ({ data: [] }),
}));

vi.mock('@/common/components/modals/Modal', () => ({
  Modal: ({ isOpen, title, footer, children }: { isOpen: boolean; title: string; footer?: React.ReactNode; children: React.ReactNode }) => (
    isOpen ? (
      <div>
        <h1>{title}</h1>
        {children}
        {footer}
      </div>
    ) : null
  ),
}));

vi.mock('@/features/slicer/components/CloneProfilesModal', () => ({
  CloneProfilesModal: () => null,
}));

function basePrinterDetails(overrides: Record<string, unknown> = {}) {
  return {
    name: 'test-printer',
    serverUrl: 'http://test.local',
    originalServerUrl: 'http://test.local',
    notes: '',
    manufacturerId: 'mfg-1',
    modelId: 'model-1',
    backend: 'Moonraker',
    apiKey: '',
    username: '',
    password: '',
    cameraStreamUrl: '',
    cameraSnapshotUrl: '',
    capabilities: {},
    backendPort: 7125,
    frontendPort: 80,
    obicoEnabled: false,
    toolheads: [],
    wattage: undefined,
    machineHourlyRate: undefined,
    ...overrides,
  };
}

describe('Printer Cost Fields', () => {
  const mutateAsync = vi.fn().mockResolvedValue({ name: 'test-printer' });

  beforeEach(() => {
    vi.clearAllMocks();
    mockUsePrinterDetails.mockReturnValue({ data: basePrinterDetails() });
    mockUseUpdatePrinter.mockReturnValue({ status: 'idle', mutateAsync });
  });

  it('renders Wattage and Machine Hourly Rate fields in edit modal', async () => {
    render(
      <EditPrinterModal printerId="p-1" isOpen onClose={vi.fn()} onSuccess={vi.fn()} />
    );

    expect(await screen.findByText('Cost Settings')).toBeInTheDocument();
    expect(screen.getByTitle('Printer power consumption in watts')).toBeInTheDocument();
    expect(screen.getByTitle('Machine hourly operating rate')).toBeInTheDocument();
  });

  it('shows helper text for cost fields', async () => {
    render(
      <EditPrinterModal printerId="p-1" isOpen onClose={vi.fn()} onSuccess={vi.fn()} />
    );

    await screen.findByText('Cost Settings');
    expect(screen.getByText(/power consumption in watts/i)).toBeInTheDocument();
    expect(screen.getByText(/hourly operating cost/i)).toBeInTheDocument();
  });

  it('pre-populates wattage and machineHourlyRate when printer has values', async () => {
    mockUsePrinterDetails.mockReturnValue({
      data: basePrinterDetails({ wattage: 350, machineHourlyRate: 1.25 }),
    });

    render(
      <EditPrinterModal printerId="p-1" isOpen onClose={vi.fn()} onSuccess={vi.fn()} />
    );

    const wattageInput = await screen.findByTitle('Printer power consumption in watts');
    const rateInput = screen.getByTitle('Machine hourly operating rate');

    expect(wattageInput).toHaveValue(350);
    expect(rateInput).toHaveValue(1.25);
  });

  it('leaves cost fields empty when printer has no overrides', async () => {
    render(
      <EditPrinterModal printerId="p-1" isOpen onClose={vi.fn()} onSuccess={vi.fn()} />
    );

    const wattageInput = await screen.findByTitle('Printer power consumption in watts');
    const rateInput = screen.getByTitle('Machine hourly operating rate');

    expect(wattageInput).toHaveValue(null);
    expect(rateInput).toHaveValue(null);
  });

  it('submits wattage and machineHourlyRate with numeric values', async () => {
    const user = userEvent.setup();

    render(
      <EditPrinterModal printerId="p-1" isOpen onClose={vi.fn()} onSuccess={vi.fn()} />
    );

    const wattageInput = await screen.findByTitle('Printer power consumption in watts');
    const rateInput = screen.getByTitle('Machine hourly operating rate');

    await user.clear(wattageInput);
    await user.type(wattageInput, '400');
    await user.clear(rateInput);
    await user.type(rateInput, '2.50');

    await user.click(screen.getByRole('button', { name: /save changes/i }));

    await waitFor(() => {
      expect(mutateAsync).toHaveBeenCalledWith({
        id: 'p-1',
        printer: expect.objectContaining({
          wattage: 400,
          machineHourlyRate: 2.5,
        }),
      });
    });
  });

  it('submits null for empty cost fields', async () => {
    const user = userEvent.setup();

    mockUsePrinterDetails.mockReturnValue({
      data: basePrinterDetails({ wattage: 200, machineHourlyRate: 0.75 }),
    });

    render(
      <EditPrinterModal printerId="p-1" isOpen onClose={vi.fn()} onSuccess={vi.fn()} />
    );

    const wattageInput = await screen.findByTitle('Printer power consumption in watts');
    const rateInput = screen.getByTitle('Machine hourly operating rate');

    await user.clear(wattageInput);
    await user.clear(rateInput);

    await user.click(screen.getByRole('button', { name: /save changes/i }));

    await waitFor(() => {
      expect(mutateAsync).toHaveBeenCalledWith({
        id: 'p-1',
        printer: expect.objectContaining({
          wattage: undefined,
          machineHourlyRate: undefined,
        }),
      });
    });
  });
});
