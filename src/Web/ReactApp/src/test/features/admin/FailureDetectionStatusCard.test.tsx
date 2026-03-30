import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';

const mockStatus = {
  monitoringEnabled: true,
  confidenceThreshold: 0.7,
  scanIntervalSeconds: 30,
  autoPauseOnFailure: true,
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

describe('FailureDetectionStatusCard', () => {
  it('renders runtime summary and printers needing attention', async () => {
    const { FailureDetectionStatusCard } = await import(
      '@/features/admin/components/FailureDetectionStatusCard'
    );

    render(<FailureDetectionStatusCard />);

    expect(screen.getByText('Failure Detection Runtime')).toBeTruthy();
    expect(screen.getByText('Configured')).toBeTruthy();
    expect(screen.getByText('3')).toBeTruthy();
    expect(screen.getByText('Bravo')).toBeTruthy();
    expect(screen.getByText('No enabled camera snapshot URL is configured.')).toBeTruthy();
    expect(screen.getByText('Alpha')).toBeTruthy();
  });
});
