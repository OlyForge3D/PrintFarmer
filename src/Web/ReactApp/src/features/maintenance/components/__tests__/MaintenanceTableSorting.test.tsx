import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { FleetPrinterStatistics, MaintenanceComponentDto, MaintenanceTaskDto } from '@/types/maintenance';

const { fleetQuery, componentsQuery, tasksQuery, navigate } = vi.hoisted(() => ({
  fleetQuery: vi.fn(),
  componentsQuery: vi.fn(),
  tasksQuery: vi.fn(),
  navigate: vi.fn(),
}));
vi.mock('@tanstack/react-query', () => ({ useQuery: fleetQuery }));
vi.mock('react-router', () => ({ useNavigate: () => navigate }));
vi.mock('../../hooks/useMaintenanceComponents', () => ({
  useMaintenanceComponents: componentsQuery,
  useComponentCategories: () => ({ data: ['Hardware'] }),
  useCreateComponent: () => ({ mutateAsync: vi.fn() }),
  useUpdateComponent: () => ({ mutateAsync: vi.fn() }),
  useDeleteComponent: () => ({ mutateAsync: vi.fn() }),
  useExportComponents: () => ({ mutateAsync: vi.fn() }),
  useImportComponents: () => ({ mutateAsync: vi.fn() }),
}));
vi.mock('../../hooks/useTaskCatalog', () => ({
  useTaskCatalog: tasksQuery,
  useTaskCategories: () => ({ data: ['Hardware'] }),
  useCreateCatalogTask: () => ({ mutateAsync: vi.fn() }),
  useUpdateCatalogTask: () => ({ mutateAsync: vi.fn() }),
  useDeleteCatalogTask: () => ({ mutateAsync: vi.fn() }),
  useAddCatalogTaskComponent: () => ({ mutateAsync: vi.fn() }),
  useRemoveCatalogTaskComponent: () => ({ mutateAsync: vi.fn() }),
  useExportTasks: () => ({ mutateAsync: vi.fn() }),
  useImportTasks: () => ({ mutateAsync: vi.fn() }),
}));

import { FleetStatisticsTable } from '../FleetStatisticsTable';
import { PartsInventoryTab } from '../PartsInventoryTab';
import { TaskCatalogTab } from '../TaskCatalogTab';

const printer: FleetPrinterStatistics = {
  printerId: 'a', printerName: 'Alpha', isOnline: false, inMaintenance: false,
  totalPrintHours: 10, totalJobsCompleted: 10, totalJobsFailed: 0,
  totalFilamentUsedGrams: 10, totalFilamentUsedMeters: 1,
  daysUntilNextMaintenance: -1,
};
const part: MaintenanceComponentDto = {
  id: 'a', name: 'Alpha', category: 'Hardware', unitCost: null,
  inStock: 10, minimumStock: 2, createdAt: '', updatedAt: '',
};
const parts: MaintenanceComponentDto[] = [
  part,
  { ...part, id: 'z', name: 'Zulu', unitCost: 10, inStock: 2, sku: 'Z' },
  { ...part, id: 'b', name: 'Beta', unitCost: 2, inStock: 100, sku: 'B' },
];
const task: MaintenanceTaskDto = {
  id: 'task', taskName: 'Replace hardware', category: 'Hardware',
  priority: 2, isActive: true, isDefault: false, createdAt: '', updatedAt: '',
  taskComponents: parts.map((item, index) => ({
    id: `link-${item.id}`, maintenanceTaskId: 'task', maintenanceComponentId: item.id,
    componentName: item.name, quantity: [10, 2, 100][index],
  })),
};
const names = (table: HTMLElement) => within(table).getAllByRole('row').slice(1)
  .map(row => row.firstElementChild?.textContent);

