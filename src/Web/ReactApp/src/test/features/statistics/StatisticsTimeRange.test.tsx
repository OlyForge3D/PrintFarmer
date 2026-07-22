import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const mockUseStatisticsSummary = vi.fn();
const mockUseJobsOverTime = vi.fn();
const mockUseCostOverTime = vi.fn();
const mockUseFilamentByMaterial = vi.fn();
const mockUsePrinterUtilization = vi.fn();

vi.mock('@/features/statistics/hooks/useStatistics', () => ({
  useStatisticsSummary: (...args: unknown[]) => mockUseStatisticsSummary(...args),
  useJobsOverTime: (...args: unknown[]) => mockUseJobsOverTime(...args),
  useCostOverTime: (...args: unknown[]) => mockUseCostOverTime(...args),
  useFilamentByMaterial: (...args: unknown[]) => mockUseFilamentByMaterial(...args),
  usePrinterUtilization: (...args: unknown[]) => mockUsePrinterUtilization(...args),
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

import { StatisticsPage } from '@/features/statistics/pages/StatisticsPage';

describe('StatisticsPage time range handling', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockUseStatisticsSummary.mockReturnValue({ data: undefined, isLoading: false, error: null });
    mockUseJobsOverTime.mockReturnValue({ data: [], isLoading: false, error: null });
    mockUseCostOverTime.mockReturnValue({ data: [], isLoading: false, error: null });
    mockUseFilamentByMaterial.mockReturnValue({ data: [], isLoading: false, error: null });
    mockUsePrinterUtilization.mockReturnValue({ data: [], isLoading: false, error: null });
  });

  it('passes an unbounded range to trend hooks for the All time preset', async () => {
    const user = userEvent.setup();
    render(<StatisticsPage />);

    await user.click(screen.getByRole('button', { name: 'All time' }));

    expect(mockUseJobsOverTime).toHaveBeenLastCalledWith(undefined, undefined, undefined);
    expect(mockUseCostOverTime).toHaveBeenLastCalledWith(undefined, undefined, undefined);
  });
});
