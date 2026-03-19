import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { AutoDispatchStatus } from '@/types/api';

vi.mock('@/services/api', () => ({
  apiClient: {
    post: vi.fn().mockResolvedValue({ data: {} }),
    skipAutoDispatchJob: vi.fn().mockResolvedValue(undefined),
    cancelAutoDispatch: vi.fn().mockResolvedValue(undefined),
  },
}));

vi.mock('sonner', () => ({
  toast: {
    success: vi.fn(),
    info: vi.fn(),
    error: vi.fn(),
    warning: vi.fn(),
  },
}));

vi.mock('@/common/components/ui', () => ({
  Button: ({ children, onClick, disabled, loading, 'aria-label': ariaLabel }: {
    children: React.ReactNode;
    onClick?: () => void;
    disabled?: boolean;
    loading?: boolean;
    'aria-label'?: string;
  }) => (
    <button onClick={onClick} disabled={disabled || loading} aria-label={ariaLabel} data-loading={loading}>
      {children}
    </button>
  ),
}));

vi.mock('@/common/components/icons/MdiIcons', () => ({
  CheckCircleIcon: () => <span data-testid="check-icon" />,
  SkipForwardIcon: () => <span data-testid="skip-icon" />,
  CloseIcon: () => <span data-testid="close-icon" />,
}));

vi.mock('@/common/hooks/useApi', () => ({
  queryKeys: {
    printers: ['printers'] as const,
    printer: (id: string) => ['printers', id] as const,
  },
}));

import { BedClearBanner } from '../components/BedClearBanner';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
}

const baseStatus: AutoDispatchStatus = {
  printerId: 'printer-1',
  enabled: true,
  state: 'PendingReady',
  queueDepth: 2,
};

const readyResultWithJob = {
  status: { printerId: 'printer-1', enabled: true, state: 'Ready', queueDepth: 1 },
  nextJob: { id: 'job-1', name: 'benchy.gcode', estimatedFilamentUsageG: 10 },
  filamentCheck: { sufficient: true, materialMismatch: false },
};

const readyResultNoJob = {
  status: { printerId: 'printer-1', enabled: true, state: 'None', queueDepth: 0 },
  nextJob: null,
  filamentCheck: null,
};

const readyResultMaterialMismatch = {
  status: { printerId: 'printer-1', enabled: true, state: 'Ready', queueDepth: 1 },
  nextJob: { id: 'job-2', name: 'part.gcode' },
  filamentCheck: {
    sufficient: true,
    materialMismatch: true,
    loadedMaterial: 'PLA',
    requiredMaterial: 'PETG',
  },
};

const readyResultInsufficientFilament = {
  status: { printerId: 'printer-1', enabled: true, state: 'Ready', queueDepth: 1 },
  nextJob: { id: 'job-3', name: 'big-part.gcode' },
  filamentCheck: {
    sufficient: false,
    materialMismatch: false,
    message: 'Only 50g remaining, job requires 200g',
  },
};

