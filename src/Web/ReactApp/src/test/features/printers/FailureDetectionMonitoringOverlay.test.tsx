import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FailureDetectionMonitoringOverlay } from '@/features/printers/components/FailureDetectionMonitoringOverlay';
import type { FailureDetectionPrinterStatusDto } from '@/types/api';

describe('FailureDetectionMonitoringOverlay', () => {
  describe('visibility logic', () => {
    it('renders nothing when disabled and no status', () => {
      const { container } = render(
        <FailureDetectionMonitoringOverlay enabled={false} />
      );

      expect(container.firstChild).toBeNull();
    });

    it('renders "Checking" chip when enabled with no status', () => {
      render(<FailureDetectionMonitoringOverlay enabled={true} />);

      expect(screen.getByText('Checking')).toBeInTheDocument();
      expect(screen.getByText(/Connecting/)).toBeInTheDocument();
    });
  });

  describe('monitoring state', () => {
    it('renders "Guarding" label with no hint', () => {
      const status: FailureDetectionPrinterStatusDto = {
        state: 'monitoring',
        reason: 'Monitoring via global Obico ML settings.',
        isPrinting: true,
        detectionSource: 'global',
        lastOutcome: 'healthy',
        lastAnalyzedAt: '2026-01-15T10:30:00Z',
        lastConfidence: null,
        lastAutoPaused: false,
      };

      render(<FailureDetectionMonitoringOverlay enabled={true} status={status} />);

      expect(screen.getByText('Guarding')).toBeInTheDocument();
      expect(screen.queryByText('·')).not.toBeInTheDocument();
    });

    it('applies success styling (border and icon)', () => {
      const status: FailureDetectionPrinterStatusDto = {
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
        <FailureDetectionMonitoringOverlay enabled={true} status={status} />
      );

      const overlay = container.firstChild as HTMLElement;
      expect(overlay.className).toContain('border-pf-success');

      const icon = container.querySelector('svg');
      expect(icon?.classList.contains('text-pf-success')).toBe(true);
    });
  });

  describe('idle state', () => {
    it('renders "Ready" label with no hint', () => {
      const status: FailureDetectionPrinterStatusDto = {
        state: 'idle',
        reason: 'Printer is not printing.',
        isPrinting: false,
        detectionSource: 'none',
        lastOutcome: 'none',
        lastAnalyzedAt: null,
        lastConfidence: null,
        lastAutoPaused: false,
      };

      render(<FailureDetectionMonitoringOverlay enabled={true} status={status} />);

      expect(screen.getByText('Ready')).toBeInTheDocument();
      expect(screen.queryByText('·')).not.toBeInTheDocument();
    });

    it('applies accent styling (border and icon)', () => {
      const status: FailureDetectionPrinterStatusDto = {
        state: 'idle',
        reason: 'Printer is not printing.',
        isPrinting: false,
        detectionSource: 'none',
        lastOutcome: 'none',
        lastAnalyzedAt: null,
        lastConfidence: null,
        lastAutoPaused: false,
      };

      const { container } = render(
        <FailureDetectionMonitoringOverlay enabled={true} status={status} />
      );

      const overlay = container.firstChild as HTMLElement;
      expect(overlay.className).toContain('border-pf-accent');

      const icon = container.querySelector('svg');
      expect(icon?.classList.contains('text-pf-accent')).toBe(true);
    });
  });

  describe('misconfigured state', () => {
    it('renders "Needs setup" label with "Check settings" hint', () => {
      const status: FailureDetectionPrinterStatusDto = {
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
      expect(screen.getByText(/Check settings/)).toBeInTheDocument();
    });

    it('applies warning styling (border and icon)', () => {
      const status: FailureDetectionPrinterStatusDto = {
        state: 'misconfigured',
        reason: 'No enabled camera snapshot URL is configured.',
        isPrinting: false,
        detectionSource: 'none',
        lastOutcome: 'none',
        lastAnalyzedAt: null,
        lastConfidence: null,
        lastAutoPaused: false,
      };

      const { container } = render(
        <FailureDetectionMonitoringOverlay enabled={true} status={status} />
      );

      const overlay = container.firstChild as HTMLElement;
      expect(overlay.className).toContain('border-pf-warning');

      const icon = container.querySelector('svg');
      expect(icon?.classList.contains('text-pf-warning')).toBe(true);
    });
  });

  describe('error state', () => {
    it('renders "Attention" label with "Needs attention" hint', () => {
      const status: FailureDetectionPrinterStatusDto = {
        state: 'error',
        reason: 'Failed to contact Obico ML service.',
        isPrinting: true,
        detectionSource: 'global',
        lastOutcome: 'error',
        lastAnalyzedAt: '2026-01-15T10:30:00Z',
        lastConfidence: null,
        lastAutoPaused: false,
      };

      render(<FailureDetectionMonitoringOverlay enabled={true} status={status} />);

      expect(screen.getByText('Attention')).toBeInTheDocument();
      expect(screen.getByText(/Needs attention/)).toBeInTheDocument();
    });

    it('applies error styling (border and icon)', () => {
      const status: FailureDetectionPrinterStatusDto = {
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

      const overlay = container.firstChild as HTMLElement;
      expect(overlay.className).toContain('border-pf-error');

      const icon = container.querySelector('svg');
      expect(icon?.classList.contains('text-pf-error')).toBe(true);
    });
  });

  describe('disabled state', () => {
    it('renders "Standby" when enabled but state is disabled', () => {
      const status: FailureDetectionPrinterStatusDto = {
        state: 'disabled',
        reason: 'Failure detection is disabled in Settings.',
        isPrinting: false,
        detectionSource: 'none',
        lastOutcome: 'none',
        lastAnalyzedAt: null,
        lastConfidence: null,
        lastAutoPaused: false,
      };

      render(<FailureDetectionMonitoringOverlay enabled={true} status={status} />);

      expect(screen.getByText('Standby')).toBeInTheDocument();
      expect(screen.queryByText('·')).not.toBeInTheDocument();
    });

    it('renders "Off" when not enabled and state is disabled', () => {
      const status: FailureDetectionPrinterStatusDto = {
        state: 'disabled',
        reason: 'Failure detection is disabled in Settings.',
        isPrinting: false,
        detectionSource: 'none',
        lastOutcome: 'none',
        lastAnalyzedAt: null,
        lastConfidence: null,
        lastAutoPaused: false,
      };

      render(<FailureDetectionMonitoringOverlay enabled={false} status={status} />);

      expect(screen.getByText('Off')).toBeInTheDocument();
    });
  });

  describe('styling customization', () => {
    it('applies custom className when provided', () => {
      const status: FailureDetectionPrinterStatusDto = {
        state: 'idle',
        reason: 'Printer is not printing.',
        isPrinting: false,
        detectionSource: 'none',
        lastOutcome: 'none',
        lastAnalyzedAt: null,
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

      const overlay = container.firstChild as HTMLElement;
      expect(overlay.className).toContain('custom-test-class');
    });

    it('uses compact chip format (inline-flex rounded-full)', () => {
      const status: FailureDetectionPrinterStatusDto = {
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
        <FailureDetectionMonitoringOverlay enabled={true} status={status} />
      );

      const overlay = container.firstChild as HTMLElement;
      expect(overlay.className).toContain('inline-flex');
      expect(overlay.className).toContain('rounded-full');
      expect(overlay.className).toContain('pointer-events-none');
    });
  });
});
