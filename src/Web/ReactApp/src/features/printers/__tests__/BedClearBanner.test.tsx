import '@testing-library/jest-dom';
import React from 'react';
import { act, render, renderHook, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { AutoDispatchReadyResult, AutoDispatchStatus } from '@/types/api';

vi.mock('@/services/api', () => ({
  apiClient: {
    getSettings: vi.fn().mockResolvedValue({ logLevel: 'Information', consoleLoggingEnabled: false }),
    confirmAutoDispatchReady: vi.fn().mockResolvedValue({}),
    skipAutoDispatchJob: vi.fn().mockResolvedValue(undefined),
    cancelAutoDispatch: vi.fn().mockResolvedValue(undefined),
    preClearAutoDispatchBed: vi.fn().mockResolvedValue({}),
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

vi.mock('@/features/printers/components/FilamentOverrideModal', () => ({
  FilamentOverrideModal: (props: {
    isOpen: boolean;
    onCancel: () => void;
    onConfirm: () => void;
    filamentCheck: { message?: string; outcome?: string } | null;
  }) => {
    if (!props.isOpen || !props.filamentCheck) return null;
    return (
      <div data-testid="filament-override-modal">
        <span data-testid="filament-warning">{props.filamentCheck.message}</span>
        <span data-testid="filament-outcome">{props.filamentCheck.outcome}</span>
        <button data-testid="print-anyway-btn" onClick={props.onConfirm}>
          Confirm and Dispatch Anyway
        </button>
        <button data-testid="cancel-modal-btn" onClick={props.onCancel}>
          Cancel
        </button>
      </div>
    );
  },
}));

import { BedClearBanner } from '../components/BedClearBanner';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import { usePreClearBed } from '@/features/printers/hooks/useAutoDispatch';

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
  dispatchStateETag: 'dispatch-v1',
  printerETag: 'printer-v1',
  nextJobId: 'job-1',
  nextJobETag: 'job-v1',
  nextJobKind: 'Standard',
};

const readyResultWithJob: AutoDispatchReadyResult = {
  status: {
    printerId: 'printer-1',
    enabled: true,
    state: 'Ready',
    queueDepth: 1,
    dispatchStateETag: 'dispatch-v2',
    printerETag: 'printer-v1',
  },
  nextJob: {
    id: 'job-1',
    name: 'benchy.gcode',
    jobKind: 'Standard',
    jobETag: 'job-v1',
    estimatedFilamentUsageG: 10,
  },
  filamentCheck: {
    outcome: 'Compatible',
    sufficient: true,
    materialMismatch: false,
  },
  dispatchInitiated: true,
};

const readyResultNoJob: AutoDispatchReadyResult = {
  status: {
    printerId: 'printer-1',
    enabled: true,
    state: 'None',
    queueDepth: 0,
    dispatchStateETag: 'dispatch-v2',
    printerETag: 'printer-v1',
  },
  nextJob: undefined,
  filamentCheck: undefined,
};

const noneStateWithFailedGate: AutoDispatchStatus = {
  printerId: 'printer-1',
  enabled: true,
  state: 'None',
  queueDepth: 1,
  readyGateChecks: [
    {
      name: 'Bed Clear Confirmed',
      passed: false,
      message: 'Waiting for operator to confirm bed is clear',
      checkedAt: '2026-03-25T00:00:00Z',
    },
  ],
  attentionMessage: 'Print completed. 1 queued job is blocked until you clear the bed and confirm ready.',
};

const readyResultMaterialMismatch: AutoDispatchReadyResult = {
  status: {
    printerId: 'printer-1',
    enabled: true,
    state: 'PendingReady',
    queueDepth: 1,
    dispatchStateETag: 'dispatch-v2',
    printerETag: 'printer-v1',
  },
  nextJob: {
    id: 'job-2',
    name: 'part.gcode',
    jobKind: 'Standard',
    jobETag: 'job-v1',
  },
  filamentCheck: {
    outcome: 'Incompatible',
    sufficient: false,
    materialMismatch: true,
    loadedMaterial: 'PLA',
    requiredMaterial: 'PETG',
    message: 'Material mismatch: loaded PLA, job requires PETG',
  },
  dispatchInitiated: false,
  requiresFilamentOverride: true,
  filamentCheckETag: 'filament-check-v1',
};

const readyResultInsufficientFilament: AutoDispatchReadyResult = {
  status: {
    printerId: 'printer-1',
    enabled: true,
    state: 'PendingReady',
    queueDepth: 1,
    dispatchStateETag: 'dispatch-v2',
    printerETag: 'printer-v1',
  },
  nextJob: {
    id: 'job-3',
    name: 'big-part.gcode',
    jobKind: 'Standard',
    jobETag: 'job-v1',
  },
  filamentCheck: {
    outcome: 'Incompatible',
    sufficient: false,
    materialMismatch: false,
    message: 'Only 50g remaining, job requires 200g',
  },
  dispatchInitiated: false,
  requiresFilamentOverride: true,
  filamentCheckETag: 'filament-check-v1',
};

const readyResultUnknownFilament: AutoDispatchReadyResult = {
  status: {
    printerId: 'printer-1',
    enabled: true,
    state: 'PendingReady',
    queueDepth: 1,
    dispatchStateETag: 'dispatch-v1',
    printerETag: 'printer-v1',
  },
  nextJob: {
    id: 'job-4',
    name: 'unknown-filament.gcode',
    jobKind: 'Standard',
    jobETag: 'job-v1',
  },
  filamentCheck: {
    outcome: 'Unknown',
    sufficient: false,
    materialMismatch: false,
    message: 'Spoolman is unavailable, so the assigned spool could not be verified.',
  },
  dispatchInitiated: false,
  requiresFilamentOverride: true,
  filamentCheckETag: 'filament-check-v1',
};

const readyResultOverrideDispatched: AutoDispatchReadyResult = {
  ...readyResultMaterialMismatch,
  status: {
    ...readyResultMaterialMismatch.status,
    state: 'Ready',
    dispatchStateETag: 'dispatch-v2',
  },
  dispatchInitiated: true,
  requiresFilamentOverride: false,
  filamentOverrideApplied: true,
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

  it('renders nothing when the backend says no confirmation is needed', () => {
    const status = {
      ...baseStatus,
      state: 'None' as const,
      queueDepth: 0,
      readyGateChecks: [
        {
          name: 'Bed Clear Confirmed',
          passed: false,
          message: 'No confirmation needed yet',
          checkedAt: '2026-03-25T00:00:00Z',
        },
      ],
    };
    const { container } = render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={status} />,
      { wrapper: createWrapper() },
    );
    expect(container.firstChild).toBeNull();
  });

  it('renders from the failed bed-clear gate even when state is None', () => {
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={noneStateWithFailedGate} />,
      { wrapper: createWrapper() },
    );
    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.getByText(/confirm bed is clear/i)).toBeInTheDocument();
    expect(screen.getByText(/1 job queued/)).toBeInTheDocument();
  });

  it('renders nothing when live printer state is Paused but auto-dispatch is stale PendingReady', () => {
    const { container } = render(
      <BedClearBanner
        printerId="printer-1"
        printerName="MK4"
        autoDispatchStatus={baseStatus}
        printerState="Paused"
      />,
      { wrapper: createWrapper() },
    );

    expect(container.firstChild).toBeNull();
  });

  it('renders from a failed bed-clear gate with queued work even when the gate copy is blank', () => {
    const status: AutoDispatchStatus = {
      printerId: 'printer-1',
      enabled: true,
      state: 'None',
      queueDepth: 1,
      readyGateChecks: [
        {
          name: 'Bed Clear Confirmed',
          passed: false,
          message: '',
          checkedAt: '2026-03-25T00:00:00Z',
        },
      ],
    };

    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={status} />,
      { wrapper: createWrapper() },
    );

    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.getByText(/1 job queued/)).toBeInTheDocument();
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
    vi.mocked(apiClient.confirmAutoDispatchReady).mockResolvedValueOnce(readyResultWithJob);
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(apiClient.confirmAutoDispatchReady).toHaveBeenCalledWith(
        'printer-1',
        'dispatch-v1',
      );
    });
    await waitFor(() => {
      expect(toast.success).toHaveBeenCalledWith('Dispatching "benchy.gcode" to MK4');
    });
  });

  it('optimistically updates printer cache to Starting state on dispatch', async () => {
    vi.mocked(apiClient.confirmAutoDispatchReady).mockResolvedValueOnce(readyResultWithJob);
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
    vi.mocked(apiClient.confirmAutoDispatchReady).mockResolvedValueOnce(readyResultNoJob);
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    const existingPrinter = { id: 'printer-1', name: 'MK4', state: 'Idle' };
    queryClient.setQueryData(['printers'], [existingPrinter]);
    queryClient.setQueryData(['auto-dispatch', 'all-statuses'], [{ ...baseStatus, printerName: 'MK4' }]);
    queryClient.setQueryData(['auto-dispatch', 'status', 'printer-1'], { ...baseStatus, printerName: 'MK4' });
    queryClient.setQueryData(['auto-dispatch', 'global-status'], {
      globalEnabled: true,
      printers: [{ ...baseStatus, printerName: 'MK4' }],
    });

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

    const updatedStatusList = queryClient.getQueryData<AutoDispatchStatus[]>(['auto-dispatch', 'all-statuses']);
    expect(updatedStatusList?.[0]).toMatchObject({
      printerId: 'printer-1',
      state: 'None',
      queueDepth: 0,
      printerName: 'MK4',
    });

    const updatedSingleStatus = queryClient.getQueryData<AutoDispatchStatus>(['auto-dispatch', 'status', 'printer-1']);
    expect(updatedSingleStatus).toMatchObject({
      printerId: 'printer-1',
      state: 'None',
      queueDepth: 0,
    });
  });

  it('shows success without dispatch when no jobs queued', async () => {
    vi.mocked(apiClient.confirmAutoDispatchReady).mockResolvedValueOnce(readyResultNoJob);
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(toast.success).toHaveBeenCalledWith('Bed clear confirmed for MK4 — no jobs queued');
    });
  });

  it('shows the server mismatch in an explicit override modal without claiming dispatch', async () => {
    vi.mocked(apiClient.confirmAutoDispatchReady).mockResolvedValueOnce(readyResultMaterialMismatch);
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(screen.getByTestId('filament-override-modal')).toBeInTheDocument();
    });
    expect(screen.getByTestId('filament-warning')).toHaveTextContent(
      'Material mismatch: loaded PLA, job requires PETG',
    );
    expect(screen.getByTestId('filament-outcome')).toHaveTextContent('Incompatible');
    expect(toast.success).not.toHaveBeenCalled();
    expect(toast.warning).not.toHaveBeenCalled();
  });

  it('retries with an explicit server override and only reports dispatch after acceptance', async () => {
    vi.mocked(apiClient.confirmAutoDispatchReady)
      .mockResolvedValueOnce(readyResultMaterialMismatch)
      .mockResolvedValueOnce(readyResultOverrideDispatched);
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(screen.getByTestId('filament-override-modal')).toBeInTheDocument();
    });
    fireEvent.click(screen.getByTestId('print-anyway-btn'));
    await waitFor(() => {
      expect(apiClient.confirmAutoDispatchReady).toHaveBeenNthCalledWith(
        2,
        'printer-1',
        'dispatch-v2',
        true,
        'job-v1',
        'filament-check-v1',
      );
    });
    await waitFor(() => {
      expect(toast.success).toHaveBeenCalledWith(
        'Dispatching "part.gcode" to MK4 (filament override confirmed)',
      );
    });
  });

  it('shows a changed filament reason before accepting a retry', async () => {
    const changedChallenge: AutoDispatchReadyResult = {
      ...readyResultMaterialMismatch,
      filamentCheckETag: 'filament-check-v2',
      filamentCheckChanged: true,
      filamentCheck: {
        ...readyResultMaterialMismatch.filamentCheck!,
        message: 'Only 20g remaining, job requires 100g',
      },
    };
    vi.mocked(apiClient.confirmAutoDispatchReady)
      .mockResolvedValueOnce(readyResultUnknownFilament)
      .mockResolvedValueOnce(changedChallenge);
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(screen.getByTestId('filament-override-modal')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('print-anyway-btn'));

    await waitFor(() => {
      expect(apiClient.confirmAutoDispatchReady).toHaveBeenNthCalledWith(
        2,
        'printer-1',
        'dispatch-v1',
        true,
        'job-v1',
        'filament-check-v1',
      );
      expect(screen.getByTestId('filament-warning')).toHaveTextContent(
        'Only 20g remaining, job requires 100g',
      );
    });
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('closes modal when user cancels material mismatch', async () => {
    vi.mocked(apiClient.confirmAutoDispatchReady).mockResolvedValueOnce(readyResultMaterialMismatch);
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(screen.getByTestId('filament-override-modal')).toBeInTheDocument();
    });
    fireEvent.click(screen.getByTestId('cancel-modal-btn'));
    await waitFor(() => {
      expect(screen.queryByTestId('filament-override-modal')).not.toBeInTheDocument();
    });
  });

  it('requires explicit confirmation for known insufficient filament', async () => {
    vi.mocked(apiClient.confirmAutoDispatchReady).mockResolvedValueOnce(readyResultInsufficientFilament);
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(screen.getByTestId('filament-override-modal')).toBeInTheDocument();
    });
    expect(screen.getByTestId('filament-warning')).toHaveTextContent(
      'Only 50g remaining, job requires 200g',
    );
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('requires explicit confirmation when filament data is unknown', async () => {
    vi.mocked(apiClient.confirmAutoDispatchReady).mockResolvedValueOnce(readyResultUnknownFilament);
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );

    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));

    await waitFor(() => {
      expect(screen.getByTestId('filament-override-modal')).toBeInTheDocument();
    });
    expect(screen.getByTestId('filament-outcome')).toHaveTextContent('Unknown');
    expect(screen.getByTestId('filament-warning')).toHaveTextContent(
      'Spoolman is unavailable, so the assigned spool could not be verified.',
    );
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('calls skip endpoint when Skip button is clicked', async () => {
    vi.mocked(apiClient.skipAutoDispatchJob).mockResolvedValueOnce(undefined);
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Skip next queued job'));
    await waitFor(() => {
      expect(apiClient.skipAutoDispatchJob).toHaveBeenCalledWith(
        'printer-1',
        'dispatch-v1',
        'job-v1',
      );
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
      expect(apiClient.cancelAutoDispatch).toHaveBeenCalledWith(
        'printer-1',
        'dispatch-v1',
      );
    });
    await waitFor(() => {
      expect(toast.info).toHaveBeenCalledWith('Auto-dispatch cancelled');
    });
  });

  it('shows error toast on confirm failure', async () => {
    vi.mocked(apiClient.confirmAutoDispatchReady).mockRejectedValueOnce(new Error('Network error'));
    render(
      <BedClearBanner printerId="printer-1" printerName="MK4" autoDispatchStatus={baseStatus} />,
      { wrapper: createWrapper() },
    );
    fireEvent.click(screen.getByLabelText('Confirm bed clear for MK4'));
    await waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith('Network error');
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

  it('does not announce successful pre-clear when filament confirmation blocks it', async () => {
    vi.mocked(apiClient.preClearAutoDispatchBed).mockResolvedValueOnce({
      ...baseStatus,
      bedPreConfirmed: false,
      attentionMessage: 'Filament confirmation is required before dispatch.',
    });
    const { result } = renderHook(() => usePreClearBed(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync(baseStatus);
    });

    expect(toast.warning).toHaveBeenCalledWith(
      'Filament confirmation is required before dispatch.',
    );
    expect(toast.success).not.toHaveBeenCalled();
  });
});
