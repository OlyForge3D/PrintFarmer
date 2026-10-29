import React, { useImperativeHandle } from 'react';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { UseFileBrowserConfig } from '@/features/fileBrowser/types';

const M1 = 'cccccccc-1111-1111-1111-111111111111';
const G1 = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const G2 = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';

const mocks = vi.hoisted(() => ({
  deleteModel3dFiles: vi.fn(),
  deleteGcodeFiles: vi.fn(),
  deleteModel3dFile: vi.fn(),
  deleteGcodeFile: vi.fn(),
  getUnifiedFiles: vi.fn(),
  refetch: vi.fn(),
  selection: { current: [] as string[] },
  toast: { success: vi.fn(), warning: vi.fn(), error: vi.fn() },
}));

vi.mock('sonner', () => ({ toast: mocks.toast }));
vi.mock('@/services/api', () => ({
  apiClient: {
    deleteModel3dFiles: mocks.deleteModel3dFiles,
    deleteGcodeFiles: mocks.deleteGcodeFiles,
    deleteModel3dFile: mocks.deleteModel3dFile,
    deleteGcodeFile: mocks.deleteGcodeFile,
    getUnifiedFiles: mocks.getUnifiedFiles,
  },
}));
vi.mock('@/features/auth/hooks/useAuth', () => ({ useAuth: () => ({ hasPermission: () => true }) }));
vi.mock('@/common/hooks/useApi', () => ({ usePrinters: () => ({ data: [] }) }));
vi.mock('@/common/hooks/useViewModePreference', () => ({
  useViewModePreference: () => ({ viewMode: 'grid', setViewMode: vi.fn() }),
}));
vi.mock('@/common/components/modals/ModelUploadModal', () => ({ ModelUploadModal: () => null }));
vi.mock('@/common/components/modals/GcodeUploadModal', () => ({ GcodeUploadModal: () => null }));
vi.mock('@/common/components/modals/BulkTagAssignmentModal', () => ({ BulkTagAssignmentModal: () => null }));
vi.mock('@/components/TaggingModal', () => ({ TaggingModal: () => null }));
vi.mock('@/features/projects/components/AddToProjectModal', () => ({ AddToProjectModal: () => null }));
vi.mock('@/common/components/modals/ConfirmationModal', () => ({
  ConfirmationModal: ({ isOpen, title, onConfirm }: { isOpen: boolean; title: string; onConfirm: () => void }) =>
    isOpen ? <button type="button" onClick={onConfirm}>{`Confirm ${title}`}</button> : null,
}));

// Exposes the page's selection state and refetch handle; the page owns everything else.
vi.mock('@/features/fileBrowser/components/FileBrowser', async () => {
  const ReactModule = await import('react');
  return {
    FileBrowser: ReactModule.forwardRef(function FileBrowserMock(
      { config, extraToolbarActions }: { config: UseFileBrowserConfig<unknown>; extraToolbarActions?: React.ReactNode },
      ref: React.Ref<{ refetch: () => Promise<void> }>,
    ) {
      useImperativeHandle(ref, () => ({ refetch: mocks.refetch }));
      return (
        <div>
          <output aria-label="selected">{(config.selectedIds ?? []).join(',')}</output>
          <button type="button" onClick={() => config.onSelectionChange?.(mocks.selection.current)}>Select</button>
          {extraToolbarActions}
        </div>
      );
    }),
  };
});

import { FilesPage } from '../FilesPage';

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/files']}>
        <FilesPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

async function deleteSelection(ids: string[]) {
  mocks.selection.current = ids;
  const user = userEvent.setup();
  renderPage();
  // FilesPage clears the selection one animation frame after mount; select after that.
  await act(() => new Promise<void>((resolve) => { window.requestAnimationFrame(() => resolve()); }));
  await user.click(screen.getByRole('button', { name: 'Select' }));
  await user.click(screen.getByRole('button', { name: new RegExp(`^Delete \\(${ids.length}\\)`) }));
  await user.click(screen.getByRole('button', { name: 'Confirm Delete Selected Files' }));
  await waitFor(() => expect(mocks.refetch).toHaveBeenCalledTimes(1));
}

describe('FilesPage bulk delete', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.getUnifiedFiles.mockResolvedValue({ items: [], totalItems: 0, totalSize: 0, page: 1, pageSize: 50, totalPages: 0 });
    mocks.refetch.mockResolvedValue(undefined);
    mocks.deleteModel3dFiles.mockImplementation(async (ids: string[]) => ({
      deleted: ids.length, errors: 0, total: ids.length, results: ids.map((id) => ({ id, status: 'Deleted' })),
    }));
    mocks.deleteGcodeFiles.mockImplementation(async (ids: string[]) => ({
      deleted: ids.length, totalRequested: ids.length, results: ids.map((id) => ({ id, status: 'Deleted' })),
    }));
  });

  it('sends one G-code bulk request for a G-code-only selection and clears it', async () => {
    await deleteSelection([`gcode:${G1}`, `gcode:${G2}`]);
    expect(mocks.deleteGcodeFiles).toHaveBeenCalledTimes(1);
    expect(mocks.deleteGcodeFiles).toHaveBeenCalledWith([G1, G2]);
    expect(mocks.deleteModel3dFiles).not.toHaveBeenCalled();
    expect(mocks.deleteGcodeFile).not.toHaveBeenCalled();
    expect(mocks.toast.success).toHaveBeenCalledWith('Deleted 2 files');
    expect(screen.getByLabelText('selected')).toHaveTextContent(/^$/);
  });

  it('sends one request per source for a mixed selection and keeps only blocked items selected', async () => {
    mocks.deleteGcodeFiles.mockResolvedValue({
      deleted: 0, totalRequested: 1, results: [{ id: G1, status: 'Blocked', reason: 'ActiveJobs' }],
    });
    await deleteSelection([`model:${M1}`, `gcode:${G1}`]);
    expect(mocks.deleteModel3dFiles).toHaveBeenCalledTimes(1);
    expect(mocks.deleteModel3dFiles).toHaveBeenCalledWith([M1]);
    expect(mocks.deleteGcodeFiles).toHaveBeenCalledTimes(1);
    expect(mocks.deleteModel3dFile).not.toHaveBeenCalled();
    expect(mocks.deleteGcodeFile).not.toHaveBeenCalled();
    expect(mocks.toast.warning).toHaveBeenCalledWith('Deleted 1 file, 1 in use by active jobs');
    expect(screen.getByLabelText('selected')).toHaveTextContent(`gcode:${G1}`);
  });

  it('refreshes and keeps the selection when a source request has an unknown outcome', async () => {
    mocks.deleteModel3dFiles.mockRejectedValue({ statusCode: 500, message: 'Network Error' });
    await deleteSelection([`model:${M1}`]);
    expect(mocks.deleteModel3dFiles).toHaveBeenCalledTimes(1);
    expect(mocks.deleteModel3dFile).not.toHaveBeenCalled();
    expect(mocks.toast.error).toHaveBeenCalledWith(
      'Failed to delete selected files: 1 with an unknown outcome — check the refreshed list',
    );
    expect(screen.getByLabelText('selected')).toHaveTextContent(`model:${M1}`);
  });
});
