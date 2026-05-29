import React from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { FailureDetectionMonitoringOverlay } from '@/features/printers/components/FailureDetectionMonitoringOverlay';
import { usePrinterFailureDetectionStatus } from '@/features/printers/hooks/usePrinterFailureDetectionStatus';
import { apiClient } from '@/services/api';
import type { FailureDetectionMonitorStatusDto, FailureDetectionPrinterStatusDto } from '@/types/api';

vi.mock('@/services/api', () => ({
  apiClient: {
    getFailureDetectionStatus: vi.fn(),
  },
}));

function createTestQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
}

function FailureDetectionMonitoringOverlayHarness({
  printerId,
  enabled,
  printerName,
}: {
  printerId: string;
  enabled: boolean;
  printerName: string;
}) {
  const { printerStatus } = usePrinterFailureDetectionStatus(printerId, enabled);

  return (
    <FailureDetectionMonitoringOverlay
      enabled={enabled}
      status={printerStatus}
      printerName={printerName}
    />
  );
}

function renderWithQueryClient(ui: React.ReactElement) {
  const queryClient = createTestQueryClient();

  return render(
    <QueryClientProvider client={queryClient}>
      {ui}
    </QueryClientProvider>
  );
}

describe('FailureDetectionMonitoringOverlay', () => {
  beforeEach(() => {
    vi.mocked(apiClient.getFailureDetectionStatus).mockReset();
  });

  it('renders nothing when disabled and no status', () => {
    const { container } = renderWithQueryClient(
      <FailureDetectionMonitoringOverlay enabled={false} />
    );

    expect(container.firstChild).toBeNull();
  });

  it('renders a compact checking trigger when enabled with no status', () => {
    renderWithQueryClient(<FailureDetectionMonitoringOverlay enabled={true} printerName="Voron 2.4" />);

    const trigger = screen.getByRole('button', {
      name: /open spaghetti detection details for voron 2.4/i,
    });

    expect(trigger).toHaveTextContent('Checking');
    expect(screen.queryByText(/Connecting/)).not.toBeInTheDocument();
  });

  it('renders a compact needs-setup trigger without inline helper text', () => {
    const status: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-1',
      printerName: 'Voron 2.4',
      state: 'misconfigured',
      reason: 'No enabled camera snapshot URL is configured.',
      isPrinting: false,
      detectionSource: 'none',
      lastOutcome: 'none',
      lastAnalyzedAt: null,
      lastConfidence: null,
      lastAutoPaused: false,
    };

    renderWithQueryClient(<FailureDetectionMonitoringOverlay enabled={true} status={status} />);

    expect(screen.getByText('Needs setup')).toBeInTheDocument();
    expect(screen.queryByText('Check settings')).not.toBeInTheDocument();
  });

  it('renders explicit monitor-error styling when the runtime reports an active error', () => {
    const status: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-1',
      printerName: 'Voron 2.4',
      state: 'error',
      reason: 'Failed to contact Obico ML service.',
      isPrinting: true,
      detectionSource: 'global',
      lastOutcome: 'error',
      lastAnalyzedAt: '2026-01-15T10:30:00Z',
      lastConfidence: null,
      lastAutoPaused: false,
    };

    const { container } = renderWithQueryClient(
      <FailureDetectionMonitoringOverlay enabled={true} status={status} />
    );

    expect(screen.getByText('Monitor error')).toBeInTheDocument();
    const trigger = container.querySelector('button');
    expect(trigger?.className).toContain('border-pf-error');
  });

  it('falls back to checking before printing starts', () => {
    const status: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-1',
      printerName: 'Voron 2.4',
      state: 'error',
      reason: 'Failed to contact Obico ML service.',
      isPrinting: false,
      detectionSource: 'global',
      lastOutcome: 'error',
      lastAnalyzedAt: '2026-01-15T10:30:00Z',
      lastConfidence: null,
      lastAutoPaused: false,
    };

    renderWithQueryClient(<FailureDetectionMonitoringOverlay enabled={true} status={status} />);

    expect(screen.getByText('Checking')).toBeInTheDocument();
    expect(screen.queryByText('Monitor error')).not.toBeInTheDocument();
  });

  it('opens a modal with the detailed operator guidance when clicked', () => {
    const status: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-1',
      printerName: 'Voron 2.4',
      state: 'misconfigured',
      reason: 'No enabled camera snapshot URL is configured.',
      isPrinting: false,
      detectionSource: 'none',
      lastOutcome: 'none',
      lastAnalyzedAt: null,
      lastConfidence: null,
      lastAutoPaused: false,
    };

    renderWithQueryClient(<FailureDetectionMonitoringOverlay enabled={true} status={status} />);

    fireEvent.click(
      screen.getByRole('button', { name: /open spaghetti detection details for voron 2.4/i })
    );

    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getByText('Spaghetti detection details')).toBeInTheDocument();
    expect(
      screen.getAllByText('No enabled camera snapshot URL is configured.').length
    ).toBeGreaterThan(0);
    expect(
      screen.getByText(/Add and enable a usable linked camera snapshot feed/i)
    ).toBeInTheDocument();
  });

  it('applies custom className and interactive button styling', () => {
    const status: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-1',
      printerName: 'Voron 2.4',
      state: 'monitoring',
      reason: 'Monitoring via global Obico ML settings.',
      isPrinting: true,
      detectionSource: 'global',
      lastOutcome: 'healthy',
      lastAnalyzedAt: '2026-01-15T10:30:00Z',
      lastConfidence: null,
      lastAutoPaused: false,
    };

    const { container } = renderWithQueryClient(
      <FailureDetectionMonitoringOverlay
        enabled={true}
        status={status}
        className="custom-test-class"
      />
    );

    const trigger = container.querySelector('button') as HTMLElement;
    expect(trigger.className).toContain('custom-test-class');
    expect(trigger.className).toContain('inline-flex');
    expect(trigger.className).toContain('rounded-full');
    expect(trigger.className).toContain('pointer-events-auto');
  });

  it('surfaces an actionable compatibility error from the live status query path inside the modal', async () => {
    const status: FailureDetectionMonitorStatusDto = {
      monitoringEnabled: true,
      confidenceThreshold: 0.7,
      scanIntervalSeconds: 60,
      autoPauseOnFailure: true,
      configuredPrinterCount: 1,
      activelyMonitoredPrinterCount: 1,
      lastAnalyzedPrinterCount: 1,
      lastFailureCount: 0,
      lastScanStartedAt: '2026-03-25T12:00:00Z',
      lastScanCompletedAt: '2026-03-25T12:00:05Z',
      lastError: null,
      printers: [{
        printerId: 'printer-1',
        printerName: 'Voron 2.4',
        state: 'error',
        reason: 'Configured Obico server is not exposing a supported prediction route (legacy POST /p/ returned HTTP 405). Check that the URL points to the Obico ML API root that supports upstream GET /p/?img=... or legacy POST /p/.',
        isPrinting: true,
        detectionSource: 'global',
        detectionTarget: 'Global Obico ML',
        snapshotUrl: null,
        lastAnalyzedAt: '2026-03-25T12:00:05Z',
        lastOutcome: 'error',
        lastConfidence: null,
        lastAutoPaused: false,
        lastFailureDetectedAt: null,
      }],
    };

    vi.mocked(apiClient.getFailureDetectionStatus).mockResolvedValue(status);

    renderWithQueryClient(
      <FailureDetectionMonitoringOverlayHarness
        printerId="printer-1"
        enabled={true}
        printerName="Voron 2.4"
      />
    );

    await waitFor(() => {
      expect(apiClient.getFailureDetectionStatus).toHaveBeenCalledTimes(1);
    });

    const trigger = await screen.findByRole('button', {
      name: /open spaghetti detection details for voron 2.4/i,
    });
    expect(trigger).toHaveTextContent('Monitor error');

    fireEvent.click(trigger);

    expect(await screen.findByRole('dialog')).toBeInTheDocument();
    expect(screen.getByText('Spaghetti detection details')).toBeInTheDocument();
    expect(screen.getAllByText(/Configured Obico server is not exposing a supported prediction route/).length).toBeGreaterThan(1);
    expect(
      screen.getByText(
        'Check the Obico ML service connection and camera reachability before relying on failure detection or auto-pause.'
      )
    ).toBeInTheDocument();
  });

  it('shows private snapshot reachability details without hiding the latest snapshot link', () => {
    const status: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-1',
      printerName: 'Voron 2.4',
      state: 'error',
      reason: 'The selected snapshot URL is private to the printer LAN, so the Obico service could not reach it directly.',
      isPrinting: true,
      detectionSource: 'pooled',
      detectionTarget: 'North Bay pooled Obico',
      snapshotUrl: 'http://127.0.0.1:8080/webcam/?action=snapshot',
      lastOutcome: 'error',
      lastAnalyzedAt: '2026-03-25T12:00:05Z',
      lastConfidence: null,
      lastAutoPaused: false,
    };

    renderWithQueryClient(<FailureDetectionMonitoringOverlay enabled={true} status={status} />);

    fireEvent.click(
      screen.getByRole('button', { name: /open spaghetti detection details for voron 2.4/i })
    );

    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getAllByText(/private to the printer lan/i).length).toBeGreaterThan(0);
    expect(screen.getByText('North Bay pooled Obico')).toBeInTheDocument();
    expect(
      screen.getByText(/Make the linked camera snapshot feed reachable/i)
    ).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /open latest snapshot/i })).toHaveAttribute(
      'href',
      'http://127.0.0.1:8080/webcam/?action=snapshot'
    );
  });

  it('shows timeout-specific operator guidance when PrintFarmer cannot fetch the snapshot in time', () => {
    const status: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-1',
      printerName: 'Voron 2.4',
      state: 'error',
      reason: 'Snapshot fetch timeout. PrintFarmer could not download the camera snapshot in time.',
      isPrinting: true,
      detectionSource: 'pooled',
      detectionTarget: 'North Bay pooled Obico',
      snapshotUrl: 'http://printer.local/webcam/?action=snapshot',
      lastOutcome: 'error',
      lastAnalyzedAt: '2026-03-25T12:00:05Z',
      lastConfidence: null,
      lastAutoPaused: false,
    };

    renderWithQueryClient(<FailureDetectionMonitoringOverlay enabled={true} status={status} />);

    fireEvent.click(
      screen.getByRole('button', { name: /open spaghetti detection details for voron 2.4/i })
    );

    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getAllByText(/snapshot fetch timeout/i).length).toBeGreaterThan(0);
    expect(
      screen.getByText(/Open the latest snapshot and confirm/i)
    ).toBeInTheDocument();
  });

  it('liveIsPrinting_True_PreservesReason_WhenDisabledWithFeatureOffReason', () => {
    // Regression for Bishop review on #313: state=disabled means the feature is
    // intentionally off. The reason must be preserved even when live isPrinting=true.
    const status: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-3',
      printerName: 'Bambu X1C',
      state: 'disabled',
      reason: 'Failure detection is disabled in Settings.',
      isPrinting: false,
      detectionSource: 'none',
      lastOutcome: 'none',
      lastAnalyzedAt: null,
    };

    renderWithQueryClient(
      <FailureDetectionMonitoringOverlay
        enabled={false}
        status={status}
        isPrinting={true}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: /open spaghetti detection details for bambu x1c/i }));

    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getAllByText('Failure detection is disabled in Settings.').length).toBeGreaterThan(0);
    expect(screen.queryByText(/waiting for the monitoring service/i)).not.toBeInTheDocument();
  });

  it('liveIsPrinting_True_PreservesReason_WhenDisabledWithUnsupportedBackendReason', () => {
    // Regression for Bishop review on #313: backends that do not support failure detection
    // report state=disabled with an explanatory reason. That reason must not be overwritten.
    const status: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-4',
      printerName: 'SDCP Resin Printer',
      state: 'disabled',
      reason: 'Backend does not support failure detection.',
      isPrinting: false,
      detectionSource: 'none',
      lastOutcome: 'none',
      lastAnalyzedAt: null,
    };

    renderWithQueryClient(
      <FailureDetectionMonitoringOverlay
        enabled={true}
        status={status}
        isPrinting={true}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: /open spaghetti detection details for sdcp resin printer/i }));

    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getAllByText('Backend does not support failure detection.').length).toBeGreaterThan(0);
    expect(screen.queryByText(/waiting for the monitoring service/i)).not.toBeInTheDocument();
  });
});
