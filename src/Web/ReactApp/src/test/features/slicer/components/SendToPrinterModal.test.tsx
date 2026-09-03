import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import React from 'react';
import { SendToPrinterModal } from '@/features/slicer/components/SendToPrinterModal';
import { toast } from 'sonner';

vi.mock('sonner', () => ({
  toast: {
    success: vi.fn(),
    error: vi.fn(),
    info: vi.fn(),
  },
}));

const mockSendToPrinter = vi.fn();
const mockAddSliceToQueue = vi.fn();

vi.mock('@/services/sliceJobService', () => ({
  sliceJobService: {
    sendToPrinter: (...args: unknown[]) => mockSendToPrinter(...args),
    addSliceToQueue: (...args: unknown[]) => mockAddSliceToQueue(...args),
  },
}));

const mockPrinters = [
  { id: 'printer-1', name: 'Prusa MK4', isOnline: true, backend: 'PrusaLink', backendUrl: 'http://prusa' },
  { id: 'printer-2', name: 'Voron 2.4', isOnline: true, backend: 'Moonraker', backendUrl: 'http://voron' },
  { id: 'printer-3', name: 'Offline Ender', isOnline: false, backend: 'Moonraker', backendUrl: 'http://ender' },
];

vi.mock('@/common/hooks/useApi', () => ({
  usePrintersFast: () => ({ data: mockPrinters, isLoading: false }),
}));

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  const Wrapper = function Wrapper({ children }: { children: React.ReactNode }) {
    return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
  };
  return { Wrapper, queryClient };
}

function renderModal(props: Partial<React.ComponentProps<typeof SendToPrinterModal>> = {}) {
  const defaultProps = {
    isOpen: true,
    onClose: vi.fn(),
    jobId: 'job-123',
    artifactId: 'artifact-selected',
    ...props,
  };
  const { Wrapper, queryClient } = createWrapper();
  return {
    ...render(<SendToPrinterModal {...defaultProps} />, { wrapper: Wrapper }),
    props: defaultProps,
    queryClient,
  };
}

