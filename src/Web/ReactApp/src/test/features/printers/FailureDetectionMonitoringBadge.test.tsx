import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { FailureDetectionMonitoringBadge } from '@/features/printers/components/FailureDetectionMonitoringBadge';
import type { FailureDetectionPrinterStatusDto } from '@/types/api';

describe('FailureDetectionMonitoringBadge', () => {
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

    render(<FailureDetectionMonitoringBadge enabled={true} status={status} />);

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
    expect(screen.getByRole('link', { name: /open latest snapshot/i })).toHaveAttribute(
      'href',
      'http://example.com/failure.jpg'
    );
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
});
