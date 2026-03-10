import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { AutoPrintStatus } from '@/types/api';

vi.mock('@/services/api', () => ({
  apiClient: {
    post: vi.fn().mockResolvedValue({ data: {} }),
    dispatchPrintQueueJob: vi.fn().mockResolvedValue({}),
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

const baseStatus: AutoPrintStatus = {
  printerId: 'printer-1',
  autoPrintEnabled: true,
  state: 'PendingReady',
  queuedJobCount: 2,
};

const readyResultWithJob = {
  status: { printerId: 'printer-1', autoPrintEnabled: true, state: 'Ready', queuedJobCount: 1 },
  nextJob: { id: 'job-1', name: 'benchy.gcode', estimatedFilamentUsageG: 10 },
  filamentCheck: { sufficient: true, materialMismatch: false },
};

const readyResultNoJob = {
  status: { printerId: 'printer-1', autoPrintEnabled: true, state: 'None', queuedJobCount: 0 },
  nextJob: null,
  filamentCheck: null,
};

const readyResultMaterialMismatch = {
  status: { printerId: 'printer-1', autoPrintEnabled: true, state: 'Ready', queuedJobCount: 1 },
  nextJob: { id: 'job-2', name: 'part.gcode' },
  filamentCheck: {
    sufficient: true,
    materialMismatch: true,
    loadedMaterial: 'PLA',
    requiredMaterial: 'PETG',
  },
};

const readyResultInsufficientFilament = {
  status: { printerId: 'printer-1', autoPrintEnabled: true, state: 'Ready', queuedJobCount: 1 },
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
      <BedClearBanner printerId="printer-1" printerName="MK4" autoPrintStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.getByText(/confirm bed is clear/i)).toBeInTheDocument();
    expect(screen.getByText(/2 jobs queued/)).toBeInTheDocument();
  });

  it('renders nothing when state is None', () => {
    const status = { ...baseStatus, state: 'None' as const };
    const { container } = render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoPrintStatus={status} />,
      { wrapper: createWrapper() },
    );
    expect(container.firstChild).toBeNull();
  });

  it('renders nothing when state is Ready', () => {
    const status = { ...baseStatus, state: 'Ready' as const };
    const { container } = render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoPrintStatus={status} />,
      { wrapper: createWrapper() },
    );
    expect(container.firstChild).toBeNull();
  });

  it('shows singular "job" when queuedJobCount is 1', () => {
    const status = { ...baseStatus, queuedJobCount: 1 };
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoPrintStatus={status} />,
      { wrapper: createWrapper() },
    );
    expect(screen.getByText(/1 job queued/)).toBeInTheDocument();
  });

  it('confirms and dispatches job when filament check passes', async () => {
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: readyResultWithJob });
    vi.mocked(apiClient.dispatchPrintQueueJob).mockResolvedValueOnce({});
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoPrintStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(apiClient.post).toHaveBeenCalledWith('/autoprint/printer-1/ready');
    });
    await waitFor(() => {
      expect(apiClient.dispatchPrintQueueJob).toHaveBeenCalledWith('job-1');
    });
    await waitFor(() => {
      expect(toast.success).toHaveBeenCalledWith('Dispatching "benchy.gcode" to MK4');
    });
  });

  it('shows success without dispatch when no jobs queued', async () => {
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: readyResultNoJob });
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoPrintStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(toast.success).toHaveBeenCalledWith('Bed clear confirmed for MK4 — no jobs queued');
    });
    expect(apiClient.dispatchPrintQueueJob).not.toHaveBeenCalled();
  });

  it('warns on material mismatch without dispatching', async () => {
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: readyResultMaterialMismatch });
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoPrintStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(toast.warning).toHaveBeenCalledWith(
        expect.stringContaining('Material mismatch'),
        expect.objectContaining({ duration: 8000 }),
      );
    });
    expect(apiClient.dispatchPrintQueueJob).not.toHaveBeenCalled();
  });

  it('warns on insufficient filament without dispatching', async () => {
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: readyResultInsufficientFilament });
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoPrintStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(toast.warning).toHaveBeenCalledWith(
        'Only 50g remaining, job requires 200g',
        expect.objectContaining({ duration: 8000 }),
      );
    });
    expect(apiClient.dispatchPrintQueueJob).not.toHaveBeenCalled();
  });

  it('calls skip endpoint when Skip button is clicked', async () => {
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: {} });
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoPrintStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Skip next queued job'));
    await waitFor(() => {
      expect(apiClient.post).toHaveBeenCalledWith('/autoprint/printer-1/skip');
    });
    await waitFor(() => {
      expect(toast.info).toHaveBeenCalledWith('Skipped next queued job');
    });
  });

  it('calls cancel endpoint when Cancel button is clicked', async () => {
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: {} });
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoPrintStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Cancel auto-dispatch'));
    await waitFor(() => {
      expect(apiClient.post).toHaveBeenCalledWith('/autoprint/printer-1/cancel');
    });
    await waitFor(() => {
      expect(toast.info).toHaveBeenCalledWith('Auto-dispatch cancelled');
    });
  });

  it('shows error toast on confirm failure', async () => {
    vi.mocked(apiClient.post).mockRejectedValueOnce(new Error('Network error'));
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoPrintStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith('Failed to confirm bed clear');
    });
  });

  it('has correct ARIA attributes', () => {
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoPrintStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    const alert = screen.getByRole('alert');
    expect(alert).toHaveAttribute('aria-label', 'Bed clear confirmation required');
  });
});
