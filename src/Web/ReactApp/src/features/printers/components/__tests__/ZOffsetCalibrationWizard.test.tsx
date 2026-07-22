import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ZOffsetCalibrationWizard } from '../ZOffsetCalibrationWizard';
import type { Printer } from '@/types/api';

const mockSendGcode = vi.fn();
const mockSaveZOffset = vi.fn();

vi.mock('@/services/api', () => ({
  apiClient: {
    sendGcode: (...args: unknown[]) => mockSendGcode(...args),
    saveZOffset: (...args: unknown[]) => mockSaveZOffset(...args),
  },
}));

vi.mock('@/common/hooks/useApi', () => ({
  queryKeys: {
    printers: ['printers'],
    printerDetails: (id: string) => ['printers', id, 'details'],
  },
}));

vi.mock('@/common/components/modals/Modal', () => ({
  Modal: ({ isOpen, title, footer, children }: { isOpen: boolean; title: string; footer?: React.ReactNode; children: React.ReactNode }) => (
    isOpen ? (
      <div data-testid="modal">
        <h1>{title}</h1>
        {children}
        {footer}
      </div>
    ) : null
  ),
}));

function createTestPrinter(overrides: Partial<Printer> = {}): Printer {
  return {
    id: 'printer-1',
    name: 'Test Printer',
    backend: 'Moonraker' as unknown as Printer['backend'],
    isOnline: true,
    state: 'Idle',
    serverUrl: 'http://test.local',
    ...overrides,
  } as Printer;
}

function renderWizard(props: { isOpen?: boolean; printer?: Printer } = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onClose = vi.fn();
  const printer = props.printer ?? createTestPrinter();

  return {
    onClose,
    ...render(
      <QueryClientProvider client={queryClient}>
        <ZOffsetCalibrationWizard
          isOpen={props.isOpen ?? true}
          onClose={onClose}
          printer={printer}
        />
      </QueryClientProvider>,
    ),
  };
}

