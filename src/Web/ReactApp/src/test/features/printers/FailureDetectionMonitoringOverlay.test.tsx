import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { FailureDetectionMonitoringOverlay } from '@/features/printers/components/FailureDetectionMonitoringOverlay';
import type { FailureDetectionPrinterStatusDto } from '@/types/api';

describe('FailureDetectionMonitoringOverlay', () => {
  it('renders nothing when disabled and no status', () => {
    const { container } = render(
      <FailureDetectionMonitoringOverlay enabled={false} />
    );

    expect(container.firstChild).toBeNull();
  });

  it('renders a compact checking trigger when enabled with no status', () => {
    render(<FailureDetectionMonitoringOverlay enabled={true} printerName="Voron 2.4" />);

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

    render(<FailureDetectionMonitoringOverlay enabled={true} status={status} />);

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

    const { container } = render(
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

    render(<FailureDetectionMonitoringOverlay enabled={true} status={status} />);

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

    render(<FailureDetectionMonitoringOverlay enabled={true} status={status} />);

    fireEvent.click(
      screen.getByRole('button', { name: /open spaghetti detection details for voron 2.4/i })
    );

    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getByText('Spaghetti detection details')).toBeInTheDocument();
    expect(
      screen.getAllByText('No enabled camera snapshot URL is configured.').length
    ).toBeGreaterThan(1);
    expect(
      screen.getByText(
        'Add or enable a usable camera snapshot feed so failure detection can inspect frames from this printer.'
      )
    ).toBeInTheDocument();
    expect(screen.getByText('Current camera target not reported')).toBeInTheDocument();
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

    const { container } = render(
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
});
