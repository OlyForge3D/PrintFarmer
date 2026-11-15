import '@testing-library/jest-dom';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { toast } from 'sonner';
import { AddToProjectModal } from '@/features/projects/components/AddToProjectModal';
import { projectService } from '@/services/projectService';
import type { GcodeFile, PrintProjectFileDto, PrintProjectListDto } from '@/types/api';
import { PrintProjectFileStatus, PrintProjectStatus } from '@/types/api';

vi.mock('@/services/projectService', () => ({
  projectService: { getProjects: vi.fn(), addFilesToProject: vi.fn() },
}));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const project: PrintProjectListDto = {
  id: 'project-1', name: 'Workshop parts', status: PrintProjectStatus.Open, priority: 1,
  totalFiles: 0, completedFiles: 0, totalPrints: 0, completedPrints: 0,
  createdAt: '2026-01-01T00:00:00Z', progressPercent: 0,
};
const added: PrintProjectFileDto = {
  id: 'added-1', gcodeFileId: 'new', fileName: 'part.gcode',
  printCount: 1, printedCount: 0, status: PrintProjectFileStatus.Pending, sortOrder: 0,
  isComplete: false, remainingPrints: 1,
};

function renderModal(props: { files?: GcodeFile[]; fileIds?: string[] }) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
  const onClose = vi.fn();
  render(
    <QueryClientProvider client={queryClient}>
      <AddToProjectModal isOpen onClose={onClose} {...props} />
    </QueryClientProvider>
  );
  return { invalidate, onClose };
}

async function submit() {
  const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: /Workshop parts/ }));
  await user.click(screen.getByRole('button', { name: 'Add to Project' }));
}

describe('AddToProjectModal batch results', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    vi.mocked(projectService.getProjects).mockResolvedValue([project]);
  });

  it.each([0, 1, 2])('reports %i actual additions, not selected IDs, and refreshes both caches', async (count) => {
    const fileIds = ['existing', 'new', 'new', 'missing'];
    vi.mocked(projectService.addFilesToProject).mockResolvedValue(
      Array.from({ length: count }, (_, i) => ({ ...added, id: `added-${i}`, gcodeFileId: `new-${i}` }))
    );
    const { invalidate, onClose } = renderModal({ fileIds });
    await submit();

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith(
      `Added ${count} file${count !== 1 ? 's' : ''} to "Workshop parts"`
    ));
    expect(projectService.addFilesToProject).toHaveBeenCalledExactlyOnceWith('project-1',
      fileIds.map((id) => ({ gcodeFileId: id, printCount: 1, materialRequirement: undefined }))
    );
    expect(invalidate.mock.calls).toEqual([
      [{ queryKey: ['projects'] }], [{ queryKey: ['project', 'project-1'] }],
    ]);
    expect(onClose).toHaveBeenCalledOnce();
    expect(toast.error).not.toHaveBeenCalled();
  });

  it('preserves material metadata when full files are supplied', async () => {
    const files: GcodeFile[] = [{
      id: 'new', path: '/gcode', name: 'part.gcode', fileName: 'part.gcode', fileSize: 100,
      uploadedAt: new Date('2026-01-01T00:00:00Z'), isDirectory: false, requiredMaterial: 'PETG',
    }];
    vi.mocked(projectService.addFilesToProject).mockResolvedValue([added]);
    renderModal({ files });
    await submit();
    await waitFor(() => expect(projectService.addFilesToProject).toHaveBeenCalledExactlyOnceWith(
      'project-1', [{ gcodeFileId: 'new', printCount: 1, materialRequirement: 'PETG' }]
    ));
  });

  it('keeps the selection open and shows the API message on batch failure without success invalidations', async () => {
    vi.mocked(projectService.addFilesToProject).mockRejectedValue({
      message: 'Project no longer exists', statusCode: 404, isAxiosError: true,
    });
    const { invalidate, onClose } = renderModal({ fileIds: ['first', 'second'] });
    await submit();
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('Failed to add files: Project no longer exists'));
    expect(toast.success).not.toHaveBeenCalled();
    expect(invalidate).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Add to Project' })).toBeEnabled();
    expect(projectService.addFilesToProject).toHaveBeenCalledTimes(1);
  });
});
