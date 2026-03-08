import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';

// Mock hooks first
const mockUseActiveAlerts = vi.fn();

vi.mock('@/features/analytics/hooks/usePredictiveAnalytics', () => ({
  useActiveAlerts: () => mockUseActiveAlerts(),
}));

// Import component after mocks
import { PredictiveAlertsPanel } from '@/features/analytics/components/PredictiveAlertsPanel';

describe('PredictiveAlertsPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockUseActiveAlerts.mockReturnValue({
      data: [
        {
          alertType: 'HighFailureRate',
          severity: 'Warning',
          message: 'Recent failure rate is 25% (last 7 days)',
          recommendedAction: 'Review recent failed jobs',
        },
        {
          alertType: 'MaintenanceDue',
          severity: 'Critical',
          message: 'Printer A: Nozzle Replacement due in ~2 days',
          recommendedAction: 'Schedule maintenance',
        },
      ],
      isLoading: false,
      error: null,
    });
  });

  it('renders alert cards with severity badges', () => {
    render(<PredictiveAlertsPanel />);

    expect(screen.getByText('Recent failure rate is 25% (last 7 days)')).toBeInTheDocument();
    expect(screen.getByText('Warning')).toBeInTheDocument();
    expect(screen.getByText('Critical')).toBeInTheDocument();
  });

  it('displays recommended actions', () => {
    render(<PredictiveAlertsPanel />);

    expect(screen.getByText(/review recent failed jobs/i)).toBeInTheDocument();
    expect(screen.getByText(/schedule maintenance/i)).toBeInTheDocument();
  });

  it('renders empty state when no predictions', () => {
    mockUseActiveAlerts.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });

    render(<PredictiveAlertsPanel />);
    // Component returns null when no alerts
    expect(screen.queryByText(/predictive alerts/i)).not.toBeInTheDocument();
  });

  it('renders loading state', () => {
    mockUseActiveAlerts.mockReturnValue({
      data: undefined,
      isLoading: true,
      error: null,
    });

    render(<PredictiveAlertsPanel />);
    expect(screen.getByText(/loading alerts/i)).toBeInTheDocument();
  });

  it('renders nothing on error', () => {
    mockUseActiveAlerts.mockReturnValue({
      data: undefined,
      isLoading: false,
      error: new Error('Failed to load alerts'),
    });

    render(<PredictiveAlertsPanel />);
    // Component returns null on error
    expect(screen.queryByText(/predictive alerts/i)).not.toBeInTheDocument();
  });

  it('applies correct severity badge colors', () => {
    render(<PredictiveAlertsPanel />);

    const warningBadge = screen.getByText('Warning');
    expect(warningBadge.className).toContain('bg-pf-warning');

    const criticalBadge = screen.getByText('Critical');
    expect(criticalBadge.className).toContain('bg-pf-error');
  });
});
