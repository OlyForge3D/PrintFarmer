import { beforeAll, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import type { FailureDetectionMonitorStatusDto } from '@/types/api';

const mockStatus: FailureDetectionMonitorStatusDto = {
  monitoringEnabled: true,
  confidenceThreshold: 0.7,
  scanIntervalSeconds: 30,
  configuredPrinterCount: 3,
  activelyMonitoredPrinterCount: 2,
  lastAnalyzedPrinterCount: 2,
  lastFailureCount: 1,
  lastScanStartedAt: '2026-01-01T10:00:00Z',
  lastScanCompletedAt: '2026-01-01T10:00:05Z',
  printers: [
    {
      printerId: 'printer-1',
      printerName: 'Alpha',
      state: 'monitoring',
      reason: 'Monitoring via global Obico ML settings.',
      isPrinting: true,
      detectionSource: 'global',
      lastOutcome: 'healthy',
    },
    {
      printerId: 'printer-2',
      printerName: 'Bravo',
      state: 'misconfigured',
      reason: 'No enabled camera snapshot URL is configured.',
      isPrinting: false,
      detectionSource: 'none',
      lastOutcome: 'none',
    },
  ],
};

vi.mock('@tanstack/react-query', () => ({
  useQuery: () => ({
    data: mockStatus,
    isLoading: false,
    error: null,
  }),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getFailureDetectionStatus: vi.fn(),
  },
}));

let FailureDetectionStatusCard:
  typeof import('@/features/admin/components/FailureDetectionStatusCard')['FailureDetectionStatusCard'];

beforeAll(async () => {
  ({ FailureDetectionStatusCard } = await import(
    '@/features/admin/components/FailureDetectionStatusCard'
  ));
}, 60_000);

describe('FailureDetectionStatusCard', () => {
  it('shows each provider once in a fleet-wide section, not inside attention or active summaries', () => {
    const original = mockStatus.printers;
    mockStatus.printers = original.map(printer => ({
      ...printer,
      providers: [{
        provider: 'PrintGuard', state: 'monitoring', analysisMode: 'Shadow',
        reason: `Provider status for ${printer.printerName}`, lastOutcome: 'healthy',
      }],
    }));
    try {
      render(<FailureDetectionStatusCard />);
      const providers = screen.getByRole('region', { name: 'Detection provider status' });
      for (const name of ['Alpha', 'Bravo']) {
        expect(within(providers).getByText(name)).toBeInTheDocument();
        expect(screen.getAllByText(`Provider status for ${name}`, { exact: false })).toHaveLength(1);
      }
      expect(screen.getByText('Printers needing attention').parentElement).not.toContainElement(providers);
      expect(screen.getByText('Active now').parentElement).not.toContainElement(providers);
    } finally {
      mockStatus.printers = original;
    }
  });

  it('shows held offline reservations as attention, not actively monitored', () => {
    const original = mockStatus.printers;
    mockStatus.printers = [{
      printerId: 'held',
      printerName: 'Offline printer',
      state: 'reservation-held',
      reason: 'Printer activity is unknown or offline. Obico reservation retained.',
      isPrinting: false,
      detectionSource: 'pooled',
      lastOutcome: 'healthy',
    }];
    try {
      render(<FailureDetectionStatusCard />);
      expect(screen.getByText('Offline printer')).toBeInTheDocument();
      expect(screen.getByText('Reservation held')).toBeInTheDocument();
      expect(screen.getByText(/No printers are actively being monitored/)).toBeInTheDocument();
    } finally {
      mockStatus.printers = original;
    }
  });

  it('renders runtime summary and printers needing attention', () => {
    render(<FailureDetectionStatusCard />);

    expect(screen.getByText('Failure Detection Runtime')).toBeTruthy();
    expect(screen.getByText('Configured')).toBeTruthy();
    expect(screen.getByText('3')).toBeTruthy();
    expect(screen.getByText('Bravo')).toBeTruthy();
    expect(screen.getByText('No enabled camera snapshot URL is configured.')).toBeTruthy();
    expect(screen.getByText('Alpha')).toBeTruthy();
  });
});
