import '@testing-library/jest-dom';
import React from 'react';
import { act, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AutoDispatchStatus, Printer } from '@/types/api';

const { autoPrintStateListeners } = vi.hoisted(() => ({
  autoPrintStateListeners: [] as Array<(status: AutoDispatchStatus) => void>,
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
    connect: vi.fn().mockResolvedValue(undefined),
    onAutoPrintStateChanged: vi.fn((callback: (status: AutoDispatchStatus) => void) => {
      autoPrintStateListeners.push(callback);
      return () => {
        const index = autoPrintStateListeners.indexOf(callback);
        if (index >= 0) {
          autoPrintStateListeners.splice(index, 1);
        }
      };
    }),
  },
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    get: vi.fn(),
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

function emitAutoPrintStateChanged(status: AutoDispatchStatus) {
  act(() => {
    autoPrintStateListeners.forEach((listener) => listener(status));
  });
}

describe('CompactPrinterCard PendingReady live updates', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    autoPrintStateListeners.splice(0, autoPrintStateListeners.length);
    vi.mocked(apiClient.get).mockResolvedValue({ data: { printers: [] } });
  });

  it('shows Pending Ready status and bed-clear banner after an auto-print SignalR update', async () => {
    const printer = makePrinter();

    render(
      <CompactPrinterCard
        printer={printer}
        onExpand={vi.fn()}
      />,
      { wrapper: createWrapper() },
    );

    expect(await screen.findByText('Idle')).toBeInTheDocument();

    emitAutoPrintStateChanged({
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
});
