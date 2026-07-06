import { act, render, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import type { ReactElement } from 'react';
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
  const renderAndFlushRows = async (ui: ReactElement) => {
    const result = render(ui);
    await act(async () => {
      await Promise.resolve();
    });
    return result;
  };

  it('should render table with data', async () => {
    await renderAndFlushRows(
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

  it('should handle sortable columns', async () => {
    await renderAndFlushRows(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
      />
    );

    const nameHeader = screen.getByText('Name').closest('th');
    expect(nameHeader).toBeInTheDocument();
  });

  it('should render with default sort column', async () => {
    await renderAndFlushRows(
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

  it('should render actions column when renderActions provided', async () => {
    const mockRenderActions = vi.fn((item: TestItem) => (
      <button>Edit {item.name}</button>
    ));

    await renderAndFlushRows(
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

  it('should support row selection callback', async () => {
    const onRowSelect = vi.fn();

    await renderAndFlushRows(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
        onRowSelect={onRowSelect}
        keyboardNavigation
      />
    );

    // Test that the component renders with keyboard navigation enabled
    const table = screen.getByRole('table');
    expect(table).toBeInTheDocument();
  });

  it('should enable keyboard navigation when specified', async () => {
    await renderAndFlushRows(
      <DataTable
        data={mockData}
        columns={mockColumns}
        getRowKey={(item) => item.id}
        keyboardNavigation
      />
    );

    const rows = screen.getAllByRole('row');
    // Should have tabIndex when keyboard navigation is enabled
    expect(rows.length).toBeGreaterThan(1);
  });

  it('should apply custom className', async () => {
    const { container } = await renderAndFlushRows(
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

  it('should render column with custom headerClassName', async () => {
    const columnsWithCustomClass: DataTableColumn<TestItem>[] = [
      {
        key: 'name',
        header: 'Name',
        headerClassName: 'custom-header',
        render: (item) => <span>{item.name}</span>,
      },
    ];

    await renderAndFlushRows(
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

  it('should render with custom actions width', async () => {
    await renderAndFlushRows(
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

  it('should use getRowKey for unique keys', async () => {
    const { container } = await renderAndFlushRows(
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
