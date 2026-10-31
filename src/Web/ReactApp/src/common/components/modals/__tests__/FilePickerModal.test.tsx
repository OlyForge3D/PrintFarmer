import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import { FilePickerModal } from '@/common/components/modals/FilePickerModal';
import { apiClient } from '@/services/api';

vi.mock('@/services/api', () => ({
  apiClient: {
    getGcodeFilesQuery: vi.fn().mockResolvedValue({ files: [], totalPages: 1 }),
    getTags: vi.fn().mockResolvedValue([
      { id: 'workshop', name: 'Workshop' },
      { id: 'production', name: 'Production' },
    ]),
  },
}));

describe('FilePickerModal filters', () => {
  it('uses the shared disclosure and resets all tags without changing search or sort', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const onSelect = vi.fn();
    render(
      <QueryClientProvider client={client}>
        <FilePickerModal isOpen onClose={vi.fn()} onSelect={onSelect} />
      </QueryClientProvider>,
    );

    const filters = screen.getByRole('button', { name: 'Filters' });
    const search = screen.getByRole('searchbox', { name: 'Search files' });
    const sort = screen.getByRole('combobox', { name: 'Sort by' });
    expect(search.compareDocumentPosition(filters) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(filters.compareDocumentPosition(sort) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(filters).toHaveAttribute('aria-expanded', 'false');
    fireEvent.click(filters);
    const panel = screen.getByRole('region', { name: 'Filters' });
    expect(panel.id).toBe(filters.getAttribute('aria-controls'));
    expect(screen.getByRole('button', { name: 'Reset filters' })).toBeDisabled();

    fireEvent.change(screen.getByRole('searchbox', { name: 'Search files' }), {
      target: { value: 'adapter' },
    });
    fireEvent.change(screen.getByRole('combobox', { name: 'Sort by' }), {
      target: { value: 'name' },
    });
    fireEvent.click(await screen.findByRole('button', { name: 'Workshop' }));
    fireEvent.click(screen.getByRole('button', { name: 'Production' }));
    expect(screen.getByRole('button', { name: 'Filters (2)' })).toBeInTheDocument();

    await waitFor(() => expect(apiClient.getGcodeFilesQuery).toHaveBeenLastCalledWith(
      expect.objectContaining({ search: 'adapter', sortBy: 'name', tagIds: ['workshop', 'production'] }),
    ));
    fireEvent.click(screen.getByRole('button', { name: 'Reset filters' }));
    await waitFor(() => expect(apiClient.getGcodeFilesQuery).toHaveBeenLastCalledWith(
      expect.objectContaining({ search: 'adapter', sortBy: 'name', page: 1 }),
    ));
    expect(vi.mocked(apiClient.getGcodeFilesQuery).mock.lastCall?.[0]).not.toHaveProperty('tagIds');
    expect(screen.getByRole('button', { name: 'Workshop' })).toHaveAttribute('aria-pressed', 'false');
    expect(screen.getByRole('button', { name: 'Production' })).toHaveAttribute('aria-pressed', 'false');
    expect(screen.getByRole('button', { name: 'Reset filters' })).toBeDisabled();
    expect(filters).toHaveFocus();
    expect(onSelect).not.toHaveBeenCalled();
    client.clear();
  });
});
