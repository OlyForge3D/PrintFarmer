import '@testing-library/jest-dom';
import React from 'react';
import { act, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AutoDispatchStatus, Printer } from '@/types/api';

const {
  autoDispatchStateListeners,
  connectSignalR,
  subscribeToAutoDispatchStateChanged,
} = vi.hoisted(() => ({
  autoDispatchStateListeners: [] as Array<(status: AutoDispatchStatus) => void>,
  connectSignalR: vi.fn().mockResolvedValue(undefined),
  subscribeToAutoDispatchStateChanged: vi.fn((callback: (status: AutoDispatchStatus) => void) => {
    autoDispatchStateListeners.push(callback);
    return () => {
      const index = autoDispatchStateListeners.indexOf(callback);
      if (index >= 0) {
        autoDispatchStateListeners.splice(index, 1);
      }
    };
  }),
}));

vi.mock('@/common/hooks/useApi', () => ({
  queryKeys: {
    printers: ['printers'] as const,
    printer: (id: string) => ['printers', id] as const,
  },
  useJobQueue: () => ({ data: [], isLoading: false }),
}));

vi.mock('@/services/printer-signalr', () => ({
  printerSignalRService: {
    connect: connectSignalR,
    onAutoDispatchStateChanged: subscribeToAutoDispatchStateChanged,
  },
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    get: vi.fn(),
    getAutoDispatchStatus: vi.fn().mockResolvedValue({ printers: [] }),
    getObjectTags: vi.fn().mockResolvedValue([]),
    setAutoDispatchEnabled: vi.fn().mockResolvedValue(undefined),
    post: vi.fn().mockResolvedValue({ data: {} }),
    skipAutoDispatchJob: vi.fn().mockResolvedValue(undefined),
    cancelAutoDispatch: vi.fn().mockResolvedValue(undefined),
  },
}));

vi.mock('@/features/printers/hooks/useFailureDetectionAlert', () => ({
  useFailureDetectionAlert: () => ({ event: undefined }),
}));

vi.mock('@/features/printers/hooks/usePrinterFailureDetectionStatus', () => ({
  usePrinterFailureDetectionStatus: () => ({
    printerStatus: undefined,
    data: undefined,
    isLoading: false,
  }),
}));

vi.mock('@/features/printers/components/PrinterHistoryModal', () => ({
  PrinterHistoryModal: () => null,
}));

vi.mock('@/features/printers/components/PrinterFilesModal', () => ({
  PrinterFilesModal: () => null,
}));

vi.mock('@/features/printers/components/PrintProgressBar', () => ({
  PrintProgressBar: () => <div data-testid="print-progress" />,
}));

vi.mock('@/features/printers/components/FailureDetectionBadge', () => ({
  FailureDetectionBadge: () => null,
}));

vi.mock('@/features/printers/components/FailureDetectionMonitoringBadge', () => ({
  FailureDetectionMonitoringBadge: () => null,
}));

vi.mock('@/features/printers/components/PrinterCameraPreview', () => ({
  PrinterCameraPreview: () => <div data-testid="camera-preview" />,
}));

vi.mock('@/components/TaggingModal', () => ({
  TaggingModal: () => null,
}));

vi.mock('sonner', () => ({
  toast: {
    success: vi.fn(),
    error: vi.fn(),
    info: vi.fn(),
    warning: vi.fn(),
  },
}));

import { apiClient } from '@/services/api';
import { CompactPrinterCard } from '@/features/printers/components/CompactPrinterCard';

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  return ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
}

function makePrinter(overrides: Partial<Printer> = {}): Printer {
  return {
    id: 'test-printer-1',
    name: 'Test Printer',
    serverUrl: 'http://test.local',
    backendPort: 7125,
    backend: 'Moonraker',
    isOnline: true,
    isEnabled: true,
    state: 'Idle',
    hotendTemp: 25,
    hotendTarget: 0,
    bedTemp: 25,
    bedTarget: 0,
    progress: 0,
    fileName: null,
    cameraStreamUrl: null,
    cameraSnapshotUrl: null,
    obicoEnabled: false,
    obicoServerId: null,
    ...overrides,
  } as Printer;
}

function emitAutoDispatchStateChanged(status: AutoDispatchStatus) {
  act(() => {
    autoDispatchStateListeners.forEach((listener) => listener(status));
  });
}

