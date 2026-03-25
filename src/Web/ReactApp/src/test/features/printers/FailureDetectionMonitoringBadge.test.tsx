import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { FailureDetectionMonitoringBadge } from '@/features/printers/components/FailureDetectionMonitoringBadge';
import type { FailureDetectionPrinterStatusDto } from '@/types/api';

describe('FailureDetectionMonitoringBadge', () => {
  it('suppresses attention styling before printing begins', () => {
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

    expect(screen.getByText('Checking')).toBeInTheDocument();
    expect(screen.queryByText('Monitor error')).not.toBeInTheDocument();
  });

  it('opens a modal with operator-facing detail when the badge is clicked', () => {
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

    render(<FailureDetectionMonitoringBadge enabled={true} status={status} />);

    fireEvent.click(
      screen.getByRole('button', { name: /open spaghetti detection details for voron 2.4/i })
    );

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
    expect(screen.getByRole('link', { name: /open latest snapshot/i })).toHaveAttribute(
      'href',
      'http://example.com/failure.jpg'
    );
  });
});