describe('SendToPrinterModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders printer selector with online printers only', () => {
    renderModal();

    expect(screen.getByLabelText('Select printer')).toBeInTheDocument();
    expect(screen.getByText('Prusa MK4')).toBeInTheDocument();
    expect(screen.getByText('Voron 2.4')).toBeInTheDocument();
    expect(screen.queryByText('Offline Ender')).not.toBeInTheDocument();
  });

  it('disables submit when no printer is selected', () => {
    renderModal();

    const sendButton = screen.getByRole('button', { name: /send to printer/i });
    expect(sendButton).toBeDisabled();
  });

  it('enables submit when a printer is selected', () => {
    renderModal();

    fireEvent.change(screen.getByLabelText('Select printer'), { target: { value: 'printer-1' } });
    const sendButton = screen.getByRole('button', { name: /send to printer/i });
    expect(sendButton).toBeEnabled();
  });

  it('uses the canonical promote-first direct print contract', async () => {
    mockSendToPrinter.mockResolvedValue({
      jobId: 'job-123',
      printerId: 'printer-1',
      fileName: 'model.gcode',
      printStarted: false,
      message: 'File sent',
    });

    renderModal();

    fireEvent.change(screen.getByLabelText('Select printer'), { target: { value: 'printer-1' } });
    fireEvent.click(screen.getByRole('button', { name: /send to printer/i }));

    await waitFor(() => {
      expect(mockSendToPrinter).toHaveBeenCalledWith(
        'job-123',
        'artifact-selected',
        'printer-1',
        false,
      );
    });
  });

  it('sends startPrint=true when checkbox is checked', async () => {
    mockSendToPrinter.mockResolvedValue({
      jobId: 'job-123',
      printerId: 'printer-2',
      fileName: 'model.gcode',
      printStarted: true,
      message: 'Print started',
    });

    renderModal();

    fireEvent.change(screen.getByLabelText('Select printer'), { target: { value: 'printer-2' } });
    fireEvent.click(screen.getByText('Start printing immediately'));
    fireEvent.click(screen.getByRole('button', { name: /send to printer/i }));

    await waitFor(() => {
      expect(mockSendToPrinter).toHaveBeenCalledWith(
        'job-123',
        'artifact-selected',
        'printer-2',
        true,
      );
    });
  });

  it('shows success toast and closes modal on success', async () => {
    const onClose = vi.fn();
    mockSendToPrinter.mockResolvedValue({
      jobId: 'job-123',
      printerId: 'printer-1',
      fileName: 'benchy.gcode',
      printStarted: false,
      message: 'File sent',
    });

    renderModal({ onClose });

    fireEvent.change(screen.getByLabelText('Select printer'), { target: { value: 'printer-1' } });
    fireEvent.click(screen.getByRole('button', { name: /send to printer/i }));

    await waitFor(() => {
      expect(toast.success).toHaveBeenCalledWith('Sent benchy.gcode to Prusa MK4');
    });
    expect(onClose).toHaveBeenCalled();
  });

  it('shows error toast on failure', async () => {
    mockSendToPrinter.mockRejectedValue(new Error('Upload failed'));

    renderModal();

    fireEvent.change(screen.getByLabelText('Select printer'), { target: { value: 'printer-1' } });
    fireEvent.click(screen.getByRole('button', { name: /send to printer/i }));

    await waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith('Failed to send to printer: Upload failed');
    });
  });

  it('does not render when isOpen is false', () => {
    renderModal({ isOpen: false });

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  describe('mode chooser', () => {
    it('shows both Send to Printer and Add to Queue mode buttons', () => {
      renderModal();
      expect(screen.getByRole('radio', { name: /send to printer/i })).toBeInTheDocument();
      expect(screen.getByRole('radio', { name: /add to queue/i })).toBeInTheDocument();
    });

    it('defaults to Send to Printer mode', () => {
      renderModal();
      const directBtn = screen.getByRole('radio', { name: /send to printer/i });
      expect(directBtn).toHaveAttribute('aria-checked', 'true');
    });
  });

  describe('Add to Queue mode', () => {
    it('uses the canonical promote-first queue contract and refreshes queue queries', async () => {
      mockAddSliceToQueue.mockResolvedValue({
        printJobId: 'pj-1',
        queuePosition: 3,
        message: 'Queued',
      });

      const { queryClient } = renderModal({ selectedSpoolId: 42, requiredPrinterModel: 'MK4', requiredMaterialType: 'PLA', requiredNozzleDiameter: 0.4 });
      const invalidate = vi.spyOn(queryClient, 'invalidateQueries');

      fireEvent.click(screen.getByRole('radio', { name: /add to queue/i }));

      fireEvent.click(screen.getByRole('button', { name: /add to queue/i }));

      await waitFor(() => {
        expect(mockAddSliceToQueue).toHaveBeenCalledWith('job-123', expect.objectContaining({
          artifactId: 'artifact-selected',
          priority: 'Normal',
          copies: 1,
          spoolId: 42,
          requiredPrinterModel: 'MK4',
          requiredMaterialType: 'PLA',
          requiredNozzleDiameter: 0.4,
        }));
      });
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['job-queue'] });
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['queue-jobs'] });
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['queue-stats'] });
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['queue-summaries', 'fleet'] });
    });

    it('shows success toast with queue position on success', async () => {
      const onClose = vi.fn();
      mockAddSliceToQueue.mockResolvedValue({
        printJobId: 'pj-1',
        queuePosition: 2,
        message: 'Queued',
      });

      renderModal({ onClose });

      fireEvent.click(screen.getByRole('radio', { name: /add to queue/i }));
      fireEvent.click(screen.getByRole('button', { name: /add to queue/i }));

      await waitFor(() => {
        expect(toast.success).toHaveBeenCalledWith('Queued — position 2');
        expect(onClose).toHaveBeenCalled();
      });
    });

    it('shows success toast without position when queuePosition is null', async () => {
      mockAddSliceToQueue.mockResolvedValue({
        printJobId: 'pj-1',
        queuePosition: null,
        message: 'Queued',
      });

      renderModal();

      fireEvent.click(screen.getByRole('radio', { name: /add to queue/i }));
      fireEvent.click(screen.getByRole('button', { name: /add to queue/i }));

      await waitFor(() => {
        expect(toast.success).toHaveBeenCalledWith('Queued');
      });
    });

    it('shows error toast on queue failure', async () => {
      mockAddSliceToQueue.mockRejectedValue(new Error('Queue full'));

      renderModal();

      fireEvent.click(screen.getByRole('radio', { name: /add to queue/i }));
      fireEvent.click(screen.getByRole('button', { name: /add to queue/i }));

      await waitFor(() => {
        expect(toast.error).toHaveBeenCalledWith('Failed to add to queue: Queue full');
      });
    });

    it('renders requirement chips when requirements are provided', () => {
      renderModal({ requiredPrinterModel: 'MK4', requiredMaterialType: 'PETG', requiredNozzleDiameter: 0.6 });

      fireEvent.click(screen.getByRole('radio', { name: /add to queue/i }));

      expect(screen.getByText(/MK4/)).toBeInTheDocument();
      expect(screen.getByText(/PETG/)).toBeInTheDocument();
      expect(screen.getByText(/0.6mm nozzle/)).toBeInTheDocument();
    });
  });
});
