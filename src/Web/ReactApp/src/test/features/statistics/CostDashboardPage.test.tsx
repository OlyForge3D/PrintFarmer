import React from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';

const mockUseCostSummary = vi.fn();
const mockUseCostsByPrinter = vi.fn();
const mockUseCostsByMaterial = vi.fn();

vi.mock('@/common/hooks/useApi', () => ({
  useCostSummary: (...args: unknown[]) => mockUseCostSummary(...args),
  useCostsByPrinter: (...args: unknown[]) => mockUseCostsByPrinter(...args),
  useCostsByMaterial: (...args: unknown[]) => mockUseCostsByMaterial(...args),
}));

import { CostDashboardPage } from '@/features/statistics/pages/CostDashboardPage';

const defaultSummary = {
  totalCostUsd: 250.5,
  averageCostPerJobUsd: 12.53,
  totalMaterialCostUsd: 180.0,
  totalEnergyCostUsd: 70.5,
};

describe('CostDashboardPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockUseCostSummary.mockReturnValue({ data: defaultSummary, isLoading: false, error: null });
    mockUseCostsByPrinter.mockReturnValue({ data: [], isLoading: false, error: null });
    mockUseCostsByMaterial.mockReturnValue({ data: [], isLoading: false, error: null });
  });

  it('renders page title and time period filter', () => {
    render(<CostDashboardPage />);
    expect(screen.getByRole('heading', { name: /cost analytics/i })).toBeInTheDocument();
    expect(screen.getByRole('group', { name: /time period/i })).toBeInTheDocument();
  });

  it('renders all five time period options', () => {
    render(<CostDashboardPage />);
    expect(screen.getByText('7 days')).toBeInTheDocument();
    expect(screen.getByText('30 days')).toBeInTheDocument();
    expect(screen.getByText('90 days')).toBeInTheDocument();
    expect(screen.getByText('1 year')).toBeInTheDocument();
    expect(screen.getByText('All time')).toBeInTheDocument();
  });

  it('defaults to 30 days selected', () => {
    render(<CostDashboardPage />);
    const btn30 = screen.getByRole('button', { name: '30 days' });
    expect(btn30).toHaveAttribute('aria-pressed', 'true');
    expect(mockUseCostSummary).toHaveBeenCalledWith(30, undefined, undefined);
  });

  it('passes days parameter to all three hooks when filter changes', async () => {
    const user = userEvent.setup();
    render(<CostDashboardPage />);

    await user.click(screen.getByText('7 days'));
    expect(mockUseCostSummary).toHaveBeenCalledWith(7, undefined, undefined);
    expect(mockUseCostsByPrinter).toHaveBeenCalledWith(7, undefined, undefined);
    expect(mockUseCostsByMaterial).toHaveBeenCalledWith(7, undefined, undefined);
  });

  it('passes undefined for All time selection', async () => {
    const user = userEvent.setup();
    render(<CostDashboardPage />);

    await user.click(screen.getByText('All time'));
    expect(mockUseCostSummary).toHaveBeenCalledWith(undefined, undefined, undefined);
    expect(mockUseCostsByPrinter).toHaveBeenCalledWith(undefined, undefined, undefined);
    expect(mockUseCostsByMaterial).toHaveBeenCalledWith(undefined, undefined, undefined);
  });

  it('renders KPI cards with summary data', () => {
    render(<CostDashboardPage />);
    expect(screen.getByText('Total Cost')).toBeInTheDocument();
    expect(screen.getByText('Avg Cost/Job')).toBeInTheDocument();
    expect(screen.getByText('Material Cost %')).toBeInTheDocument();
    expect(screen.getByText('Energy Cost %')).toBeInTheDocument();
  });

  it('shows error state when hooks fail', () => {
    mockUseCostSummary.mockReturnValue({ data: null, isLoading: false, error: new Error('Server error') });
    render(<CostDashboardPage />);
    expect(screen.getByText(/failed to load cost data/i)).toBeInTheDocument();
  });
});
