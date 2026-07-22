import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import type {
  AutoDispatchStatus,
  FailureDetectionEvent,
  FailureDetectionMonitorStatusDto,
  FailureDetectionPrinterStatusDto,
  Printer,
} from '@/types/api';

let failureDetectionStatusMock: FailureDetectionMonitorStatusDto | undefined;
let failureDetectionPrinterStatusMock: FailureDetectionPrinterStatusDto | undefined;
let autoDispatchStatusMock: AutoDispatchStatus | null = null;

// ── Mocks for CompactPrinterCard dependencies ──

vi.mock('@/common/hooks/useApi', () => ({
  usePrinters: () => ({ data: [], isLoading: false }),
  usePrinterDetails: () => ({ data: undefined, isLoading: false }),
  useJobQueue: () => ({ data: [], isLoading: false }),
  useFailureDetectionHistory: () => ({ data: [], isLoading: false, isError: false }),
  usePrintSessionTimeline: () => ({ data: undefined, isLoading: false, isError: false }),
}));

vi.mock('@/common/hooks/useSpoolmanConfigured', () => ({
  useSpoolmanConfigured: () => ({ ready: false }),
}));

vi.mock('@/common/hooks/usePrinterDisplay', () => ({
  usePrinterDisplay: (printer: Printer) => printer,
}));

vi.mock('@tanstack/react-query', () => ({
  useQueryClient: () => ({
    invalidateQueries: vi.fn(),
    setQueryData: vi.fn(),
  }),
  useQuery: () => ({ data: undefined, isLoading: false }),
  useMutation: () => ({
    mutate: vi.fn(),
    mutateAsync: vi.fn().mockResolvedValue({ success: true }),
    isPending: false,
  }),
}));

vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAutoDispatchStatus: () => ({ data: autoDispatchStatusMock, isLoading: false }),
  useSetAutoDispatchEnabled: () => ({ mutateAsync: vi.fn() }),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinterDetails: vi.fn().mockResolvedValue({}),
  },
}));

vi.mock('@/features/printers/hooks/usePrinterFailureDetectionStatus', () => ({
  usePrinterFailureDetectionStatus: () => ({
    printerStatus: failureDetectionPrinterStatusMock ?? failureDetectionStatusMock?.printers[0],
    data: failureDetectionStatusMock,
    isLoading: false,
  }),
}));

vi.mock('@/features/printers/components/PrinterCameraPreview', () => ({
  PrinterCameraPreview: ({
    overlay,
  }: {
    overlay?: ReactNode;
  }) => (
    <div data-testid="camera-preview">
      {overlay}
    </div>
  ),
}));

const failureDetectionListeners: Array<(event: FailureDetectionEvent) => void> = [];

vi.mock('@/services/printer-signalr', () => ({
  printerSignalRService: {
    onFailureDetected: vi.fn((callback: (event: FailureDetectionEvent) => void) => {
      failureDetectionListeners.push(callback);
      return () => {
        const index = failureDetectionListeners.indexOf(callback);
        if (index >= 0) {
          failureDetectionListeners.splice(index, 1);
        }
      };
    }),
  },
}));

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

vi.mock('lucide-react', () => ({
  PanelRightOpen: () => <span data-testid="panel-icon" />,
  Zap: () => <span data-testid="zap-icon" />,
}));

vi.mock('@/features/printers/components/PrinterHistoryModal', () => ({
  PrinterHistoryModal: () => null,
}));

vi.mock('@/features/printers/components/PrinterFilesModal', () => ({
  PrinterFilesModal: () => null,
}));

vi.mock('@/features/printers/components/SpoolPickerModal', () => ({
  SpoolPickerModal: () => null,
}));

vi.mock('@/features/printers/components/ToolheadSpoolPicker', () => ({
  ToolheadSpoolPicker: () => <div data-testid="toolhead-spool-picker" />,
}));

vi.mock('@/features/printers/components/TemperatureControlSection', () => ({
  TemperatureControlSection: () => <div data-testid="temp-section" />,
}));

vi.mock('@/features/printers/components/MovementControlSection', () => ({
  MovementControlSection: () => <div data-testid="movement-section" />,
}));

vi.mock('@/features/printers/components/FilamentControlSection', () => ({
  FilamentControlSection: () => <div data-testid="filament-section" />,
}));

vi.mock('@/features/printers/components/PrinterActionBar', () => ({
  PrinterActionBar: () => <div data-testid="action-bar" />,
}));