describe('CompactPrinterCard PendingReady live updates', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    autoDispatchStateListeners.splice(0, autoDispatchStateListeners.length);
    vi.mocked(apiClient.getAutoDispatchStatus).mockResolvedValue({ printers: [] });
  });

  it('uses the auto-dispatch hook path to fetch bulk status and subscribe to live updates', async () => {
    const printer = makePrinter();

    render(
      <CompactPrinterCard
        printer={printer}
        onExpand={vi.fn()}
      />,
      { wrapper: createWrapper() },
    );

    expect(await screen.findByText('Idle')).toBeInTheDocument();

    await waitFor(() => {
      expect(apiClient.getAutoDispatchStatus).toHaveBeenCalledTimes(1);
      expect(connectSignalR).toHaveBeenCalledTimes(1);
      expect(subscribeToAutoDispatchStateChanged).toHaveBeenCalledTimes(1);
    });
  });

  it('shows Pending Ready status and bed-clear banner from the initial bulk status snapshot when the bed-clear gate is red', async () => {
    const printer = makePrinter();
    vi.mocked(apiClient.getAutoDispatchStatus).mockResolvedValueOnce({
      printers: [
        {
          printerId: printer.id,
          printerName: printer.name,
          enabled: true,
          isReady: false,
          queueDepth: 1,
          state: 'None',
          bedPreConfirmed: false,
          readyGateChecks: [
            {
              name: 'Bed Clear Confirmed',
              passed: false,
              message: 'Waiting for operator to confirm bed is clear',
              checkedAt: '2026-03-25T00:00:00Z',
            },
          ],
          attentionMessage: 'Print completed. 1 queued job is blocked until you clear the bed and confirm ready.',
        },
      ],
    });

    render(
      <CompactPrinterCard
        printer={printer}
        onExpand={vi.fn()}
      />,
      { wrapper: createWrapper() },
    );

    expect(await screen.findByText('Pending Ready')).toBeInTheDocument();
    expect(screen.getByRole('alert', { name: 'Bed clear confirmation required' })).toBeInTheDocument();
    expect(screen.getByText('Print complete — confirm bed is clear')).toBeInTheDocument();
    expect(screen.getByText('1 job queued')).toBeInTheDocument();
  });

  it('shows Pending Ready status and bed-clear banner after an auto-dispatch SignalR update', async () => {
    const printer = makePrinter();

    render(
      <CompactPrinterCard
        printer={printer}
        onExpand={vi.fn()}
      />,
      { wrapper: createWrapper() },
    );

    expect(await screen.findByText('Idle')).toBeInTheDocument();

    emitAutoDispatchStateChanged({
      printerId: printer.id,
      printerName: printer.name,
      enabled: true,
      isReady: false,
      queueDepth: 2,
      state: 'PendingReady',
      bedPreConfirmed: false,
      readyGateChecks: [
        {
          name: 'Bed Clear Confirmed',
          passed: false,
          message: 'Waiting for operator to confirm bed is clear',
          checkedAt: '2026-03-25T00:00:00Z',
        },
      ],
      attentionMessage: 'Print completed. 2 queued jobs are blocked until you clear the bed and confirm ready.',
    });

    await waitFor(() => {
      expect(screen.getByText('Pending Ready')).toBeInTheDocument();
    });
    expect(screen.getByRole('alert', { name: 'Bed clear confirmation required' })).toBeInTheDocument();
    expect(screen.getByText('Print complete — confirm bed is clear')).toBeInTheDocument();
    expect(screen.getByText('2 jobs queued')).toBeInTheDocument();
  });

  it('keeps the Pending Ready banner when a partial live update omits ready-gate details', async () => {
    const printer = makePrinter();
    vi.mocked(apiClient.getAutoDispatchStatus).mockResolvedValueOnce({
      printers: [
        {
          printerId: printer.id,
          printerName: printer.name,
          enabled: true,
          isReady: false,
          queueDepth: 1,
          state: 'None',
          bedPreConfirmed: false,
          readyGateChecks: [
            {
              name: 'Bed Clear Confirmed',
              passed: false,
              message: 'Waiting for operator to confirm bed is clear',
              checkedAt: '2026-03-25T00:00:00Z',
            },
          ],
          attentionMessage: 'Print completed. 1 queued job is blocked until you clear the bed and confirm ready.',
        },
      ],
    });

    render(
      <CompactPrinterCard
        printer={printer}
        onExpand={vi.fn()}
      />,
      { wrapper: createWrapper() },
    );

    expect(await screen.findByText('Pending Ready')).toBeInTheDocument();
    expect(screen.getByRole('alert', { name: 'Bed clear confirmation required' })).toBeInTheDocument();

    emitAutoDispatchStateChanged({
      printerId: printer.id,
      printerName: printer.name,
      enabled: true,
      isReady: false,
      queueDepth: 1,
      state: 'None',
      bedPreConfirmed: false,
    });

    await waitFor(() => {
      expect(screen.getByText('Pending Ready')).toBeInTheDocument();
    });
    expect(screen.getByRole('alert', { name: 'Bed clear confirmation required' })).toBeInTheDocument();
    expect(screen.getByText('1 job queued')).toBeInTheDocument();
  });

  it('clears the Pending Ready overlay when the backend returns None with no confirmation needed', async () => {
    const printer = makePrinter();

    render(
      <CompactPrinterCard
        printer={printer}
        onExpand={vi.fn()}
      />,
      { wrapper: createWrapper() },
    );

    expect(await screen.findByText('Idle')).toBeInTheDocument();

    emitAutoDispatchStateChanged({
      printerId: printer.id,
      printerName: printer.name,
      enabled: true,
      isReady: false,
      queueDepth: 2,
      state: 'PendingReady',
      bedPreConfirmed: false,
      readyGateChecks: [
        {
          name: 'Bed Clear Confirmed',
          passed: false,
          message: 'Waiting for operator to confirm bed is clear',
          checkedAt: '2026-03-25T00:00:00Z',
        },
      ],
      attentionMessage: 'Print completed. 2 queued jobs are blocked until you clear the bed and confirm ready.',
    });

    expect(await screen.findByText('Pending Ready')).toBeInTheDocument();
    expect(screen.getByRole('alert', { name: 'Bed clear confirmation required' })).toBeInTheDocument();

    emitAutoDispatchStateChanged({
      printerId: printer.id,
      printerName: printer.name,
      enabled: true,
      isReady: false,
      queueDepth: 0,
      state: 'None',
      bedPreConfirmed: false,
      readyGateChecks: [
        {
          name: 'Bed Clear Confirmed',
          passed: false,
          message: 'No confirmation needed yet',
          checkedAt: '2026-03-25T00:00:05Z',
        },
      ],
    });

    await waitFor(() => {
      expect(screen.queryByText('Pending Ready')).not.toBeInTheDocument();
    });
    expect(screen.queryByRole('alert', { name: 'Bed clear confirmation required' })).not.toBeInTheDocument();
    expect(screen.getByText('Idle')).toBeInTheDocument();
  });

  it('shows Pending Ready status and bed-clear banner when a stale summary row still says the operator is waiting for confirmation', async () => {
    const printer = makePrinter();

    render(
      <CompactPrinterCard
        printer={printer}
        onExpand={vi.fn()}
      />,
      { wrapper: createWrapper() },
    );

    expect(await screen.findByText('Idle')).toBeInTheDocument();

    emitAutoDispatchStateChanged({
      printerId: printer.id,
      printerName: printer.name,
      enabled: true,
      isReady: false,
      queueDepth: 2,
      state: 'None',
      bedPreConfirmed: false,
      readyGateChecks: [
        {
          name: 'Bed Clear Confirmed',
          passed: false,
          message: 'Waiting for operator to confirm bed is clear',
          checkedAt: '2026-03-25T00:00:00Z',
        },
      ],
      attentionMessage: 'Print completed. 2 queued jobs are blocked until you clear the bed and confirm ready.',
    });

    await waitFor(() => {
      expect(screen.getByText('Pending Ready')).toBeInTheDocument();
    });
    expect(screen.getByRole('alert', { name: 'Bed clear confirmation required' })).toBeInTheDocument();
    expect(screen.getByText('2 jobs queued')).toBeInTheDocument();
  });

  it('shows Pending Ready status and bed-clear banner when the live row only reports a failed red bed-clear gate with queued work', async () => {
    const printer = makePrinter();

    render(
      <CompactPrinterCard
        printer={printer}
        onExpand={vi.fn()}
      />,
      { wrapper: createWrapper() },
    );

    expect(await screen.findByText('Idle')).toBeInTheDocument();

    emitAutoDispatchStateChanged({
      printerId: printer.id,
      printerName: printer.name,
      enabled: true,
      isReady: false,
      queueDepth: 1,
      state: 'None',
      bedPreConfirmed: false,
      readyGateChecks: [
        {
          name: 'Bed Clear Confirmed',
          passed: false,
          message: '',
          checkedAt: '2026-03-25T00:00:00Z',
        },
      ],
    });

    await waitFor(() => {
      expect(screen.getByText('Pending Ready')).toBeInTheDocument();
    });
    expect(screen.getByRole('alert', { name: 'Bed clear confirmation required' })).toBeInTheDocument();
    expect(screen.getByText('1 job queued')).toBeInTheDocument();
  });
});
