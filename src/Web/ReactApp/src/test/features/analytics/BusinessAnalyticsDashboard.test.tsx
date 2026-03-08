import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';

// Mock hooks first before importing component
const mockUseStatisticsSummary = vi.fn();

vi.mock('@/features/statistics/hooks/useStatistics', () => ({
  useStatisticsSummary: () => mockUseStatisticsSummary(),
}));

// Import component after mocks are set up
import { BusinessAnalyticsDashboard } from '@/features/statistics/pages/BusinessAnalyticsDashboard';

describe('BusinessAnalyticsDashboard', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockUseStatisticsSummary.mockReturnValue({
      data: {
        totalJobs: 100,
        successRate: 92,
        totalCost: 500.25,
        totalPrintHours: 250.5,
        completedJobs: 92,
        failedJobs: 5,
        cancelledJobs: 3,
        totalFilamentGrams: 5000,
      },
      isLoading: false,
      error: null,
    });
  });

  it('renders with loading state initially', () => {
    mockUseStatisticsSummary.mockReturnValue({
      data: undefined,
      isLoading: true,
      error: null,
    });

    render(<BusinessAnalyticsDashboard />);
    expect(screen.getByRole('status')).toBeInTheDocument();
  });

  it('displays KPI cards with correct data', () => {
    render(<BusinessAnalyticsDashboard />);

    expect(screen.getByText('Total Jobs')).toBeInTheDocument();
    expect(screen.getByText('100')).toBeInTheDocument();
    expect(screen.getByText('Success Rate')).toBeInTheDocument();
    expect(screen.getByText('92%')).toBeInTheDocument();
  });

  it('renders export buttons', () => {
    render(<BusinessAnalyticsDashboard />);

    expect(screen.getByRole('button', { name: /export pdf/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /export csv/i })).toBeInTheDocument();
  });

  it('renders date range selector', () => {
    render(<BusinessAnalyticsDashboard />);

    expect(screen.getByLabelText(/date range/i)).toBeInTheDocument();
    expect(screen.getByText('30 days')).toBeInTheDocument();
  });

  it('renders tab navigation', () => {
    render(<BusinessAnalyticsDashboard />);

    expect(screen.getByRole('tab', { name: /overview/i })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: /jobs/i })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: /costs/i })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: /printers/i })).toBeInTheDocument();
  });

  it('renders with error state', () => {
    mockUseStatisticsSummary.mockReturnValue({
      data: undefined,
      isLoading: false,
      error: new Error('Failed to load'),
    });

    render(<BusinessAnalyticsDashboard />);
    expect(screen.getByText(/failed to load/i)).toBeInTheDocument();
  });
});
