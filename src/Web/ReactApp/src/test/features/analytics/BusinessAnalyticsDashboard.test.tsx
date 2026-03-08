import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';

// Mock hooks first before importing component
const mockUseStatisticsSummary = vi.fn();

vi.mock('@/features/statistics/hooks/useStatistics', () => ({
  useStatisticsSummary: () => mockUseStatisticsSummary(),
  useJobsOverTime: () => ({ data: [], isLoading: false, error: null }),
  useCostOverTime: () => ({ data: [], isLoading: false, error: null }),
  useFilamentByMaterial: () => ({ data: [], isLoading: false, error: null }),
  usePrinterUtilization: () => ({ data: [], isLoading: false, error: null }),
}));

vi.mock('@/features/analytics/hooks/useCorrelationAnalytics', () => ({
  useCorrelationData: () => ({ data: null, isLoading: false, error: null }),
}));

vi.mock('@/features/analytics/hooks/usePredictiveAnalytics', () => ({
  useActiveAlerts: () => ({ data: [], isLoading: false, error: null }),
  useMaintenanceForecast: () => ({ data: [], isLoading: false, error: null }),
  useJobFailurePrediction: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));

vi.mock('@/features/statistics/components/JobsOverTimeChart', () => ({
  JobsOverTimeChart: () => <div data-testid="jobs-chart">Jobs Chart</div>,
}));
vi.mock('@/features/statistics/components/CostOverTimeChart', () => ({
  CostOverTimeChart: () => <div data-testid="cost-chart">Cost Chart</div>,
}));
vi.mock('@/features/statistics/components/FilamentByMaterialChart', () => ({
  FilamentByMaterialChart: () => <div data-testid="filament-chart">Filament Chart</div>,
}));
vi.mock('@/features/statistics/components/PrinterUtilizationChart', () => ({
  PrinterUtilizationChart: () => <div data-testid="utilization-chart">Utilization Chart</div>,
}));
vi.mock('@/features/analytics/components/ExportMenu', () => ({
  ExportMenu: () => <div data-testid="export-menu">Export</div>,
}));
vi.mock('@/features/analytics/components/PredictiveAlertsPanel', () => ({
  PredictiveAlertsPanel: () => <div data-testid="alerts-panel">Alerts</div>,
}));
vi.mock('@/features/analytics/components/CorrelationChartsSection', () => ({
  CorrelationChartsSection: () => <div data-testid="correlation-charts">Correlation</div>,
}));

// Import component after mocks are set up
import { AnalyticsDashboardPage } from '@/features/analytics/pages/AnalyticsDashboardPage';

describe('AnalyticsDashboardPage', () => {
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

    render(<AnalyticsDashboardPage />);
    expect(screen.getByText('Total Jobs')).toBeInTheDocument();
  });

  it('displays KPI cards with correct data', () => {
    render(<AnalyticsDashboardPage />);

    expect(screen.getByText('Total Jobs')).toBeInTheDocument();
    expect(screen.getByText('100')).toBeInTheDocument();
    expect(screen.getByText('Success Rate')).toBeInTheDocument();
    expect(screen.getByText('92%')).toBeInTheDocument();
  });

  it('renders export menu', () => {
    render(<AnalyticsDashboardPage />);

    expect(screen.getByTestId('export-menu')).toBeInTheDocument();
  });

  it('renders time period buttons', () => {
    render(<AnalyticsDashboardPage />);

    expect(screen.getByText('7 days')).toBeInTheDocument();
    expect(screen.getByText('30 days')).toBeInTheDocument();
  });

  it('renders tab navigation', () => {
    render(<AnalyticsDashboardPage />);

    expect(screen.getByText('Overview')).toBeInTheDocument();
    expect(screen.getByText('Performance Correlations')).toBeInTheDocument();
    expect(screen.getByText('Maintenance Forecast')).toBeInTheDocument();
  });

  it('renders with error state in hooks gracefully', () => {
    mockUseStatisticsSummary.mockReturnValue({
      data: undefined,
      isLoading: false,
      error: new Error('Failed to load'),
    });

    render(<AnalyticsDashboardPage />);
    // Page still renders with default values when data fails
    expect(screen.getByText('Business Analytics')).toBeInTheDocument();
  });
});
