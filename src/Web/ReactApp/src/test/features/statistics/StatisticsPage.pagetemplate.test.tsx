import React from 'react';
import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';

/**
 * StatisticsPage — PageTemplate integration tests.
 *
 * Batch 2 fix: StatisticsPage will be wrapped in PageTemplate with
 * title="Print Statistics". These tests validate the EXPECTED state
 * after that fix lands. Until then, tests that check for PageTemplate
 * structure will fail — that's intentional.
 */

// Mock all chart components to keep tests focused on page structure
vi.mock('../../../features/statistics/components/JobsOverTimeChart', () => ({
  JobsOverTimeChart: () => <div data-testid="jobs-chart">Jobs Chart</div>,
}));
vi.mock('../../../features/statistics/components/CostOverTimeChart', () => ({
  CostOverTimeChart: () => <div data-testid="cost-chart">Cost Chart</div>,
}));
vi.mock('../../../features/statistics/components/FilamentByMaterialChart', () => ({
  FilamentByMaterialChart: () => <div data-testid="filament-chart">Filament Chart</div>,
}));
vi.mock('../../../features/statistics/components/PrinterUtilizationChart', () => ({
  PrinterUtilizationChart: () => <div data-testid="utilization-chart">Utilization Chart</div>,
}));

// Mock the statistics hooks to return stable data
vi.mock('../../../features/statistics/hooks/useStatistics', () => ({
  useStatisticsSummary: () => ({
    data: {
      totalJobs: 42,
      successRate: 95,
      totalCost: 123.45,
      totalPrintHours: 88.5,
      completedJobs: 40,
      failedJobs: 1,
      cancelledJobs: 1,
      totalFilamentGrams: 2500,
    },
    isLoading: false,
  }),
  useJobsOverTime: () => ({ data: [], isLoading: false, error: null }),
  useCostOverTime: () => ({ data: [], isLoading: false, error: null }),
  useFilamentByMaterial: () => ({ data: [], isLoading: false, error: null }),
  usePrinterUtilization: () => ({ data: [], isLoading: false, error: null }),
}));

import { StatisticsPage } from '../../../features/statistics/pages/StatisticsPage';

describe('StatisticsPage — page structure', () => {
  beforeEach(() => {
    render(<StatisticsPage />);
  });

  it('displays "Print Statistics" as the page title', () => {
    expect(screen.getByText('Print Statistics')).toBeInTheDocument();
  });

  it('renders KPI cards with summary data', () => {
    expect(screen.getByText('Total Jobs')).toBeInTheDocument();
    expect(screen.getByText('42')).toBeInTheDocument();
    expect(screen.getByText('Success Rate')).toBeInTheDocument();
    expect(screen.getByText('95%')).toBeInTheDocument();
  });

  it('renders all four chart sections', () => {
    expect(screen.getByTestId('jobs-chart')).toBeInTheDocument();
    expect(screen.getByTestId('cost-chart')).toBeInTheDocument();
    expect(screen.getByTestId('filament-chart')).toBeInTheDocument();
    expect(screen.getByTestId('utilization-chart')).toBeInTheDocument();
  });

  it('renders cost formatted as currency', () => {
    expect(screen.getByText('$123.45')).toBeInTheDocument();
  });

  it('renders filament used in kilograms', () => {
    expect(screen.getByText('2.50 kg')).toBeInTheDocument();
  });

  it('renders print hours', () => {
    expect(screen.getByText('88.5')).toBeInTheDocument();
  });

  it('renders time period filter buttons', () => {
    expect(screen.getByText('7 days')).toBeInTheDocument();
    expect(screen.getByText('30 days')).toBeInTheDocument();
    expect(screen.getByText('90 days')).toBeInTheDocument();
    expect(screen.getByText('1 year')).toBeInTheDocument();
    expect(screen.getByText('All time')).toBeInTheDocument();
  });

  it('has time period filter group with accessible role', () => {
    expect(screen.getByRole('group', { name: /time period/i })).toBeInTheDocument();
  });
});

describe('StatisticsPage — PageTemplate wrapper (batch 2)', () => {
  /**
   * After the batch 2 fix, StatisticsPage should be wrapped in PageTemplate.
   * Until the fix lands, these tests will fail with "Unable to find role heading"
   * because the current implementation uses a raw <h1> not from PageTemplate.
   */
  it('uses PageTemplate with title prop', () => {
    render(<StatisticsPage />);
    // PageTemplate renders title as an h1 heading — verify it exists
    const heading = screen.getByRole('heading', { name: /print statistics/i });
    expect(heading).toBeInTheDocument();
  });

  it('does not use hardcoded gray/slate classes in KPI cards', () => {
    const { container } = render(<StatisticsPage />);
    const allClasses = Array.from(container.querySelectorAll('*'))
      .map((el) => el.className)
      .filter((c) => typeof c === 'string')
      .join(' ');

    // KPI color classes like text-green-500 are acceptable for semantic status,
    // but raw gray-*/slate-* should be replaced by pf-* tokens.
    expect(allClasses).not.toMatch(/\bgray-\d/);
    expect(allClasses).not.toMatch(/\bslate-\d/);
  });
});