describe('ZOffsetCalibrationWizard', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockSendGcode.mockResolvedValue({ success: true });
    mockSaveZOffset.mockResolvedValue({ success: true });
  });

  it('does not render when closed', () => {
    renderWizard({ isOpen: false });
    expect(screen.queryByTestId('modal')).not.toBeInTheDocument();
  });

  it('renders the introduction step initially', () => {
    renderWizard();
    expect(screen.getByText('Z-Offset Calibration')).toBeInTheDocument();
    expect(screen.getByText(/this wizard will guide you/i)).toBeInTheDocument();
  });

  it('shows step progress as a progress bar', () => {
    renderWizard();
    expect(screen.getByRole('progressbar')).toBeInTheDocument();
    // Step label text is split across nodes: "Step 1 of 6: Introduction"
    expect(screen.getByText(/Introduction/)).toBeInTheDocument();
  });

  it('advances to Home Axes step when Next is clicked', async () => {
    const user = userEvent.setup();
    renderWizard();

    await user.click(screen.getByRole('button', { name: /next/i }));
    expect(screen.getByRole('button', { name: /home all axes/i })).toBeInTheDocument();
  });

  it('sends G28 command when homing and auto-advances', async () => {
    const user = userEvent.setup();
    renderWizard();

    // Go to Home Axes step
    await user.click(screen.getByRole('button', { name: /next/i }));
    await user.click(screen.getByRole('button', { name: /home all axes/i }));

    expect(mockSendGcode).toHaveBeenCalledWith('printer-1', 'G28');
    // After success, auto-advances to Move to Center
    await waitFor(() => {
      expect(screen.getByText(/move the nozzle to the center/i)).toBeInTheDocument();
    });
  });

  it('navigates back with the Back button', async () => {
    const user = userEvent.setup();
    renderWizard();

    // Go to step 2
    await user.click(screen.getByRole('button', { name: /next/i }));
    expect(screen.getByRole('button', { name: /home all axes/i })).toBeInTheDocument();

    // Go back — intro text should reappear
    await user.click(screen.getByRole('button', { name: /back/i }));
    expect(screen.getByText(/this wizard will guide you/i)).toBeInTheDocument();
  });

  it('navigates to Adjust Z-Offset step via action buttons', async () => {
    const user = userEvent.setup();
    renderWizard();

    // Step 1 → 2 via Next
    await user.click(screen.getByRole('button', { name: /next/i }));
    // Step 2 → 3 via Home action (auto-advance on success)
    await user.click(screen.getByRole('button', { name: /home all axes/i }));
    await waitFor(() => {
      expect(screen.getByText(/move the nozzle to the center/i)).toBeInTheDocument();
    });
    // Step 3 → 4 via Move action (auto-advance on success)
    await user.click(screen.getByRole('button', { name: /move to center/i }));
    await waitFor(() => {
      expect(screen.getByText(/Z-Offset:.*0\.000 mm/)).toBeInTheDocument();
    });

    // Verify increment buttons
    expect(screen.getByText('0.01 mm')).toBeInTheDocument();
    expect(screen.getByText('0.05 mm')).toBeInTheDocument();
    expect(screen.getByText('0.1 mm')).toBeInTheDocument();
  });

  it('adjusts Z-offset down and sends relative G-code', async () => {
    const user = userEvent.setup();
    renderWizard();

    // Navigate to Adjust step
    await user.click(screen.getByRole('button', { name: /next/i }));
    await user.click(screen.getByRole('button', { name: /home all axes/i }));
    await waitFor(() => expect(screen.getByText(/move the nozzle/i)).toBeInTheDocument());
    await user.click(screen.getByRole('button', { name: /move to center/i }));
    await waitFor(() => expect(screen.getByText(/Z-Offset:/)).toBeInTheDocument());

    // Click Nozzle Down — sends absolute move from Z=10 offset by -0.05
    await user.click(screen.getByRole('button', { name: /nozzle down/i }));
    expect(mockSendGcode).toHaveBeenLastCalledWith(
      'printer-1',
      expect.stringContaining('G1 Z'),
    );
  });

  it('shows the first layer visual guide on Adjust step', async () => {
    const user = userEvent.setup();
    renderWizard();

    // Navigate to Adjust step
    await user.click(screen.getByRole('button', { name: /next/i }));
    await user.click(screen.getByRole('button', { name: /home all axes/i }));
    await waitFor(() => expect(screen.getByText(/move the nozzle/i)).toBeInTheDocument());
    await user.click(screen.getByRole('button', { name: /move to center/i }));
    await waitFor(() => expect(screen.getByText(/Z-Offset:/)).toBeInTheDocument());

    expect(screen.getByText('First Layer Visual Guide')).toBeInTheDocument();
    expect(screen.getByText('Too Far')).toBeInTheDocument();
    expect(screen.getByText('Just Right')).toBeInTheDocument();
    expect(screen.getByText('Too Close')).toBeInTheDocument();
  });

  it('"Looks Good — Continue" advances to Save step', async () => {
    const user = userEvent.setup();
    renderWizard();

    // Navigate to Adjust step
    await user.click(screen.getByRole('button', { name: /next/i }));
    await user.click(screen.getByRole('button', { name: /home all axes/i }));
    await waitFor(() => expect(screen.getByText(/move the nozzle/i)).toBeInTheDocument());
    await user.click(screen.getByRole('button', { name: /move to center/i }));
    await waitFor(() => expect(screen.getByText(/Z-Offset:/)).toBeInTheDocument());

    // Click "Looks Good — Continue"
    await user.click(screen.getByRole('button', { name: /looks good/i }));
    expect(screen.getByText(/ready to save/i)).toBeInTheDocument();
  });

  it('shows Klipper commands for Moonraker backend on Save step', async () => {
    const user = userEvent.setup();
    renderWizard();

    // Navigate to Save step
    await user.click(screen.getByRole('button', { name: /next/i }));
    await user.click(screen.getByRole('button', { name: /home all axes/i }));
    await waitFor(() => expect(screen.getByText(/move the nozzle/i)).toBeInTheDocument());
    await user.click(screen.getByRole('button', { name: /move to center/i }));
    await waitFor(() => expect(screen.getByText(/Z-Offset:/)).toBeInTheDocument());
    await user.click(screen.getByRole('button', { name: /looks good/i }));

    expect(screen.getByText(/Klipper\/Moonraker/i)).toBeInTheDocument();
    expect(screen.getByText(/SET_GCODE_OFFSET/)).toBeInTheDocument();
  });
});
