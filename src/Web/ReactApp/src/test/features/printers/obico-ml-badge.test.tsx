import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import type { FailureDetectionEvent, Printer } from '@/types/api';

// ── Mocks for CompactPrinterCard dependencies ──

vi.mock('@/common/hooks/useApi', () => ({
  usePrinters: () => ({ data: [], isLoading: false }),
  useJobQueue: () => ({ data: [], isLoading: false }),
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
  }),
  useQuery: () => ({ data: undefined, isLoading: false }),
}));

vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAutoDispatchStatus: () => ({ data: null, isLoading: false }),
  useSetAutoDispatchEnabled: () => ({ mutateAsync: vi.fn() }),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinterDetails: vi.fn().mockResolvedValue({}),
  },
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

// ── CompactPrinterCard ML badge tests ──

describe('CompactPrinterCard ML badge', () => {
  it('shows ML badge when printer has Obico monitoring enabled and is printing', async () => {
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

    expect(screen.getByText('ML')).toBeTruthy();
    expect(screen.getByLabelText('ML Monitoring Active')).toBeTruthy();
  });

  it('does NOT show ML badge when printer does not have Obico monitoring enabled', async () => {
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

    expect(screen.queryByText('ML')).toBeNull();
  });

  it('does NOT show ML badge when printer is idle even with Obico monitoring enabled', async () => {
    const { CompactPrinterCard } = await import(
      '@/features/printers/components/CompactPrinterCard'
    );

    const printer = makePrinter({
      obicoEnabled: true,
      state: 'Idle',
      isOnline: true,
    });

    render(
      <CompactPrinterCard
        printer={printer}
        onExpand={vi.fn()}
      />
    );

    expect(screen.queryByText('ML')).toBeNull();
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
  });
});

// ── DetailedPrinterCard ML badge tests ──

describe('DetailedPrinterCard ML badge', () => {
  it('shows ML badge when printer has Obico monitoring enabled and is printing', async () => {
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

    expect(screen.getByText('ML')).toBeTruthy();
    expect(screen.getByLabelText('ML Monitoring Active')).toBeTruthy();
  });

  it('does NOT show ML badge when printer does not have Obico monitoring enabled', async () => {
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

    expect(screen.queryByText('ML')).toBeNull();
  });

  it('shows a detailed failure alert when a matching failure event arrives', async () => {
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

    expect(screen.getByText('Print auto-paused after failure detection')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'View snapshot' })).toBeTruthy();
  });
});
