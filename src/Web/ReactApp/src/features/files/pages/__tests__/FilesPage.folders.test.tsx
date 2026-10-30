import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/services/api';
import type { UnifiedFilesQueryRequest, UnifiedFilesQueryResponse } from '@/types/api';
import { FilesPage } from '../FilesPage';

vi.mock('@/services/api', () => ({
  apiClient: {
    getUnifiedFiles: vi.fn(),
    listModelsFolders: vi.fn(),
    getGcodeFilesFolders: vi.fn(),
  },
}));
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: () => false }),
}));
vi.mock('@/common/hooks/useApi', () => ({
  usePrinters: () => ({ data: [] }),
}));
vi.mock('@/common/components/modals/ModelUploadModal', () => ({ ModelUploadModal: () => null }));
vi.mock('@/common/components/modals/GcodeUploadModal', () => ({ GcodeUploadModal: () => null }));
vi.mock('@/common/components/modals/BulkTagAssignmentModal', () => ({ BulkTagAssignmentModal: () => null }));
vi.mock('@/components/TaggingModal', () => ({ TaggingModal: () => null }));
vi.mock('@/features/projects/components/AddToProjectModal', () => ({ AddToProjectModal: () => null }));
vi.mock('@/common/components/modals/ConfirmationModal', () => ({ ConfirmationModal: () => null }));

function response(request: UnifiedFilesQueryRequest): UnifiedFilesQueryResponse {
  const page = request.page ?? 1;
  return {
    items: [{
      source: 'Model', id: `model-${page}`, path: request.path ?? '/', name: `part-${page}.stl`,
      fileName: `stored-${page}.stl`, fileSize: 10, fileType: 'stl',
      uploadedAt: '2026-08-06T00:00:00Z', url: `/api/3d-models/file/model-${page}`,
    }],
    page, pageSize: 50, totalPages: request.path ? 1 : 3,
    totalItems: request.path ? 1 : 101, totalSize: request.path ? 10 : 1010,
  };
}

function LocationProbe() {
  const location = useLocation();
  return <output aria-label="Current URL">{location.pathname}{location.search}</output>;
}