describe('Maintenance table sorting', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    componentsQuery.mockReturnValue({ data: parts, isLoading: false, error: null });
    tasksQuery.mockReturnValue({ data: [task], isLoading: false, error: null });
  });

  it('sorts the full fleet before limiting rows and restores urgency order', async () => {
    fleetQuery.mockReturnValue({
      data: [
        printer,
        { ...printer, printerId: 'z', printerName: 'Zulu', totalPrintHours: 100, daysUntilNextMaintenance: null },
        { ...printer, printerId: 'b', printerName: 'Beta', totalPrintHours: 2, daysUntilNextMaintenance: 2 },
      ], isLoading: false, error: null,
    });
    const user = userEvent.setup();
    render(<FleetStatisticsTable maxRows={2} />);
    const table = screen.getByRole('table');
    expect(names(table)).toEqual(['Alpha', 'Zulu']);
    const hours = screen.getByRole('button', { name: 'Print Hours' });
    await user.click(hours);
    expect(names(table)).toEqual(['Beta', 'Alpha']);
    await user.keyboard('{Enter}');
    expect(names(table)).toEqual(['Zulu', 'Alpha']);
    await user.keyboard(' ');
    expect(names(table)).toEqual(['Alpha', 'Zulu']);
    expect(hours.closest('th')).toHaveAttribute('aria-sort', 'none');
    await user.click(screen.getByRole('button', { name: 'Next Maintenance' }));
    expect(names(table)).toEqual(['Alpha', 'Beta']);
    await user.keyboard('{Enter}');
    expect(names(table)).toEqual(['Beta', 'Alpha']);
    await user.click(within(table).getAllByRole('row')[1]);
    expect(navigate).toHaveBeenCalledWith('/printers/b/maintenance');
  });

  it('sorts inventory numerically with missing costs last and leaves cards in their default order', async () => {
    const user = userEvent.setup();
    render(<PartsInventoryTab />);
    await user.click(screen.getByRole('button', { name: 'Table view' }));
    const table = screen.getByRole('table');
    expect(names(table)).toEqual(['Alpha', 'Beta', 'Zulu']);
    const cost = screen.getByRole('button', { name: 'Unit Cost' });
    await user.click(cost);
    expect(names(table)).toEqual(['Beta', 'Zulu', 'Alpha']);
    await user.keyboard('{Enter}');
    expect(names(table)).toEqual(['Zulu', 'Beta', 'Alpha']);
    await user.keyboard(' ');
    expect(names(table)).toEqual(['Alpha', 'Beta', 'Zulu']);
    expect(cost.closest('th')).toHaveAttribute('aria-sort', 'none');
    await user.click(screen.getByRole('button', { name: 'Stock' }));
    expect(names(table)).toEqual(['Zulu', 'Alpha', 'Beta']);
    expect(within(table).getByRole('button', { name: 'Edit Zulu' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Card view' }));
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.getAllByRole('heading', { level: 4 }).map(heading => heading.textContent))
      .toEqual(['Alpha', 'Beta', 'Zulu']);
  });

  it('sorts linked task parts without changing which part is removed', async () => {
    const user = userEvent.setup();
    render(<TaskCatalogTab />);
    await user.click(screen.getByRole('button', { name: 'Edit Replace hardware' }));
    const table = screen.getByRole('grid', { name: 'Required parts' });
    const quantity = within(table).getByRole('button', { name: 'Qty' });
    await user.click(quantity);
    expect(names(table)).toEqual(['Zulu', 'Alpha', 'Beta']);
    await user.keyboard('{Enter}');
    expect(names(table)).toEqual(['Beta', 'Alpha', 'Zulu']);
    await user.keyboard(' ');
    expect(names(table)).toEqual(['Alpha', 'Zulu', 'Beta']);
    expect(quantity.closest('th')).toHaveAttribute('aria-sort', 'none');
    await user.click(within(table).getByRole('button', { name: 'Stock' }));
    expect(names(table)).toEqual(['Zulu', 'Alpha', 'Beta']);
    expect(within(within(table).getAllByRole('row')[1]).getByRole('button', { name: 'Remove Zulu' }))
      .toBeInTheDocument();
  });
});