describe('BedClearBanner', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders when state is PendingReady', () => {
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.getByText(/confirm bed is clear/i)).toBeInTheDocument();
    expect(screen.getByText(/2 jobs queued/)).toBeInTheDocument();
  });

  it('renders nothing when state is None', () => {
    const status = { ...baseStatus, state: 'None' as const };
    const { container } = render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={status} />,
      { wrapper: createWrapper() },
    );
    expect(container.firstChild).toBeNull();
  });

  it('renders nothing when state is Ready', () => {
    const status = { ...baseStatus, state: 'Ready' as const };
    const { container } = render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={status} />,
      { wrapper: createWrapper() },
    );
    expect(container.firstChild).toBeNull();
  });

  it('shows singular "job" when queueDepth is 1', () => {
    const status = { ...baseStatus, queueDepth: 1 };
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={status} />,
      { wrapper: createWrapper() },
    );
    expect(screen.getByText(/1 job queued/)).toBeInTheDocument();
  });

  it('confirms bed clear and shows dispatch toast without manual dispatch call', async () => {
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: readyResultWithJob });
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(apiClient.post).toHaveBeenCalledWith('/auto-print/printer-1/ready');
    });
    await waitFor(() => {
      expect(toast.success).toHaveBeenCalledWith('Dispatching "benchy.gcode" to MK4');
    });
  });

  it('optimistically updates printer cache to Starting state on dispatch', async () => {
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: readyResultWithJob });
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    const existingPrinter = { id: 'printer-1', name: 'MK4', state: 'Idle', jobName: undefined, progress: undefined };
    queryClient.setQueryData(['printers'], [existingPrinter]);
    queryClient.setQueryData(['printers', 'printer-1'], existingPrinter);

    const wrapper = ({ children }: { children: React.ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    );

    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));

    await waitFor(() => {
      expect(toast.success).toHaveBeenCalledWith('Dispatching "benchy.gcode" to MK4');
    });

    const updatedList = queryClient.getQueryData<Array<{ id: string; state: string; jobName?: string; progress?: number }>>(['printers']);
    expect(updatedList?.[0]?.state).toBe('Starting...');
    expect(updatedList?.[0]?.jobName).toBe('benchy.gcode');
    expect(updatedList?.[0]?.progress).toBe(0);

    const updatedSingle = queryClient.getQueryData<{ state: string; jobName?: string }>(['printers', 'printer-1']);
    expect(updatedSingle?.state).toBe('Starting...');
    expect(updatedSingle?.jobName).toBe('benchy.gcode');
  });

  it('does not optimistically update cache when no next job', async () => {
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: readyResultNoJob });
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    const existingPrinter = { id: 'printer-1', name: 'MK4', state: 'Idle' };
    queryClient.setQueryData(['printers'], [existingPrinter]);

    const wrapper = ({ children }: { children: React.ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    );

    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));

    await waitFor(() => {
      expect(toast.success).toHaveBeenCalledWith('Bed clear confirmed for MK4 — no jobs queued');
    });

    const updatedList = queryClient.getQueryData<Array<{ id: string; state: string }>>(['printers']);
    expect(updatedList?.[0]?.state).toBe('Idle');
  });

  it('shows success without dispatch when no jobs queued', async () => {
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: readyResultNoJob });
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(toast.success).toHaveBeenCalledWith('Bed clear confirmed for MK4 — no jobs queued');
    });
  });

  it('warns on material mismatch without dispatching', async () => {
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: readyResultMaterialMismatch });
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(toast.warning).toHaveBeenCalledWith(
        expect.stringContaining('Material mismatch'),
        expect.objectContaining({ duration: 8000 }),
      );
    });
  });

  it('warns on insufficient filament without dispatching', async () => {
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: readyResultInsufficientFilament });
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(toast.warning).toHaveBeenCalledWith(
        'Only 50g remaining, job requires 200g',
        expect.objectContaining({ duration: 8000 }),
      );
    });
  });

  it('calls skip endpoint when Skip button is clicked', async () => {
    vi.mocked(apiClient.skipAutoDispatchJob).mockResolvedValueOnce(undefined);
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Skip next queued job'));
    await waitFor(() => {
      expect(apiClient.skipAutoDispatchJob).toHaveBeenCalledWith('printer-1');
    });
    await waitFor(() => {
      expect(toast.info).toHaveBeenCalledWith('Skipped next queued job');
    });
  });

  it('calls cancel endpoint when Cancel button is clicked', async () => {
    vi.mocked(apiClient.cancelAutoDispatch).mockResolvedValueOnce(undefined);
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Cancel auto-dispatch'));
    await waitFor(() => {
      expect(apiClient.cancelAutoDispatch).toHaveBeenCalledWith('printer-1');
    });
    await waitFor(() => {
      expect(toast.info).toHaveBeenCalledWith('Auto-dispatch cancelled');
    });
  });

  it('shows error toast on confirm failure', async () => {
    vi.mocked(apiClient.post).mockRejectedValueOnce(new Error('Network error'));
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith('Failed to confirm bed clear');
    });
  });

  it('has correct ARIA attributes', () => {
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    const alert = screen.getByRole('alert');
    expect(alert).toHaveAttribute('aria-label', 'Bed clear confirmation required');
  });
});
