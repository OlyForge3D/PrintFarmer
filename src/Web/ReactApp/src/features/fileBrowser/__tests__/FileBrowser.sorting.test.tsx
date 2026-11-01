import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { FileBrowser } from '../components/FileBrowser';
import type { ColumnDef, FileItem, FileQueryState } from '../types';

const columns: ColumnDef[] = [
  { key: 'fileName', label: 'Name', sortable: true, sortKey: 'name' },
  { key: 'fileSize', label: 'Size', sortable: true, sortKey: 'size' },
  { key: 'uploadedAt', label: 'Modified', sortable: true, sortKey: 'date' },
];
const files: FileItem[] = [
  { id: '1', path: '/part.gcode', fileName: 'part.gcode', fileSize: 123, isDirectory: false },
];

function renderBrowser(displayColumns = columns) {
  const fetcher = vi.fn(async (params: unknown) => ({
    items: files, totalItems: 1, totalPages: 1, page: (params as FileQueryState).page,
  }));
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <FileBrowser config={{
        fetcher, mapDomainToFileItem: file => file,
        defaultSort: { sortBy: 'date', sortOrder: 'desc' },
      }} columns={displayColumns} />
    </QueryClientProvider>,
  );
  return fetcher;
}

describe('FileBrowser provider sorting', () => {
  it('maps visible headers to server fields, restores the default, and keeps grid controls usable', async () => {
    const user = userEvent.setup();
    const fetcher = renderBrowser();
    await screen.findByText('part.gcode');
    expect(screen.getByRole('combobox', { name: 'Sort by' })).toHaveValue('date');
    await user.click(screen.getByRole('button', { name: 'Explorer view' }));
    expect(screen.queryByRole('combobox', { name: 'Sort by' })).not.toBeInTheDocument();
    const name = screen.getByRole('button', { name: 'Name' });
    expect(name.closest('th')).toHaveAttribute('aria-sort', 'none');
    expect(screen.getByRole('button', { name: 'Modified' }).closest('th')).toHaveAttribute('aria-sort', 'descending');
    name.focus();
    await user.keyboard('{Enter}');
    await waitFor(() => expect(fetcher).toHaveBeenLastCalledWith(
      expect.objectContaining({ sortBy: 'name', sortOrder: 'asc', page: 1 }), expect.any(AbortSignal),
    ));
    expect(name.closest('th')).toHaveAttribute('aria-sort', 'ascending');
    await user.keyboard(' ');
    expect(name.closest('th')).toHaveAttribute('aria-sort', 'descending');
    await user.click(name);
    await waitFor(() => expect(fetcher).toHaveBeenLastCalledWith(
      expect.objectContaining({ sortBy: 'date', sortOrder: 'desc', page: 1 }), expect.any(AbortSignal),
    ));
    expect(name.closest('th')).toHaveAttribute('aria-sort', 'none');
    expect(screen.getByRole('button', { name: 'Modified' }).closest('th')).toHaveAttribute('aria-sort', 'none');
    await user.click(screen.getByRole('button', { name: 'Size' }));
    await waitFor(() => expect(fetcher).toHaveBeenLastCalledWith(
      expect.objectContaining({ sortBy: 'size', sortOrder: 'asc' }), expect.any(AbortSignal),
    ));
    await user.click(screen.getByRole('button', { name: 'Size' }));
    await user.click(screen.getByRole('button', { name: 'Size' }));
    await user.click(screen.getByRole('button', { name: 'Grid view' }));
    expect(screen.getByRole('combobox', { name: 'Sort by' })).toHaveValue('');
    expect(screen.getByRole('button', { name: /Sort direction/ })).toBeDisabled();
    await user.selectOptions(screen.getByRole('combobox', { name: 'Sort by' }), 'name');
    expect(screen.getByRole('button', { name: /Sort direction/ })).toBeEnabled();
  });

  it('retains toolbar sorting when a provider option has no visible sortable column', async () => {
    const user = userEvent.setup();
    renderBrowser(columns.filter(column => column.sortKey !== 'date'));
    await screen.findByText('part.gcode');
    await user.click(screen.getByRole('button', { name: 'Explorer view' }));
    expect(screen.getByRole('combobox', { name: 'Sort by' })).toHaveValue('date');
  });
});
