import { fireEvent, render, screen } from '@testing-library/react';
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

  it('should support row selection callback', () => {
    const onRowSelect = vi.fn();

    render(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
        onRowSelect={onRowSelect}
        keyboardNavigation
      />
    );

    const table = screen.getByRole('table');
    fireEvent.keyDown(table, { key: 'ArrowDown' });
    fireEvent.keyDown(table, { key: 'Enter' });

    expect(onRowSelect).toHaveBeenCalledWith(mockData[0], 0);
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

    const table = screen.getByRole('table');
    const rows = screen.getAllByRole('row');

    expect(table).toHaveAttribute('tabIndex', '0');
    expect(rows.length).toBeGreaterThan(1);
    expect(container.querySelectorAll('tbody tr[data-rowindex]')).toHaveLength(mockData.length);
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
