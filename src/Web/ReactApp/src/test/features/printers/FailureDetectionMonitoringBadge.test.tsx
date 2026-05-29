import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { FailureDetectionMonitoringBadge } from '@/features/printers/components/FailureDetectionMonitoringBadge';
import type {
  FailureDetectionEvent,
  FailureDetectionPrinterStatusDto,
  JobStateHistoryDto,
} from '@/types/api';

let historyMock: FailureDetectionEvent[] = [];
let historyLoadingMock = false;
let historyErrorMock = false;
let timelineMock: JobStateHistoryDto | undefined;
let timelineLoadingMock = false;
let timelineErrorMock = false;

vi.mock('@/common/hooks/useApi', () => ({
  useFailureDetectionHistory: () => ({
    data: historyMock,
    isLoading: historyLoadingMock,
    isError: historyErrorMock,
  }),
  usePrintSessionTimeline: () => ({
    data: timelineMock,
    isLoading: timelineLoadingMock,
    isError: timelineErrorMock,
  }),
}));

describe('FailureDetectionMonitoringBadge', () => {
  beforeEach(() => {
    historyMock = [];
    historyLoadingMock = false;
    historyErrorMock = false;
    timelineMock = undefined;
    timelineLoadingMock = false;
    timelineErrorMock = false;
  });

  it('renders_WithIconOnly_NoInlineText', () => {
    const status: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-1',
      printerName: 'Voron 2.4',
      state: 'monitoring',
      reason: null,
      isPrinting: true,
      detectionSource: 'global',
      lastOutcome: 'clean',
      lastAnalyzedAt: '2026-01-15T10:30:00Z',
      lastConfidence: null,
      lastAutoPaused: false,
    };
    const recentEvents = [
      {
        printerId: 'printer-1',
        printerName: 'Voron 2.4',
        confidence: 0.92,
        detectedAt: '2026-01-15T10:29:45Z',
        snapshotUrl: 'http://example.com/failure.jpg',
        autoPaused: true,
      },
    ];

    render(
      <FailureDetectionMonitoringBadge
        enabled={true}
        status={status}
        recentEvents={recentEvents}
      />
    );

    // Shield icon should be present
    const button = screen.getByRole('button', { name: /open spaghetti detection details for voron 2.4/i });
    expect(button).toBeInTheDocument();

    // No inline status text should be rendered (icon-only)
    expect(screen.queryByText('Monitoring')).not.toBeInTheDocument();
    expect(screen.queryByText('Checking')).not.toBeInTheDocument();
    expect(screen.queryByText('Monitor error')).not.toBeInTheDocument();
  });

  it('exposesStateInTooltip_BeforePrintBegins_ShowsCheckingState', () => {
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

    render(<FailureDetectionMonitoringBadge enabled={true} status={status} />);

    const button = screen.getByRole('button', { name: /open spaghetti detection details for voron 2.4/i });
    
    // Tooltip should contain the state
    expect(button).toHaveAttribute('title', expect.stringContaining('Checking'));
    expect(button).toHaveAttribute('title', expect.stringContaining('click for details'));
  });

  it('exposesStateInTooltip_MonitoringState_ShowsGuarding', () => {
    const status: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-1',
      printerName: 'Prusa MK4',
      state: 'monitoring',
      reason: null,
      isPrinting: true,
      detectionSource: 'global',
      lastOutcome: 'clean',
      lastAnalyzedAt: '2026-01-15T10:30:00Z',
      lastConfidence: null,
      lastAutoPaused: false,
    };

    render(<FailureDetectionMonitoringBadge enabled={true} status={status} />);

    const button = screen.getByRole('button', { name: /open spaghetti detection details for prusa mk4/i });
    
    expect(button).toHaveAttribute('title', expect.stringContaining('Guarding'));
    expect(button).toHaveAttribute('title', expect.stringContaining('click for details'));
  });

  it('opensModal_WhenClicked_ShowsDetailedContext', () => {
    historyMock = [
      {
        id: 'incident-1',
        printerId: 'printer-1',
        printerName: 'Voron 2.4',
        jobId: 'job-1',
        jobName: 'Calibration Cube',
        fileName: 'cube.gcode',
        confidence: 0.88,
        detectedAt: '2026-01-15T09:20:00Z',
        snapshotUrl: 'http://example.com/history.jpg',
        autoPaused: false,
      },
    ];
    timelineMock = {
      jobId: 'job-1',
      jobName: 'Calibration Cube',
      transitions: [
        {
          fromState: 'Initial',
          toState: 'Queued',
          transitionedAtUtc: '2026-01-15T09:00:00Z',
          durationInStateSeconds: 600,
          notes: 'Job created and queued',
        },
        {
          fromState: 'Queued',
          toState: 'Printing',
          transitionedAtUtc: '2026-01-15T09:10:00Z',
          durationInStateSeconds: 1200,
          notes: 'Print started',
        },
      ],
      totalDurationSeconds: 1200,
      estimatedDurationSeconds: 1800,
      variancePercent: -33,
    };

    const status: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-1',
      printerName: 'Voron 2.4',
      state: 'error',
      reason: 'Failed to contact Obico ML service.',
      isPrinting: true,
      detectionSource: 'global',
      detectionTarget: 'North bay camera',
      snapshotUrl: 'http://example.com/failure.jpg',
      lastOutcome: 'failure',
      lastAnalyzedAt: '2026-01-15T10:30:00Z',
      lastConfidence: 0.92,
      lastAutoPaused: true,
      lastFailureDetectedAt: '2026-01-15T10:29:45Z',
    };
    const recentEvents: FailureDetectionEvent[] = [
      {
        printerId: 'printer-1',
        printerName: 'Voron 2.4',
        jobId: 'job-1',
        confidence: 0.92,
        detectedAt: '2026-01-15T10:29:45Z',
        snapshotUrl: 'http://example.com/failure.jpg',
        autoPaused: true,
      },
    ];

    render(
      <FailureDetectionMonitoringBadge
        enabled={true}
        status={status}
        recentEvents={recentEvents}
      />
    );

    fireEvent.click(
      screen.getByRole('button', { name: /open spaghetti detection details for voron 2.4/i })
    );

    // Modal should open with full detail
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getByText('Spaghetti detection details')).toBeInTheDocument();
    expect(screen.getAllByText('Failed to contact Obico ML service.')).toHaveLength(2);
    expect(
      screen.getByText(
        'Check the Obico ML service connection and camera reachability before relying on failure detection or auto-pause.'
      )
    ).toBeInTheDocument();
    expect(screen.getByText('North bay camera')).toBeInTheDocument();
    expect(screen.getByText('Failure detected (92% confidence)')).toBeInTheDocument();
    expect(screen.getByText('Triggered on the last result')).toBeInTheDocument();
    expect(screen.getByText('Recent incidents')).toBeInTheDocument();
    expect(screen.getByText('Print session timeline')).toBeInTheDocument();
    expect(screen.getByText('Job queued')).toBeInTheDocument();
    expect(screen.getAllByText('Print started').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Failure incident detected').length).toBeGreaterThan(0);
    expect(screen.getByText('Print auto-paused')).toBeInTheDocument();
    expect(screen.getByText('Auto-paused')).toBeInTheDocument();
    expect(screen.getByText('Calibration Cube')).toBeInTheDocument();
    expect(screen.getByText('cube.gcode')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /open latest snapshot/i })).toHaveAttribute(
      'href',
      'http://example.com/failure.jpg'
    );
    expect(
      screen.getAllByRole('link', { name: /open incident snapshot/i }).some(
        (link) => link.getAttribute('href') === 'http://example.com/history.jpg'
      )
    ).toBe(true);
  });

  it('shows_WhenSessionContextMissing_ExplainsWhyTimelineIsUnavailable', () => {
    historyMock = [
      {
        id: 'incident-1',
        printerId: 'printer-1',
        printerName: 'Voron 2.4',
        confidence: 0.71,
        detectedAt: '2026-01-15T09:20:00Z',
        autoPaused: false,
      },
    ];

    render(
      <FailureDetectionMonitoringBadge
        enabled={true}
        status={{
          printerId: 'printer-1',
          printerName: 'Voron 2.4',
          state: 'monitoring',
          reason: 'Monitoring via pooled server.',
          isPrinting: true,
          detectionSource: 'global',
          lastOutcome: 'healthy',
          lastAnalyzedAt: '2026-01-15T10:30:00Z',
        } as FailureDetectionPrinterStatusDto}
      />
    );

    fireEvent.click(
      screen.getByRole('button', { name: /open spaghetti detection details for voron 2.4/i })
    );

    expect(
      screen.getByText('Session timeline will appear once an incident can be tied to a tracked PrintFarmer job.')
    ).toBeInTheDocument();
  });

  it('appliesCorrectIconColor_ByState', () => {
    const { rerender } = render(
      <FailureDetectionMonitoringBadge
        enabled={true}
        status={{
          printerId: 'p1',
          state: 'monitoring',
          isPrinting: true,
          detectionSource: 'global',
          lastOutcome: 'clean',
          lastAnalyzedAt: '2026-01-15T10:30:00Z',
        } as FailureDetectionPrinterStatusDto}
      />
    );

    const button = screen.getByRole('button');
    const icon = button.querySelector('svg');
    
    // Monitoring state → success color
    expect(icon?.classList.contains('text-pf-success')).toBe(true);

    // Error state → error color
    rerender(
      <FailureDetectionMonitoringBadge
        enabled={true}
        status={{
          printerId: 'p1',
          state: 'error',
          reason: 'Failed to contact Obico ML service.',
          isPrinting: true,
          detectionSource: 'global',
          lastOutcome: 'error',
          lastAnalyzedAt: '2026-01-15T10:30:00Z',
        } as FailureDetectionPrinterStatusDto}
      />
    );

    const updatedIcon = screen.getByRole('button').querySelector('svg');
    expect(updatedIcon?.classList.contains('text-pf-error')).toBe(true);
  });

  it('remainsClickable_OpensModal', () => {
    const status: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-1',
      printerName: 'Test Printer',
      state: 'checking',
      isPrinting: false,
      detectionSource: 'global',
      lastOutcome: null,
      lastAnalyzedAt: null,
    };

    render(<FailureDetectionMonitoringBadge enabled={true} status={status} />);

    const button = screen.getByRole('button', { name: /open spaghetti detection details/i });
    
    // Should be clickable
    expect(button).not.toBeDisabled();
    
    fireEvent.click(button);

    // Modal should open
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('liveIsPrinting_OverridesStaleDto_WhenPrinterIsActivePrinting', () => {
    // Regression test for #309: DTO isPrinting can lag up to 30s behind live SignalR status.
    // When the printer card reports isPrinting=true, the shield must NOT show the stale
    // "idle / not printing" state from the DTO.
    const staleStatus: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-1',
      printerName: 'Bambu X1',
      state: 'idle',
      reason: 'Printer is not printing.',
      isPrinting: false, // stale — DTO hasn't polled yet since print started
      detectionSource: 'global',
      lastOutcome: 'none',
      lastAnalyzedAt: null,
    };

    render(
      <FailureDetectionMonitoringBadge
        enabled={true}
        status={staleStatus}
        isPrinting={true} // live value from printer.state === 'Printing'
      />
    );

    const button = screen.getByRole('button', { name: /open spaghetti detection details for bambu x1/i });

    // The tooltip must NOT claim the printer is idle/off when it's actively printing.
    // With live isPrinting=true overriding the stale DTO, state stays 'idle' (not 'error'),
    // so it displays as 'Ready' rather than being suppressed as undefined.
    expect(button).toHaveAttribute('title', expect.not.stringContaining('Off'));

    // Open the modal and verify it does not show the stale "not printing" reason as the
    // primary detail — the live override replaces the reason with an accurate message.
    fireEvent.click(button);
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    // The stale reason must be completely replaced — the override patches both isPrinting
    // and the reason string so "Printer is not printing." never appears.
    expect(screen.queryByText(/printer is not printing/i)).not.toBeInTheDocument();
    // Instead, an accurate waiting message should appear.
    expect(
      screen.getAllByText('Waiting for the monitoring service to begin scanning the current print.').length
    ).toBeGreaterThan(0);
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

    render(
      <FailureDetectionMonitoringBadge
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

    render(
      <FailureDetectionMonitoringBadge
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

  it('liveIsPrinting_False_OverridesStaleDto_WhenPrintJustEnded', () => {
    // When a print ends, the live isPrinting goes false quickly (SignalR) but the DTO
    // may still say isPrinting:true. The override should propagate the live value correctly.
    const staleStatus: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-2',
      printerName: 'Prusa MK4',
      state: 'monitoring',
      reason: 'Actively monitoring the current print.',
      isPrinting: true, // stale — print already ended per SignalR
      detectionSource: 'global',
      lastOutcome: 'healthy',
      lastAnalyzedAt: '2026-05-28T17:00:00Z',
    };

    render(
      <FailureDetectionMonitoringBadge
        enabled={true}
        status={staleStatus}
        isPrinting={false} // live: print has ended
      />
    );

    const button = screen.getByRole('button', { name: /open spaghetti detection details for prusa mk4/i });
    // State is 'monitoring' which displays as 'Guarding' regardless of isPrinting value
    expect(button).toHaveAttribute('title', expect.stringContaining('Guarding'));
  });
});
