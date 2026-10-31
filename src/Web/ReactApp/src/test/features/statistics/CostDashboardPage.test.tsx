import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';

const mockUseCostSummary = vi.fn();
const mockUseCostsByPrinter = vi.fn();
const mockUseCostsByMaterial = vi.fn();
const mockUseCostsByJob = vi.fn();

vi.mock('@/common/hooks/useApi', () => ({
  useCostSummary: (...args: unknown[]) => mockUseCostSummary(...args),
  useCostsByPrinter: (...args: unknown[]) => mockUseCostsByPrinter(...args),
  useCostsByMaterial: (...args: unknown[]) => mockUseCostsByMaterial(...args),
  useCostsByJob: (...args: unknown[]) => mockUseCostsByJob(...args),
}));

import { CostDashboardPage } from '@/features/statistics/pages/CostDashboardPage';
import type { CostByJob, CostByMaterial, CostByPrinter } from '@/types/api';

const defaultSummary = {
  totalCostUsd: 250.5,
  averageCostPerJobUsd: 12.53,
  totalMaterialCostUsd: 180.0,
  totalEnergyCostUsd: 70.5,
};

const orderedFixtures = [
  { name: 'Zulu', value: 30, total: 300, date: '2026-11-01T00:00:00Z' },
  { name: 'Alpha', value: 2, total: 2, date: '2026-02-01T00:00:00Z' },
  { name: 'Bravo', value: 10, total: 30, date: '2026-10-01T00:00:00Z' },
];

function costFields(value: number) {
  return { materialCostUsd: value, energyCostUsd: value, machineTimeCostUsd: value, laborCostUsd: value };
}

const printerFixtures = orderedFixtures.map(({ name, value, total }) => ({
  printerId: name, printerName: name, jobCount: value, ...costFields(value), totalCostUsd: total,
  averageCostPerJobUsd: total / value,
})) satisfies CostByPrinter[];
const jobFixtures = orderedFixtures.map(({ name, value, total, date }) => ({
  jobId: name, jobName: name, printerName: name, filamentName: name, materialType: 'PLA',
  filamentUsedGrams: value, printTimeSeconds: value * 3600, ...costFields(value),
  totalCostUsd: total, completedAt: date,
})) satisfies CostByJob[];
const materialFixtures = orderedFixtures.map(({ name, value, total }) => ({
  materialType: name, jobCount: value, totalFilamentUsageGrams: value * 1000,
  averageCostPerJobUsd: value, totalCostUsd: total,
})) satisfies CostByMaterial[];

const sortCases = [
  ...['Printer', 'Jobs', 'Material', 'Energy', 'Machine Time', 'Labor', 'Total', 'Avg/Job']
    .map(header => ['Cost by Printer', header]),
  ...['Job', 'Printer', 'Filament', 'Weight (g)', 'Print Time', 'Material', 'Energy', 'Machine', 'Labor', 'Total', 'Completed']
    .map(header => ['Costs by Job', header]),
  ...['Material', 'Jobs', 'Weight (kg)', 'Avg/Job', 'Total Cost']
    .map(header => ['Costs by Material', header]),
];

function visibleRowNames() {
  return within(screen.getByRole('table')).getAllByRole('row').slice(1)
    .map(row => within(row).getAllByRole('cell')[0].textContent);
}

describe('CostDashboardPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockUseCostSummary.mockReturnValue({ data: defaultSummary, isLoading: false, error: null });
    mockUseCostsByPrinter.mockReturnValue({ data: [], isLoading: false, error: null });
    mockUseCostsByMaterial.mockReturnValue({ data: [], isLoading: false, error: null });
    mockUseCostsByJob.mockReturnValue({ data: [], isLoading: false, error: null });
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

  it.each(sortCases)('sorts %s / %s by raw values through the full cycle', async (tab, header) => {
    mockUseCostsByPrinter.mockReturnValue({ data: printerFixtures, isLoading: false, error: null });
    mockUseCostsByJob.mockReturnValue({ data: jobFixtures, isLoading: false, error: null });
    mockUseCostsByMaterial.mockReturnValue({ data: materialFixtures, isLoading: false, error: null });
    const user = userEvent.setup();
    render(<CostDashboardPage />);
    await user.click(screen.getByRole('tab', { name: tab }));
    const button = within(screen.getByRole('table')).getByRole('button', { name: header });
    expect(visibleRowNames()).toEqual(['Zulu', 'Alpha', 'Bravo']);
    await user.click(button);
    expect(visibleRowNames()).toEqual(['Alpha', 'Bravo', 'Zulu']);
    expect(button.closest('th')).toHaveAttribute('aria-sort', 'ascending');
    await user.click(button);
    expect(visibleRowNames()).toEqual(['Zulu', 'Bravo', 'Alpha']);
    expect(button.closest('th')).toHaveAttribute('aria-sort', 'descending');
    await user.click(button);
    expect(visibleRowNames()).toEqual(['Zulu', 'Alpha', 'Bravo']);
    expect(button.closest('th')).toHaveAttribute('aria-sort', 'none');
  });

  it.each(['Printer', 'Filament', 'Weight (g)', 'Print Time', 'Completed'])(
    'keeps missing job %s values last in both directions',
    async header => {
      const missing: CostByJob = {
        jobId: 'missing', jobName: 'Missing', ...costFields(0), totalCostUsd: 0,
      };
      mockUseCostsByJob.mockReturnValue({ data: [missing, ...jobFixtures], isLoading: false, error: null });
      const user = userEvent.setup();
      render(<CostDashboardPage />);
      await user.click(screen.getByRole('tab', { name: 'Costs by Job' }));
      const button = within(screen.getByRole('table')).getByRole('button', { name: header });
      await user.click(button);
      expect(visibleRowNames()).toEqual(['Alpha', 'Bravo', 'Zulu', 'Missing']);
      await user.click(button);
      expect(visibleRowNames()).toEqual(['Zulu', 'Bravo', 'Alpha', 'Missing']);
    }
  );

  it('sorts the displayed filament fallback and zero-job average without changing defaults', async () => {
    mockUseCostsByJob.mockReturnValue({
      data: [
        { ...jobFixtures[0], filamentName: undefined, materialType: 'Zulu' },
        { ...jobFixtures[1], filamentName: undefined, materialType: 'Alpha' },
        jobFixtures[2],
      ], isLoading: false, error: null,
    });
    mockUseCostsByPrinter.mockReturnValue({
      data: [printerFixtures[0], { ...printerFixtures[1], jobCount: 0 }, printerFixtures[2]],
      isLoading: false, error: null,
    });
    const user = userEvent.setup();
    render(<CostDashboardPage />);
    await user.click(screen.getByRole('button', { name: 'Avg/Job' }));
    expect(visibleRowNames()).toEqual(['Alpha', 'Bravo', 'Zulu']);
    await user.click(screen.getByRole('tab', { name: 'Costs by Job' }));
    await user.click(screen.getByRole('button', { name: 'Filament' }));
    expect(visibleRowNames()).toEqual(['Alpha', 'Bravo', 'Zulu']);
  });
});