function renderPage(mode: 'grid' | 'explorer', url = '/files') {
  localStorage.setItem('printfarmer-files-viewmode', mode);
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[url]}><FilesPage /><LocationProbe /></MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('FilesPage folder navigation with the real file browser', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiClient.getUnifiedFiles).mockImplementation(async request => response(request));
    vi.mocked(apiClient.listModelsFolders).mockResolvedValue([
      { path: '/Parts', fileName: 'Parts', isDirectory: true },
    ]);
    vi.mocked(apiClient.getGcodeFilesFolders).mockResolvedValue([]);
  });

  it.each(['grid', 'explorer'] as const)('matches sort-order and folder button sizing in %s mode without changing sorting', async mode => {
    renderPage(mode);
    await screen.findByRole('checkbox', { name: 'Select part-1.stl' });
    const foldersButton = screen.getByRole('button', { name: 'Folders' });
    const sortButton = screen.getByRole('button', { name: 'Sort direction: descending' });
    expect(sortButton.closest('[data-tour="gcode-toolbar"]')).toHaveClass('pf-toolbar');
    expect(screen.getByTitle('Grid view').parentElement).not.toHaveClass('p-1');
    for (const button of [foldersButton, sortButton]) {
      expect(button).toHaveClass('pf-control');
      expect(button).toHaveAttribute('data-size', 'md');
    }
    fireEvent.click(sortButton);
    await waitFor(() => expect(apiClient.getUnifiedFiles).toHaveBeenLastCalledWith(
      expect.objectContaining({ sortOrder: 'asc' }), expect.any(AbortSignal),
    ));
    expect(screen.getByRole('button', { name: 'Sort direction: ascending' })).toBe(sortButton);
    expect(sortButton).toHaveClass('pf-control');
    expect(sortButton).toHaveAttribute('data-size', 'md');
    fireEvent.click(sortButton);
    await waitFor(() => expect(apiClient.getUnifiedFiles).toHaveBeenLastCalledWith(
      expect.objectContaining({ sortOrder: 'desc' }), expect.any(AbortSignal),
    ));
  });

  it.each(['grid', 'explorer'] as const)('filters on the server and resets page and selection in %s mode', async mode => {
    renderPage(mode);
    expect(await screen.findByRole('checkbox', { name: 'Select part-1.stl' })).not.toBeChecked();
    expect(apiClient.getUnifiedFiles).toHaveBeenLastCalledWith(
      expect.objectContaining({ path: undefined, page: 1 }), expect.any(AbortSignal),
    );
    expect(screen.queryByRole('list', { name: 'Folder tree' })).not.toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: 'File path' })).not.toBeInTheDocument();
    const foldersButton = screen.getByRole('button', { name: 'Folders' });
    expect(foldersButton.textContent).toBe('');
    expect(foldersButton.querySelector('svg')).toBeInTheDocument();
    for (const [label, control] of [
      ['Search', screen.getByRole('searchbox', { name: 'Search' })],
      ['Type', screen.getByRole('combobox', { name: 'Type' })],
      ['Sort', screen.getByRole('combobox', { name: 'Sort by' })],
      ['Folders', foldersButton],
    ] as const) {
      const fieldLabel = screen.getByText(label, { selector: 'label' });
      expect(fieldLabel).toBeVisible();
      expect(fieldLabel).toHaveAttribute('for', control.id);
      expect(fieldLabel.parentElement).toHaveClass('pf-toolbar-field');
      expect(fieldLabel.parentElement?.firstElementChild).toBe(fieldLabel);
    }
    expect(foldersButton).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('combobox', { name: 'Folder' })).not.toBeInTheDocument();
    const panel = document.getElementById(foldersButton.getAttribute('aria-controls')!);
    expect(panel).not.toBeVisible();
    fireEvent.click(foldersButton);
    expect(foldersButton).toHaveAttribute('aria-expanded', 'true');
    expect(panel).toBeVisible();
    expect(within(panel!).getByRole('group', { name: 'Folder controls' })).toHaveClass('pf-toolbar');
    fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Select part-2.stl' }));
    expect(screen.getByRole('checkbox', { name: 'Select part-2.stl' })).toBeChecked();

    const select = screen.getByRole('combobox', { name: 'Folder' });
    await waitFor(() => expect(within(select).getByRole('option', { name: 'Parts' })).toBeInTheDocument());
    fireEvent.change(select, { target: { value: '/Parts' } });
    await waitFor(() => expect(apiClient.getUnifiedFiles).toHaveBeenLastCalledWith(
      expect.objectContaining({ path: '/Parts', page: 1 }), expect.any(AbortSignal),
    ));
    expect(await screen.findByRole('checkbox', { name: 'Select part-1.stl' })).not.toBeChecked();
    expect(screen.queryByText('1 selected')).not.toBeInTheDocument();
    expect(select).toHaveValue('/Parts');
    expect(screen.queryByRole('button', { name: 'Next page' })).not.toBeInTheDocument();
    expect(foldersButton.textContent).toBe('');
    expect(foldersButton).toHaveAttribute('title', 'Folders: /Parts');
    fireEvent.click(foldersButton);
    expect(panel).not.toBeVisible();
    expect(screen.queryByRole('combobox', { name: 'Folder' })).not.toBeInTheDocument();
    expect(apiClient.getUnifiedFiles).toHaveBeenLastCalledWith(
      expect.objectContaining({ path: '/Parts' }), expect.any(AbortSignal),
    );
    fireEvent.click(foldersButton);
    expect(screen.getByRole('combobox', { name: 'Folder' })).toHaveValue('/Parts');

    fireEvent.click(screen.getByRole('button', { name: mode === 'grid' ? 'Explorer view' : 'Grid view' }));
    await waitFor(() => expect(screen.getByRole('region', { name: 'File browser' })).toHaveAttribute('aria-busy', 'false'));
    expect(select).toHaveValue('/Parts');
    expect(screen.queryByRole('list', { name: 'Folder tree' })).not.toBeInTheDocument();
    const breadcrumb = within(screen.getByRole('navigation', { name: 'Folder path' }));
    fireEvent.click(breadcrumb.getByRole('button', { name: 'Root' }));
    await waitFor(() => expect(apiClient.getUnifiedFiles).toHaveBeenLastCalledWith(
      expect.objectContaining({ path: '/', page: 1 }), expect.any(AbortSignal),
    ));
    fireEvent.click(breadcrumb.getByRole('button', { name: 'All folders' }));
    await waitFor(() => expect(apiClient.getUnifiedFiles).toHaveBeenLastCalledWith(
      expect.objectContaining({ path: undefined, page: 1 }), expect.any(AbortSignal),
    ));
    expect(select).toHaveValue('');
    await waitFor(() => expect(screen.getByText('Page 1 of 3')).toBeInTheDocument());
  });

  it.each(['grid', 'explorer'] as const)('uses one URL-driven type dropdown and preserves folder scope in %s mode', async mode => {
    renderPage(mode, '/files?type=models&remoteSearch=usb');
    await screen.findByRole('checkbox', { name: 'Select part-1.stl' });
    const type = screen.getByRole('combobox', { name: 'Type' });
    expect(type).toHaveValue('models');
    expect(within(type).getAllByRole('option').map(option => option.textContent))
      .toEqual(['All', '3D Models', 'G-Code', 'Other']);
    for (const name of ['All', '3D Models', 'G-Code', 'Other']) {
      expect(screen.queryByRole('button', { name })).not.toBeInTheDocument();
    }

    fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
    await screen.findByRole('checkbox', { name: 'Select part-2.stl' });
    fireEvent.change(type, { target: { value: 'gcode' } });
    await waitFor(() => expect(apiClient.getUnifiedFiles).toHaveBeenLastCalledWith(
      expect.objectContaining({ filter: 'gcode' }), expect.any(AbortSignal),
    ));

    fireEvent.click(screen.getByRole('button', { name: 'Folders' }));
    await screen.findByRole('option', { name: 'Parts' });
    fireEvent.change(screen.getByRole('combobox', { name: 'Folder' }), { target: { value: '/Parts' } });
    await waitFor(() => expect(apiClient.getUnifiedFiles).toHaveBeenLastCalledWith(
      expect.objectContaining({ path: '/Parts', filter: 'gcode' }), expect.any(AbortSignal),
    ));
    fireEvent.click(screen.getByRole('button', { name: 'Folders' }));
    for (const filter of ['other', 'models', 'all']) {
      fireEvent.click(await screen.findByRole('checkbox', { name: 'Select part-1.stl' }));
      fireEvent.change(type, { target: { value: filter } });
      await waitFor(() => expect(apiClient.getUnifiedFiles).toHaveBeenLastCalledWith(
        expect.objectContaining({ filter, path: '/Parts', page: 1 }), expect.any(AbortSignal),
      ));
      await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Select part-1.stl' })).not.toBeChecked());
      const currentUrl = screen.getByRole('status', { name: 'Current URL' }).textContent ?? '';
      const params = new URL(currentUrl, 'http://localhost').searchParams;
      expect(params.get('type')).toBe(filter === 'all' ? null : filter);
      expect(params.get('remoteSearch')).toBe('usb');
      expect(screen.getByRole('button', { name: 'Folders' })).toHaveAttribute('aria-expanded', 'false');
      expect(screen.getByRole('button', { name: 'Folders' })).toHaveAttribute('title', 'Folders: /Parts');
    }
  });

  it('shows an API failure and retries the same folder instead of presenting a successful empty result', async () => {
    renderPage('explorer');
    await screen.findByRole('checkbox', { name: 'Select part-1.stl' });
    fireEvent.click(screen.getByRole('button', { name: 'Folders' }));
    await screen.findByRole('option', { name: 'Parts' });
    vi.mocked(apiClient.getUnifiedFiles).mockRejectedValueOnce({ statusCode: 503, message: 'Library offline' });
    fireEvent.change(screen.getByRole('combobox', { name: 'Folder' }), { target: { value: '/Parts' } });
    expect(await screen.findByRole('alert')).toHaveTextContent('Library offline');
    fireEvent.click(screen.getByRole('button', { name: 'Retry files' }));
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
    expect(apiClient.getUnifiedFiles).toHaveBeenLastCalledWith(
      expect.objectContaining({ path: '/Parts', page: 1 }), expect.any(AbortSignal),
    );
  });
});
