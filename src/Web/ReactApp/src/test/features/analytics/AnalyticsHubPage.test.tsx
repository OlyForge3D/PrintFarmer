import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const mockUseStatisticsSummary = vi.fn();
const mockUsePrinterUtilization = vi.fn();
const mockUseCostSummary = vi.fn();
const mockUsePrinters = vi.fn();

vi.mock('@/features/statistics/hooks/useStatistics', () => ({
  useStatisticsSummary: (...args: unknown[]) => mockUseStatisticsSummary(...args),
  usePrinterUtilization: (...args: unknown[]) => mockUsePrinterUtilization(...args),
}));

vi.mock('@/common/hooks/useApi', () => ({
  useCostSummary: (...args: unknown[]) => mockUseCostSummary(...args),
  usePrinters: (...args: unknown[]) => mockUsePrinters(...args),
}));

vi.mock('@/features/statistics/pages/StatisticsPage', () => ({
  StatisticsDashboardContent: () => <div data-testid="production-panel">Production content</div>,
}));

vi.mock('@/features/statistics/pages/CostDashboardPage', () => ({
  CostDashboardContent: () => <div data-testid="cost-panel">Cost content</div>,
}));

vi.mock('@/features/analytics/pages/AnalyticsDashboardPage', () => ({
  AnalyticsDashboardContent: () => <div data-testid="fleet-panel">Fleet content</div>,
}));

import { AnalyticsHubPage } from '@/features/analytics/pages/AnalyticsHubPage';

function LocationSpy() {
  const location = useLocation();
  return <div data-testid="location-search">{location.search}</div>;
}

function renderAnalyticsHub(initialEntry = '/analytics') {
  return render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <Routes>
        <Route
          path="/analytics"
          element={
            <>
              <AnalyticsHubPage />
              <LocationSpy />
            </>
          }
        />
      </Routes>
    </MemoryRouter>,
  );
}

describe('AnalyticsHubPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockUseStatisticsSummary.mockReturnValue({
      data: {
        completedJobs: 90,
        successRate: 95,
      },
      isLoading: false,
    });
    mockUseCostSummary.mockReturnValue({
      data: {
        averageCostPerJobUsd: 12.5,
        totalMaterialCostUsd: 220.4,
      },
      isLoading: false,
    });
    mockUsePrinterUtilization.mockReturnValue({
      data: [
        { printerId: 'p1', printerName: 'A1', totalPrintHours: 72 },
        { printerId: 'p2', printerName: 'A2', totalPrintHours: 36 },
      ],
      isLoading: false,
    });
    mockUsePrinters.mockReturnValue({
      data: [{ id: 'p1' }, { id: 'p2' }, { id: 'p3' }, { id: 'p4' }],
      isLoading: false,
      error: null,
    });
  });

  it('renders KPI summary cards and defaults to the production lens', () => {
    renderAnalyticsHub();

    expect(screen.getByRole('heading', { name: /analytics/i })).toBeInTheDocument();
    expect(screen.getByText('Jobs Completed')).toBeInTheDocument();
    expect(screen.getByText('90')).toBeInTheDocument();
    expect(screen.getByText('Success Rate')).toBeInTheDocument();
    expect(screen.getByText('95%')).toBeInTheDocument();
    expect(screen.getByText('$12.50')).toBeInTheDocument();
    expect(screen.getByText('$220.40')).toBeInTheDocument();
    expect(screen.getByText('3.8%')).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: /production/i })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('production-panel')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Custom' })).toBeInTheDocument();
  });

  it('selects the requested lens from the query string', () => {
    renderAnalyticsHub('/analytics?lens=fleet');

    expect(screen.getByRole('tab', { name: /fleet/i })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('fleet-panel')).toBeInTheDocument();
  });

  it('supports keyboard navigation and keeps the lens query param in sync', async () => {
    const user = userEvent.setup();
    renderAnalyticsHub('/analytics?lens=production');

    const productionTab = screen.getByRole('tab', { name: /production/i });
    productionTab.focus();
    await user.keyboard('{ArrowRight}');

    expect(screen.getByRole('tab', { name: /cost/i })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: /cost/i })).toHaveFocus();
    expect(screen.getByTestId('cost-panel')).toBeInTheDocument();
    expect(screen.getByTestId('location-search')).toHaveTextContent('?lens=cost');
  });

  it('shows an unavailable state instead of misleading zeroes when a KPI query fails', () => {
    mockUseCostSummary.mockReturnValue({
      data: undefined,
      isLoading: false,
      error: new Error('cost summary failed'),
    });

    renderAnalyticsHub();

    expect(screen.getAllByText('Unavailable').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Check source').length).toBeGreaterThan(0);
  });

  it('marks fleet utilization unavailable for the All time preset', async () => {
    const user = userEvent.setup();
    renderAnalyticsHub();

    await user.click(screen.getByRole('button', { name: 'All time' }));

    expect(screen.getAllByText('Unavailable').length).toBeGreaterThan(0);
    expect(screen.getByText('Bounded only')).toBeInTheDocument();
  });
});
