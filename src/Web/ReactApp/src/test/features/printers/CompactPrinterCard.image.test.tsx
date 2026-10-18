import '@testing-library/jest-dom';
import React from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
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
  useFailureDetectionHistory: () => ({ data: [], isLoading: false, isError: false }),
  usePrintSessionTimeline: () => ({ data: undefined, isLoading: false, isError: false }),
}));

vi.mock('@/services/printer-signalr', () => ({
  printerSignalRService: {
    connect: connectSignalR,
    onAutoDispatchStateChanged: subscribeToAutoDispatchStateChanged,
    onFilamentCoverageChanged: vi.fn(() => () => {}),
  },
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    get: vi.fn(),
    getObjectTags: vi.fn().mockResolvedValue([]),
    getObjectsTags: vi.fn().mockResolvedValue([]),
    post: vi.fn().mockResolvedValue({ data: {} }),
  },
}));

vi.mock('@/services/api/queueSummariesApi', () => ({
  getPrinterQueueSummaries: vi.fn().mockResolvedValue([]),
}));

vi.mock('@/services/api/autoDispatchApi', () => ({
  getAutoDispatchStatus: vi.fn().mockResolvedValue({ globalEnabled: false, printers: [] }),
  setAutoDispatchEnabled: vi.fn().mockResolvedValue(undefined),
  skipAutoDispatchJob: vi.fn().mockResolvedValue(undefined),
  cancelAutoDispatch: vi.fn().mockResolvedValue(undefined),
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

vi.mock('@/services/assetService', () => ({
  assetService: {
    getCoverImageUrl: (manufacturer: string, model: string) => `/covers/${manufacturer}/${model}.png`,
    getFallbackImageUrl: () => '/assets/printers/generic-corexy.svg',
  },
}));

vi.mock('sonner', () => ({
  toast: {
    success: vi.fn(),
    error: vi.fn(),
    info: vi.fn(),
    warning: vi.fn(),
  },
}));

import { getAutoDispatchStatus } from '@/services/api/autoDispatchApi';
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

function getTileImage(): HTMLImageElement {
  const img = screen.getByTestId('compact-printer-image').querySelector('img');
  if (!img) throw new Error('printer image not rendered');
  return img;
}

describe('CompactPrinterCard printer image', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(getAutoDispatchStatus).mockResolvedValue({ globalEnabled: false, printers: [] });
  });

  it('renders the model cover image and identity above the progress bar', async () => {
    render(
      <CompactPrinterCard
        printer={makePrinter({ manufacturerName: 'Prusa', modelName: 'MK4' })}
        onExpand={vi.fn()}
      />,
      { wrapper: createWrapper() },
    );

    await screen.findByTestId('compact-printer-image');
    const image = getTileImage();
    expect(image).toHaveAttribute('src', '/covers/Prusa/MK4.png');
    expect(screen.getByText('MK4')).toBeInTheDocument();
    expect(screen.getByText('Prusa')).toBeInTheDocument();

    const imageRow = screen.getByTestId('compact-printer-image');
    const progress = screen.getByTestId('print-progress');
    expect(imageRow.compareDocumentPosition(progress) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('marks the image decorative because the identity is already rendered as text', async () => {
    render(
      <CompactPrinterCard
        printer={makePrinter({ manufacturerName: 'Prusa', modelName: 'MK4' })}
        onExpand={vi.fn()}
      />,
      { wrapper: createWrapper() },
    );

    await screen.findByTestId('compact-printer-image');
    expect(getTileImage()).toHaveAttribute('alt', '');
    expect(screen.queryByRole('img', { name: /Prusa|MK4/ })).not.toBeInTheDocument();
  });

  it('falls back to the generic motion-type image when the cover fails to load', async () => {
    render(
      <CompactPrinterCard
        printer={makePrinter({ manufacturerName: 'Prusa', modelName: 'MK4' })}
        onExpand={vi.fn()}
      />,
      { wrapper: createWrapper() },
    );

    await screen.findByTestId('compact-printer-image');
    fireEvent.error(getTileImage());
    expect(getTileImage()).toHaveAttribute('src', '/assets/printers/generic-corexy.svg');
  });

  it('retries the cover image after a failed load when the printer model changes', async () => {
    const { rerender } = render(
      <CompactPrinterCard
        printer={makePrinter({ manufacturerName: 'Prusa', modelName: 'MK4' })}
        onExpand={vi.fn()}
      />,
      { wrapper: createWrapper() },
    );

    await screen.findByTestId('compact-printer-image');
    fireEvent.error(getTileImage());
    expect(getTileImage()).toHaveAttribute('src', '/assets/printers/generic-corexy.svg');

    rerender(
      <CompactPrinterCard
        printer={makePrinter({ manufacturerName: 'Bambu Lab', modelName: 'P1S' })}
        onExpand={vi.fn()}
      />,
    );

    expect(getTileImage()).toHaveAttribute('src', '/covers/Bambu Lab/P1S.png');
  });

  it('uses the generic image when no catalog model is linked', async () => {
    render(
      <CompactPrinterCard printer={makePrinter()} onExpand={vi.fn()} />,
      { wrapper: createWrapper() },
    );

    await screen.findByTestId('compact-printer-image');
    expect(getTileImage()).toHaveAttribute('src', '/assets/printers/generic-corexy.svg');
  });
});