vi.mock('@/features/printers/components/BedClearBanner', () => ({
  BedClearBanner: () => <div data-testid="bed-clear-banner" />,
}));

vi.mock('@/features/printers/components/PrintProgressBar', () => ({
  PrintProgressBar: () => <div data-testid="print-progress" />,
}));

vi.mock('@/components/TaggingModal', () => ({
  TaggingModal: () => null,
}));

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

function emitFailureDetected(event: FailureDetectionEvent) {
  failureDetectionListeners.forEach(listener => listener(event));
}

beforeEach(() => {
  failureDetectionListeners.splice(0, failureDetectionListeners.length);
  failureDetectionStatusMock = undefined;
  failureDetectionPrinterStatusMock = undefined;
  autoDispatchStatusMock = null;
});

// ── FailureDetectionEvent type shape test ──

describe('FailureDetectionEvent type', () => {
  it('should have the correct shape when used as an object', async () => {
    const event = {
      printerId: 'abc-123',
      printerName: 'My Printer',
      jobId: 'job-1',
      confidence: 0.855,
      detectedAt: '2026-01-01T00:00:00Z',
      snapshotUrl: 'http://example.com/snapshot.jpg',
      autoPaused: true,
    } satisfies import('@/types/api').FailureDetectionEvent;

    expect(event.printerId).toBe('abc-123');
    expect(event.printerName).toBe('My Printer');
    expect(event.confidence).toBe(0.855);
    expect(event.detectedAt).toBe('2026-01-01T00:00:00Z');
    expect(event.snapshotUrl).toBe('http://example.com/snapshot.jpg');
    expect(event.autoPaused).toBe(true);
    expect(event.jobId).toBe('job-1');
  });

  it('should allow optional jobId to be undefined', () => {
    const event = {
      printerId: 'abc-123',
      printerName: 'Printer',
      confidence: 0.5,
      detectedAt: '2026-01-01T00:00:00Z',
      autoPaused: false,
    } satisfies import('@/types/api').FailureDetectionEvent;

    expect(event.jobId).toBeUndefined();
  });
});

// ── ShieldIcon tests ──

describe('ShieldIcon', () => {
  beforeEach(() => {
    vi.resetModules();
  });

  it('renders without errors', async () => {
    // Import directly — ShieldIcon is a simple SVG component
    const { ShieldIcon } = await import('@/common/components/icons/MdiIcons');
    render(<ShieldIcon />);
    const svg = screen.getByRole('img');
    expect(svg).toBeTruthy();
  });

  it('renders with custom ariaLabel', async () => {
    const { ShieldIcon } = await import('@/common/components/icons/MdiIcons');
    render(<ShieldIcon ariaLabel="ML Monitoring Active" />);
    const svg = screen.getByLabelText('ML Monitoring Active');
    expect(svg).toBeTruthy();
  });
});

function makeFailureDetectionStatus(
  printerId: string,
  printerName = 'Test Printer'
): FailureDetectionMonitorStatusDto {
  return {
    monitoringEnabled: true,
    confidenceThreshold: 0.8,
    scanIntervalSeconds: 30,
    autoPauseOnFailure: true,
    configuredPrinterCount: 1,
    activelyMonitoredPrinterCount: 1,
    lastAnalyzedPrinterCount: 1,
    lastFailureCount: 0,
    lastScanCompletedAt: '2026-01-01T00:00:00Z',
    printers: [
      {
        printerId,
        printerName,
        state: 'monitoring',
        reason: 'Monitoring via pooled server.',
        isPrinting: true,
        detectionSource: 'pooled',
        detectionTarget: 'Primary',
        lastAnalyzedAt: '2026-01-01T00:00:00Z',
        lastOutcome: 'healthy',
        lastConfidence: 0.12,
      },
    ],
  };
}

// ── CompactPrinterCard monitoring badge tests ──

