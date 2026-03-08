import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StatisticsPage } from '@/features/statistics/pages/StatisticsPage';

// Mock the statistics hooks
vi.mock('@/features/statistics/hooks/useStatistics', () => ({
  useStatisticsSummary: vi.fn(() => ({
    data: {
      totalJobs: 42,
      completedJobs: 35,
      failedJobs: 5,
      cancelledJobs: 2,
      successRate: 83,
      totalCost: 150.5,
      totalFilamentGrams: 2500,
      totalPrintHours: 120.5,
    },
    isLoading: false,
  })),
  useJobsOverTime: vi.fn(() => ({ data: [], isLoading: false, error: null })),
  useCostOverTime: vi.fn(() => ({ data: [], isLoading: false, error: null })),
  useFilamentByMaterial: vi.fn(() => ({ data: [], isLoading: false, error: null })),
  usePrinterUtilization: vi.fn(() => ({ data: [], isLoading: false, error: null })),
}));

// Mock chart components to avoid complexity
vi.mock('@/features/statistics/components/JobsOverTimeChart', () => ({
  JobsOverTimeChart: () => <div data-testid="jobs-chart">JobsOverTimeChart</div>,
}));
vi.mock('@/features/statistics/components/CostOverTimeChart', () => ({
  CostOverTimeChart: () => <div data-testid="cost-chart">CostOverTimeChart</div>,
}));
vi.mock('@/features/statistics/components/FilamentByMaterialChart', () => ({
  FilamentByMaterialChart: () => <div data-testid="filament-chart">FilamentByMaterialChart</div>,
}));
vi.mock('@/features/statistics/components/PrinterUtilizationChart', () => ({
  PrinterUtilizationChart: () => <div data-testid="utilization-chart">PrinterUtilizationChart</div>,
}));

function renderStatisticsPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <StatisticsPage />
    </QueryClientProvider>
  );
}

describe('StatisticsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('Rendering', () => {
    it('renders page heading', () => {
      renderStatisticsPage();
      expect(screen.getByRole('heading', { level: 1, name: /print statistics/i })).toBeInTheDocument();
    });

    it('renders period filter buttons', () => {
      renderStatisticsPage();
      expect(screen.getByRole('button', { name: '7 days' })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: '30 days' })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: '90 days' })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'All time' })).toBeInTheDocument();
    });

    it('renders KPI cards with summary data', () => {
      renderStatisticsPage();
      expect(screen.getByText('Total Jobs')).toBeInTheDocument();
      expect(screen.getByText('42')).toBeInTheDocument();
      expect(screen.getByText('Success Rate')).toBeInTheDocument();
      expect(screen.getByText('83%')).toBeInTheDocument();
    });

    it('renders all four chart sections', () => {
      renderStatisticsPage();
      expect(screen.getByTestId('jobs-chart')).toBeInTheDocument();
      expect(screen.getByTestId('cost-chart')).toBeInTheDocument();
      expect(screen.getByTestId('filament-chart')).toBeInTheDocument();
      expect(screen.getByTestId('utilization-chart')).toBeInTheDocument();
    });
  });

  describe('Design tokens — no ghost tokens (PFarm1-u5h)', () => {
    it('page heading uses pf-* text token, not hardcoded color', () => {
      renderStatisticsPage();
      const heading = screen.getByRole('heading', { level: 1 });
      // Should use text-pf-text or similar, not text-gray-900, text-white, etc.
      expect(heading.className).toMatch(/text-pf-/);
    });

    it('heading does not use ghost tokens (text-gray-*, text-slate-*, text-white, text-black)', () => {
      renderStatisticsPage();
      const heading = screen.getByRole('heading', { level: 1 });
      expect(heading.className).not.toMatch(/\btext-(gray|slate|zinc|neutral)-\d+\b/);
      expect(heading.className).not.toMatch(/\btext-(white|black)\b/);
    });

    it('active period button uses pf-* token for active state', () => {
      renderStatisticsPage();
      // The default period is 30 days
      const activeButton = screen.getByRole('button', { name: '30 days' });
      expect(activeButton.className).toMatch(/bg-pf-/);
    });

    it('inactive period buttons use pf-* surface token', () => {
      renderStatisticsPage();
      const inactiveButton = screen.getByRole('button', { name: '7 days' });
      expect(inactiveButton.className).toMatch(/bg-pf-/);
      expect(inactiveButton.className).toMatch(/text-pf-/);
    });

    it('KPI cards do not contain hardcoded gray/slate background tokens', () => {
      const { container } = renderStatisticsPage();
      // KpiCard uses Card which should use pf-* tokens
      const cards = container.querySelectorAll('[class*="Card"], .p-4');
      cards.forEach((card) => {
        expect(card.className).not.toMatch(/\bbg-(gray|slate|zinc|neutral)-\d+\b/);
      });
    });

    it('no element on the page uses text-gray-900 (common ghost token)', () => {
      const { container } = renderStatisticsPage();
      const allElements = container.querySelectorAll('*');
      allElements.forEach((el) => {
        const cls = (el as HTMLElement).className;
        if (typeof cls === 'string') {
          expect(cls).not.toContain('text-gray-900');
        }
      });
    });

    it('KPI label uses pf-text-secondary token', () => {
      renderStatisticsPage();
      const label = screen.getByText('Total Jobs');
      expect(label.className).toContain('text-pf-text-secondary');
    });
  });

  describe('Interactions', () => {
    it('changes active period when clicking a filter button', () => {
      renderStatisticsPage();
      const btn7 = screen.getByRole('button', { name: '7 days' });
      fireEvent.click(btn7);
      expect(btn7).toHaveAttribute('aria-pressed', 'true');
    });
  });
});
