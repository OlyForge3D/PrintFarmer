import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { FailureDetectionMonitoringSummary } from '@/features/printers/components/FailureDetectionMonitoringSummary';
import type { FailureDetectionEvent, FailureDetectionPrinterStatusDto } from '@/types/api';

describe('FailureDetectionMonitoringSummary', () => {
  it('renders healthy coverage state with compact headline and badge', () => {
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

    const guardingElements = screen.getAllByText('Guarding');
    expect(guardingElements.length).toBe(2); // headline + badge
    expect(screen.getByText(/Last scan/)).toBeInTheDocument();
  });

  it('renders confidence gauge in compact variant when monitoring with confidence', () => {
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
      lastConfidence: 0.15,
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

    expect(screen.getByRole('img', { name: /confidence 85%/i })).toBeInTheDocument();
    // Badge should NOT be rendered when gauge is present
    expect(screen.queryByText('Guarding', { selector: '[class*="badge"]' })).toBeNull();
  });

  it('renders confidence gauge in detailed variant when monitoring with confidence', () => {
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
      lastConfidence: 0.42,
      lastAutoPaused: false,
    };

    render(
      <FailureDetectionMonitoringSummary
        enabled={true}
        status={status}
        printerName="Voron 2.4"
        variant="detailed"
      />
    );

    expect(screen.getByRole('img', { name: /confidence 58%/i })).toBeInTheDocument();
  });

  it('renders auto-pause incident with snapshot link and action badge', () => {
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
    expect(screen.getByText('Action:')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /snapshot/i })).toHaveAttribute(
      'href',
      'http://example.com/snapshot-1.jpg'
    );
    expect(screen.getByText(/Inspect print and verify/)).toBeInTheDocument();
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

    expect(screen.getByText('Setup needed')).toBeInTheDocument();
    expect(screen.getByText('Review')).toBeInTheDocument();
    expect(screen.getByText(/Add or enable a snapshot camera/)).toBeInTheDocument();
  });

  it('shows standing by state when idle', () => {
    const status: FailureDetectionPrinterStatusDto = {
      printerId: 'printer-1',
      printerName: 'Voron 2.4',
      state: 'idle',
      reason: 'Printer is not actively printing.',
      isPrinting: false,
      detectionSource: 'pooled',
      detectionTarget: 'North bay camera',
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

    expect(screen.getByText('Standing by')).toBeInTheDocument();
    expect(screen.getByText('Idle')).toBeInTheDocument();
  });

  // Card-level visibility: both CompactPrinterCard and DetailedPrinterCard
  // only mount this component when the printer is actively printing or paused.
  // The header badge remains the sole failure-detection indicator at rest.
  describe('card-level visibility contract', () => {
    it('renders nothing when not enabled, no status, and no events (card omits mount)', () => {
      const { container } = render(
        <FailureDetectionMonitoringSummary
          enabled={false}
          printerName="Voron 2.4"
          variant="compact"
        />
      );

      expect(container.innerHTML).toBe('');
    });

    it('renders detailed variant during active printing with healthy coverage', () => {
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
          variant="detailed"
        />
      );

      const guardingElements = screen.getAllByText('Guarding');
      expect(guardingElements.length).toBeGreaterThanOrEqual(1);
    });
  });
});