describe('CompactPrinterCard monitoring badge', () => {
  it('shows guarding badge when printer is actively monitored', async () => {
    const { CompactPrinterCard } = await import(
      '@/features/printers/components/CompactPrinterCard'
    );

    const printer = makePrinter({
      obicoEnabled: true,
      state: 'Printing',
      isOnline: true,
    });
    failureDetectionStatusMock = makeFailureDetectionStatus(printer.id, printer.name);

    render(
      <CompactPrinterCard
        printer={printer}
        onExpand={vi.fn()}
      />
    );

    // Summary widget shows "Guarding" text; shield badge is icon-only with aria label
    expect(screen.getAllByText('Guarding').length).toBeGreaterThanOrEqual(1);
    // Shield icon with proper aria label should be present
    expect(screen.getByLabelText('Failure detection guarding')).toBeTruthy();
    // Button should exist with tooltip
    expect(screen.getByRole('button', { name: /open spaghetti detection details/i })).toHaveAttribute('title', expect.stringContaining('Guarding'));
  });

  it('does NOT show guarding badge when printer does not have Obico monitoring enabled', async () => {
    const { CompactPrinterCard } = await import(
      '@/features/printers/components/CompactPrinterCard'
    );

    const printer = makePrinter({
      obicoEnabled: false,
      state: 'Printing',
      isOnline: true,
    });

    render(
      <CompactPrinterCard
        printer={printer}
        onExpand={vi.fn()}
      />
    );

    expect(screen.queryByText('Guarding')).toBeNull();
  });

  it('shows a ready badge when monitoring is enabled but the printer is idle', async () => {
    const { CompactPrinterCard } = await import(
      '@/features/printers/components/CompactPrinterCard'
    );

    const printer = makePrinter({
      obicoEnabled: true,
      state: 'Idle',
      isOnline: true,
    });
    failureDetectionStatusMock = {
      ...makeFailureDetectionStatus(printer.id, printer.name),
      activelyMonitoredPrinterCount: 0,
      printers: [
        {
          ...makeFailureDetectionStatus(printer.id, printer.name).printers[0],
          state: 'idle',
          isPrinting: false,
          reason: 'Printer is not actively printing.',
          lastOutcome: 'none',
          lastAnalyzedAt: undefined,
        },
      ],
    };

    render(
      <CompactPrinterCard
        printer={printer}
        onExpand={vi.fn()}
      />
    );

    // Icon-only badge, no inline text
    expect(screen.queryByText('Ready')).not.toBeInTheDocument();
    // Button should exist with tooltip showing "Ready"
    expect(screen.getByRole('button', { name: /open spaghetti detection details/i })).toHaveAttribute('title', expect.stringContaining('Ready'));
  });

  it('shows the bed-clear overlay when auto-dispatch status is PendingReady', async () => {
    const { CompactPrinterCard } = await import(
      '@/features/printers/components/CompactPrinterCard'
    );

    const printer = makePrinter({
      state: 'Idle',
      isOnline: true,
    });
    autoDispatchStatusMock = {
      printerId: printer.id,
      printerName: printer.name,
      enabled: true,
      isReady: false,
      queueDepth: 2,
      readyGateChecks: [],
      state: 'PendingReady',
      bedPreConfirmed: false,
    };

    render(
      <CompactPrinterCard
        printer={printer}
        onExpand={vi.fn()}
      />
    );

    const banner = screen.getByTestId('bed-clear-banner');
    expect(banner).toBeInTheDocument();
    expect(banner.parentElement?.parentElement).toHaveClass('absolute', 'inset-0', 'z-10');
  });

  it('shows the bed-clear overlay when the bulk status row exposes a failed bed-clear gate even if state is stale', async () => {
    const { CompactPrinterCard } = await import(
      '@/features/printers/components/CompactPrinterCard'
    );

    const printer = makePrinter({
      state: 'Idle',
      isOnline: true,
    });
    autoDispatchStatusMock = {
      printerId: printer.id,
      printerName: printer.name,
      enabled: true,
      isReady: false,
      queueDepth: 2,
      readyGateChecks: [
        {
          name: 'Bed Clear Confirmed',
          passed: false,
          message: 'Waiting for operator to confirm bed is clear',
          checkedAt: '2026-03-25T00:00:00Z',
        },
      ],
      attentionMessage: 'Print completed. 2 queued jobs are blocked until you clear the bed and confirm ready.',
      state: 'None',
      bedPreConfirmed: false,
    };

    render(
      <CompactPrinterCard
        printer={printer}
        onExpand={vi.fn()}
      />
    );

    expect(screen.getByText('Pending Ready')).toBeInTheDocument();
    const banner = screen.getByTestId('bed-clear-banner');
    expect(banner).toBeInTheDocument();
    expect(banner.parentElement?.parentElement).toHaveClass('absolute', 'inset-0', 'z-10');
  });

  it('does not show attention on the camera preview while a dispatched print is starting', async () => {
    const { CompactPrinterCard } = await import(
      '@/features/printers/components/CompactPrinterCard'
    );

    const printer = makePrinter({
      state: 'Starting...',
      isOnline: true,
      obicoEnabled: true,
      cameraSnapshotUrl: 'http://printer.local/webcam/?action=snapshot',
    });

    failureDetectionPrinterStatusMock = {
      printerId: printer.id,
      printerName: printer.name,
      state: 'error',
      reason: 'Failed to contact Obico ML service.',
      isPrinting: true,
      detectionSource: 'global',
      lastOutcome: 'error',
      lastAnalyzedAt: '2026-01-01T00:00:00Z',
      lastConfidence: null,
      lastAutoPaused: false,
    };

    render(
      <CompactPrinterCard
        printer={printer}
        onExpand={vi.fn()}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: 'Show camera preview' }));

    expect(screen.getByTestId('camera-preview')).toBeInTheDocument();
    expect(screen.queryByText('Attention')).not.toBeInTheDocument();
    expect(screen.queryByText(/Needs attention/)).not.toBeInTheDocument();
  });

  it('shows a recent failure badge when a matching failure event arrives', async () => {
    const { CompactPrinterCard } = await import(
      '@/features/printers/components/CompactPrinterCard'
    );

    const printer = makePrinter({
      obicoEnabled: true,
      state: 'Printing',
      isOnline: true,
    });

    render(
      <CompactPrinterCard
        printer={printer}
        onExpand={vi.fn()}
      />
    );

    act(() => {
      emitFailureDetected({
        printerId: printer.id,
        printerName: printer.name,
        confidence: 0.87,
        detectedAt: '2026-01-01T00:00:00Z',
        autoPaused: false,
        snapshotUrl: 'http://example.com/snapshot.jpg',
      });
    });

    expect(screen.getByText('Failure: 87%')).toBeTruthy();
    expect(screen.getByText('Review required')).toBeTruthy();
    expect(screen.queryByText('1 incident')).not.toBeInTheDocument();
  });
});

