import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi } from 'vitest';
import { DataTable, DataTableColumn } from '../DataTable';

interface TestItem {
  id: string;
  name: string;
  value: number;
  status: string;
}

const mockData: TestItem[] = [
  { id: '1', name: 'Item A', value: 100, status: 'active' },
  { id: '2', name: 'Item B', value: 50, status: 'inactive' },
  { id: '3', name: 'Item C', value: 75, status: 'active' },
];

const mockColumns: DataTableColumn<TestItem>[] = [
  {
    key: 'name',
    header: 'Name',
    sortable: true,
    sort: (a, b) => a.name.localeCompare(b.name),
    render: (item) => <span>{item.name}</span>,
  },
  {
    key: 'value',
    header: 'Value',
    sortable: true,
    sort: (a, b) => a.value - b.value,
    render: (item) => <span>{item.value}</span>,
  },
  {
    key: 'status',
    header: 'Status',
    render: (item) => <span>{item.status}</span>,
  },
];

describe('DataTable', () => {
  it('should render table with data', () => {
    render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
      />
    );

    expect(screen.getByText('Name')).toBeInTheDocument();
    expect(screen.getByText('Value')).toBeInTheDocument();
    expect(screen.getByText('Status')).toBeInTheDocument();
    expect(screen.getByText('Item A')).toBeInTheDocument();
    expect(screen.getByText('Item B')).toBeInTheDocument();
    expect(screen.getByText('Item C')).toBeInTheDocument();
  });

  it('should render empty message when no data', () => {
    render(
      <DataTable
        data={[]}
        columns={mockColumns}
        getRowKey={(item) => item.id}
        emptyMessage="No items found"
      />
    );

    expect(screen.getByText('No items found')).toBeInTheDocument();
  });

  it('should handle sortable columns', () => {
    render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
      />
    );

    const nameHeader = screen.getByText('Name').closest('th');
    expect(nameHeader).toBeInTheDocument();
  });

  it('should render with default sort column', () => {
    render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
        defaultSortColumn="name"
        defaultSortDirection="asc"
      />
    );

    const rows = screen.getAllByRole('row');
    // Should have header row + 3 data rows
    expect(rows.length).toBe(4);
  });

  it('should render actions column when renderActions provided', () => {
    const mockRenderActions = vi.fn((item: TestItem) => (
      <button>Edit {item.name}</button>
    ));

    render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
        renderActions={mockRenderActions}
        actionsHeader="Actions"
      />
    );

    expect(screen.getByText('Actions')).toBeInTheDocument();
    expect(screen.getByText('Edit Item A')).toBeInTheDocument();
    expect(screen.getByText('Edit Item B')).toBeInTheDocument();
    expect(screen.getByText('Edit Item C')).toBeInTheDocument();
  });

  it('should enable keyboard selection when rows are selectable', async () => {
    const user = userEvent.setup();
    const onRowSelect = vi.fn();

    render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
        onRowSelect={onRowSelect}
      />
    );

    const grid = screen.getByRole('grid');
    expect(grid).toHaveAttribute('tabIndex', '0');

    await user.tab();
    expect(grid).toHaveFocus();

    await user.keyboard('{ArrowDown}{Enter}');

    expect(onRowSelect).toHaveBeenCalledWith(mockData[0], 0);
    expect(screen.getAllByRole('row')[0]).not.toHaveAttribute('aria-selected');
    expect(screen.getByRole('row', { name: /Item A/ })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('row', { name: /Item B/ })).toHaveAttribute('aria-selected', 'false');
  });

  it('should make selectable rows hoverable and clickable', () => {
    const onRowSelect = vi.fn();

    render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
        onRowSelect={onRowSelect}
      />
    );

    const rows = screen.getAllByRole('row').slice(1);
    rows.forEach((row) => expect(row).toHaveClass('hover:bg-pf-bg-1'));

    fireEvent.click(rows[1]);

    expect(onRowSelect).toHaveBeenCalledWith(mockData[1], 1);
  });

  it('should keep read-only rows non-hoverable', () => {
    render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
      />
    );

    const rows = screen.getAllByRole('row').slice(1);
    rows.forEach((row) => expect(row).not.toHaveClass('hover:bg-pf-bg-1'));
  });

  it('should not select a row when its action button is clicked', () => {
    const onRowSelect = vi.fn();
    const onEdit = vi.fn();

    render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
        onRowSelect={onRowSelect}
        renderActions={(item) => (
          <button type="button" onClick={() => onEdit(item)}>
            Edit {item.name}
          </button>
        )}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: 'Edit Item A' }));

    expect(onEdit).toHaveBeenCalledWith(mockData[0]);
    expect(onRowSelect).not.toHaveBeenCalled();
  });

  it('should not select the active row when a nested action is used by keyboard', async () => {
    const user = userEvent.setup();
    const onRowSelect = vi.fn();
    const onEdit = vi.fn();

    render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
        onRowSelect={onRowSelect}
        renderActions={(item) => (
          <button type="button" onClick={() => onEdit(item)}>
            Edit {item.name}
          </button>
        )}
      />
    );

    const grid = screen.getByRole('grid');
    const editButton = screen.getByRole('button', { name: 'Edit Item A' });

    await user.tab();
    expect(grid).toHaveFocus();
    await user.keyboard('{ArrowDown}');
    const activeRowId = grid.getAttribute('aria-activedescendant');
    await user.tab();
    expect(editButton).toHaveFocus();
    await user.keyboard('{ArrowDown}{Enter}');

    expect(onEdit).toHaveBeenCalledWith(mockData[0]);
    expect(onRowSelect).not.toHaveBeenCalled();
    expect(grid).toHaveAttribute('aria-activedescendant', activeRowId);
  });

  it('should enable keyboard navigation when specified', () => {
    const { container } = render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
        keyboardNavigation
      />
    );

    const grid = screen.getByRole('grid');
    const rows = screen.getAllByRole('row');

    expect(grid).toHaveAttribute('tabIndex', '0');
    expect(grid).not.toHaveAttribute('aria-activedescendant');
    expect(rows.length).toBeGreaterThan(1);
    expect(container.querySelectorAll('tbody tr[data-rowindex]')).toHaveLength(mockData.length);
    expect(screen.getAllByRole('rowgroup')).toHaveLength(2);
    expect(screen.getAllByRole('columnheader')).toHaveLength(mockColumns.length);
    expect(screen.getAllByRole('gridcell')).toHaveLength(mockData.length * mockColumns.length);
    rows.slice(1).forEach((row) => {
      expect(row.id).not.toBe('');
      expect(row).not.toHaveAttribute('aria-selected');
    });
  });

  it('should announce the active row while DOM focus remains on the grid', async () => {
    const user = userEvent.setup();

    render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
        keyboardNavigation
        ariaLabel="Inventory"
      />
    );

    const grid = screen.getByRole('grid', { name: 'Inventory' });
    const rows = screen.getAllByRole('row').slice(1);

    await user.tab();
    await user.keyboard('{ArrowDown}');

    expect(grid).toHaveFocus();
    expect(grid).toHaveAttribute('aria-activedescendant', rows[0].id);
    expect(document.getElementById(rows[0].id)).toBe(rows[0]);

    await user.keyboard('{End}');
    expect(grid).toHaveAttribute('aria-activedescendant', rows[2].id);

    await user.keyboard('{ArrowDown}');
    expect(grid).toHaveAttribute('aria-activedescendant', rows[2].id);

    await user.keyboard('{Home}{ArrowUp}');
    expect(grid).toHaveAttribute('aria-activedescendant', rows[0].id);
  });

  it('should use stable, unique row-key IDs across sorting and table instances', () => {
    render(
      <>
        <DataTable
          data={mockData}
          columns={mockColumns}
          getRowKey={(item) => item.id}
          keyboardNavigation
          ariaLabel="First inventory"
        />
        <DataTable
          data={mockData}
          columns={mockColumns}
          getRowKey={(item) => item.id}
          keyboardNavigation
          ariaLabel="Second inventory"
        />
      </>
    );

    const initialIds = screen.getAllByRole('row')
      .filter((row) => row.id)
      .map((row) => row.id);
    expect(new Set(initialIds).size).toBe(initialIds.length);

    const itemAId = screen.getAllByRole('row', { name: /Item A/ })[0].id;
    fireEvent.click(screen.getAllByRole('columnheader', { name: /Value/ })[0]);

    expect(screen.getAllByRole('row', { name: /Item A/ })[0]).toHaveAttribute('id', itemAId);
  });

  it('should keep the active row associated with its key when sorting', async () => {
    const user = userEvent.setup();

    render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
        keyboardNavigation
      />
    );

    const grid = screen.getByRole('grid');
    await user.tab();
    await user.keyboard('{ArrowDown}');
    const itemA = screen.getByRole('row', { name: /Item A/ });
    expect(grid).toHaveAttribute('aria-activedescendant', itemA.id);

    fireEvent.click(screen.getByRole('columnheader', { name: /Value/ }));

    expect(grid).toHaveAttribute('aria-activedescendant', itemA.id);
    expect(itemA).toHaveClass('ring-2');
  });

  it('should remove an active descendant reference when the active row is absent', async () => {
    const user = userEvent.setup();
    const { rerender } = render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
        keyboardNavigation
      />
    );

    const grid = screen.getByRole('grid');
    await user.tab();
    await user.keyboard('{ArrowDown}');
    const removedRowId = grid.getAttribute('aria-activedescendant');
    expect(removedRowId).not.toBeNull();

    rerender(
      <DataTable
        data={mockData.slice(1)}
        columns={mockColumns}
        getRowKey={(item) => item.id}
        keyboardNavigation
      />
    );

    expect(grid).not.toHaveAttribute('aria-activedescendant');
    expect(document.getElementById(removedRowId!)).not.toBeInTheDocument();
  });

  it('should preserve native table semantics when keyboard navigation is disabled', () => {
    render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
      />
    );

    const table = screen.getByRole('table');
    expect(table).not.toHaveAttribute('tabIndex');
    expect(table).not.toHaveAttribute('aria-activedescendant');
    screen.getAllByRole('row').slice(1).forEach((row) => {
      expect(row).not.toHaveAttribute('id');
      expect(row).not.toHaveAttribute('aria-selected');
    });
  });

  it('should apply custom className', () => {
    const { container } = render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
        className="custom-table-class"
      />
    );

    const table = container.querySelector('table');
    expect(table).toHaveClass('custom-table-class');
  });

  it('should render column with custom headerClassName', () => {
    const columnsWithCustomClass: DataTableColumn<TestItem>[] = [
      {
        key: 'name',
        header: 'Name',
        headerClassName: 'custom-header',
        render: (item) => <span>{item.name}</span>,
      },
    ];

    render(
      <DataTable
        data={mockData}
        columns={columnsWithCustomClass}
        getRowKey={(item) => item.id}
      />
    );

    const nameHeader = screen.getByText('Name').closest('th');
    expect(nameHeader).toHaveClass('custom-header');
  });

  it('should handle empty data array', () => {
    render(
      <DataTable
        data={[]}
        columns={mockColumns}
        getRowKey={(item) => item.id}
      />
    );

    expect(screen.getByText('No data available.')).toBeInTheDocument();
  });

  it('should render with custom actions width', () => {
    render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
        renderActions={() => <button>Edit</button>}
        actionsWidth="w-32"
      />
    );

    const actionsHeader = screen.getByText('Actions').closest('th');
    expect(actionsHeader).toHaveClass('w-32');
  });

  it('should use getRowKey for unique keys', () => {
    const { container } = render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => `row-${item.id}`}
      />
    );

    const rows = container.querySelectorAll('tbody tr');
    expect(rows.length).toBe(mockData.length);
  });
});
