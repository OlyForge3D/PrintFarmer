import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';

// Mock hooks first
const mockUseActiveAlerts = vi.fn();

vi.mock('@/features/statistics/hooks/usePredictiveAnalytics', () => ({
  useActiveAlerts: () => mockUseActiveAlerts(),
}));

// Import component after mocks
import { PredictiveAlertsPanel } from '@/features/statistics/components/PredictiveAlertsPanel';

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
    expect(screen.getByText(/no active alerts/i)).toBeInTheDocument();
  });

  it('renders loading state', () => {
    mockUseActiveAlerts.mockReturnValue({
      data: undefined,
      isLoading: true,
      error: null,
    });

    render(<PredictiveAlertsPanel />);
    expect(screen.getByRole('status')).toBeInTheDocument();
  });

  it('renders error state', () => {
    mockUseActiveAlerts.mockReturnValue({
      data: undefined,
      isLoading: false,
      error: new Error('Failed to load alerts'),
    });

    render(<PredictiveAlertsPanel />);
    expect(screen.getByText(/failed to load/i)).toBeInTheDocument();
  });

  it('applies correct severity badge colors', () => {
    render(<PredictiveAlertsPanel />);

    const warningBadge = screen.getByText('Warning');
    expect(warningBadge).toHaveClass('badge-warning');

    const criticalBadge = screen.getByText('Critical');
    expect(criticalBadge).toHaveClass('badge-error');
  });
});