// ── DetailedPrinterCard monitoring badge tests ──

describe('DetailedPrinterCard monitoring badge', () => {
  it('shows guarding badge when printer is actively monitored', async () => {
    const { DetailedPrinterCard } = await import(
      '@/features/printers/components/DetailedPrinterCard'
    );

    const printer = makePrinter({
      obicoEnabled: true,
      state: 'Printing',
      isOnline: true,
    });
    failureDetectionStatusMock = makeFailureDetectionStatus(printer.id, printer.name);

    render(
      <DetailedPrinterCard
        printer={printer}
      />
    );

    // Summary widget shows "Guarding" text; shield badge is icon-only with aria label
    expect(screen.getAllByText('Guarding').length).toBeGreaterThanOrEqual(1);
    // Shield icon with proper aria label should be present
    expect(screen.getByLabelText('Failure detection guarding')).toBeTruthy();
    // Button should exist with tooltip
    expect(screen.getByRole('button', { name: /open spaghetti detection details/i })).toHaveAttribute('title', expect.stringContaining('Guarding'));
  });

  it('does NOT show guarding badge when printer does not have Obico monitoring enabled', async () => {
    const { DetailedPrinterCard } = await import(
      '@/features/printers/components/DetailedPrinterCard'
    );

    const printer = makePrinter({
      obicoEnabled: false,
      state: 'Printing',
      isOnline: true,
    });

    render(
      <DetailedPrinterCard
        printer={printer}
      />
    );

    expect(screen.queryByText('Guarding')).toBeNull();
  });

  it('shows a detailed failure operations panel when a matching failure event arrives', async () => {
    const { DetailedPrinterCard } = await import(
      '@/features/printers/components/DetailedPrinterCard'
    );

    const printer = makePrinter({
      obicoEnabled: true,
      state: 'Printing',
      isOnline: true,
    });

    render(
      <DetailedPrinterCard
        printer={printer}
      />
    );

    act(() => {
      emitFailureDetected({
        printerId: printer.id,
        printerName: printer.name,
        confidence: 0.91,
        detectedAt: '2026-01-01T00:00:00Z',
        autoPaused: true,
        snapshotUrl: 'http://example.com/snapshot.jpg',
      });
    });

    expect(screen.getByText('Print auto-paused')).toBeTruthy();
    expect(screen.queryByText('1 incident')).not.toBeInTheDocument();
    expect(
      screen.getByText(
        /Inspect print and verify/
      )
    ).toBeTruthy();
    expect(screen.getByRole('link', { name: /snapshot/i })).toHaveAttribute(
      'href',
      'http://example.com/snapshot.jpg'
    );
  });
});
