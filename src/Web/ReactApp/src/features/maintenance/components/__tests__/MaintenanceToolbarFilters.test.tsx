import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PartsInventoryTab } from '../PartsInventoryTab';
import { TaskCatalogTab } from '../TaskCatalogTab';

vi.mock('../../hooks/useTaskCatalog', () => ({
  useTaskCatalog: () => ({ data: [], isLoading: false, error: null }),
  useTaskCategories: () => ({ data: ['Routine', 'Extruder'] }),
  useCreateCatalogTask: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useUpdateCatalogTask: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useDeleteCatalogTask: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useAddCatalogTaskComponent: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useRemoveCatalogTaskComponent: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useExportTasks: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useImportTasks: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));

vi.mock('../../hooks/useMaintenanceComponents', () => ({
  useMaintenanceComponents: () => ({ data: [], isLoading: false, error: null }),
  useComponentCategories: () => ({ data: ['Electrical', 'Mechanical'] }),
  useCreateComponent: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useDeleteComponent: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useUpdateComponent: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useExportComponents: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useImportComponents: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));

describe('maintenance toolbar filters', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('keeps task search visible while category selection is disclosed, counted, and reset independently', () => {
    render(<TaskCatalogTab />);
    const search = screen.getByPlaceholderText('Search tasks…');
    fireEvent.change(search, { target: { value: 'nozzle' } });
    expect(screen.getByRole('button', { name: 'Filters' })).toHaveAttribute('aria-expanded', 'false');
    expect(screen.getByLabelText('Filter by category').closest('[role="region"]')).not.toBeVisible();
    expect(search).toBeVisible();

    fireEvent.click(screen.getByRole('button', { name: 'Filters' }));
    fireEvent.change(screen.getByLabelText('Filter by category'), { target: { value: 'Extruder' } });
    expect(screen.getByRole('button', { name: 'Filters (1)' })).toHaveAttribute('aria-expanded', 'true');

    fireEvent.click(screen.getByRole('button', { name: 'Reset filters' }));
    expect(screen.getByLabelText('Filter by category')).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Filters' })).toHaveAttribute('aria-expanded', 'true');
    expect(search).toHaveValue('nozzle');
  });

  it('keeps inventory search and view mode outside the resettable category filter', () => {
    render(<PartsInventoryTab />);
    const search = screen.getByPlaceholderText('Search parts…');
    fireEvent.change(search, { target: { value: 'bearing' } });
    expect(screen.getByRole('button', { name: 'Filters' })).toHaveAttribute('aria-expanded', 'false');
    expect(screen.getByLabelText('Filter by category').closest('[role="region"]')).not.toBeVisible();
    expect(screen.getByRole('group', { name: 'Maintenance inventory view' })).toBeVisible();

    fireEvent.click(screen.getByRole('button', { name: 'Filters' }));
    fireEvent.change(screen.getByLabelText('Filter by category'), { target: { value: 'Mechanical' } });
    expect(screen.getByRole('button', { name: 'Filters (1)' })).toHaveAttribute('aria-expanded', 'true');

    fireEvent.click(screen.getByRole('button', { name: 'Reset filters' }));
    expect(screen.getByLabelText('Filter by category')).toHaveValue('');
    expect(search).toHaveValue('bearing');
    expect(screen.getByRole('group', { name: 'Maintenance inventory view' })).toBeVisible();
  });
});
