import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { FailureDetectionMonitoringSummary } from '@/features/printers/components/FailureDetectionMonitoringSummary';
import type { FailureDetectionEvent, FailureDetectionPrinterStatusDto } from '@/types/api';

describe('FailureDetectionMonitoringSummary', () => {
  it('renders healthy operational coverage details for an active print', () => {
    const status: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-1',
      printerName: 'Voron 2.4',
      state: 'monitoring',
      reason: 'Monitoring via pooled server.',
      isPrinting: true,
      detectionSource: 'pooled',
      detectionTarget: 'North bay camera',
      lastOutcome: 'healthy',
      lastAnalyzedAt: '2026-01-15T10:30:00Z',
      lastConfidence: null,
      lastAutoPaused: false,
    };

    render(
      <FailureDetectionMonitoringSummary
        enabled={true}
        status={status}
        printerName="Voron 2.4"
        variant="compact"
      />
    );

    expect(screen.getByText('Active coverage')).toBeInTheDocument();
    expect(screen.getByText(/Last scan cleared the print/)).toBeInTheDocument();
    expect(screen.getByText('Pooled')).toBeInTheDocument();
    expect(screen.getByText(/Clear at/)).toBeInTheDocument();
    expect(screen.getByText('North bay camera')).toBeInTheDocument();
  });

  it('renders session incident history and snapshot access for live failures', () => {
    const status: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-1',
      printerName: 'Voron 2.4',
      state: 'monitoring',
      reason: 'Monitoring via pooled server.',
      isPrinting: true,
      detectionSource: 'global',
      detectionTarget: 'North bay camera',
      lastOutcome: 'failure',
      lastAnalyzedAt: '2026-01-15T10:30:00Z',
      lastConfidence: 0.91,
      lastAutoPaused: true,
      lastFailureDetectedAt: '2026-01-15T10:29:30Z',
    };
    const recentEvents: FailureDetectionEvent[] = [
      {
        printerId: 'printer-1',
        printerName: 'Voron 2.4',
        confidence: 0.91,
        detectedAt: '2026-01-15T10:29:30Z',
        autoPaused: true,
        snapshotUrl: 'http://example.com/snapshot-1.jpg',
      },
      {
        printerId: 'printer-1',
        printerName: 'Voron 2.4',
        confidence: 0.82,
        detectedAt: '2026-01-15T10:24:30Z',
        autoPaused: false,
      },
    ];

    render(
      <FailureDetectionMonitoringSummary
        enabled={true}
        status={status}
        recentEvents={recentEvents}
        printerName="Voron 2.4"
        variant="detailed"
      />
    );

    expect(screen.getByText('Print auto-paused')).toBeInTheDocument();
    expect(screen.queryByText('Session incident log')).not.toBeInTheDocument();
    expect(screen.queryByText('2 incidents')).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: /open latest snapshot/i })).toHaveAttribute(
      'href',
      'http://example.com/snapshot-1.jpg'
    );
  });

  it('surfaces operator action when coverage is blocked by setup issues', () => {
    const status: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-1',
      printerName: 'Voron 2.4',
      state: 'misconfigured',
      reason: 'No enabled camera snapshot URL is configured.',
      isPrinting: false,
      detectionSource: 'none',
      detectionTarget: '',
      lastOutcome: 'none',
      lastAnalyzedAt: null,
      lastConfidence: null,
      lastAutoPaused: false,
    };

    render(
      <FailureDetectionMonitoringSummary
        enabled={true}
        status={status}
        printerName="Voron 2.4"
        variant="compact"
      />
    );

    expect(screen.getByText('Coverage blocked')).toBeInTheDocument();
    expect(
      screen.getAllByText('No enabled camera snapshot URL is configured.').length
    ).toBeGreaterThan(1);
    expect(
      screen.getByText('Add or enable a snapshot camera for this printer.')
    ).toBeInTheDocument();
  });
});
