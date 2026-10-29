import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
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

function renderPage(mode: 'grid' | 'explorer') {
  localStorage.setItem('printfarmer-files-viewmode', mode);
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/files']}><FilesPage /></MemoryRouter>
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

  it.each(['grid', 'explorer'] as const)('filters on the server and resets page and selection in %s mode', async mode => {
    renderPage(mode);
    expect(await screen.findByRole('checkbox', { name: 'Select part-1.stl' })).not.toBeChecked();
    expect(apiClient.getUnifiedFiles).toHaveBeenLastCalledWith(
      expect.objectContaining({ path: undefined, page: 1 }), expect.any(AbortSignal),
    );
    expect(screen.queryByRole('list', { name: 'Folder tree' })).not.toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: 'File path' })).not.toBeInTheDocument();
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

  it('shows an API failure and retries the same folder instead of presenting a successful empty result', async () => {
    renderPage('explorer');
    await screen.findByRole('checkbox', { name: 'Select part-1.stl' });
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
