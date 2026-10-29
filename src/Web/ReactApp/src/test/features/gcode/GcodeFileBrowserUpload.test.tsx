import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { toast } from 'sonner';
import { GcodeFileBrowser } from '@/features/gcode/components/GcodeFileBrowser';
import { apiClient } from '@/services/api';
import { GcodeSource } from '@/types/api';

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    hasPermission: (resource: string, action: string) => resource === 'gcode_library' && action === 'create',
  }),
}));

vi.mock('@/features/fileBrowser/components/FileBrowser', () => ({
  FileBrowser: ({ extraToolbarActions }: { extraToolbarActions?: ReactNode }) => (
    <div>{extraToolbarActions}</div>
  ),
}));

vi.mock('@/services/api', () => ({
  apiClient: { uploadGcodeLibraryFile: vi.fn() },
}));

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

describe('GcodeFileBrowser HTTP upload', () => {
  let queryClient: QueryClient;

  beforeEach(() => {
    vi.clearAllMocks();
    queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  });

  const uploadFile = () => {
    render(
      <QueryClientProvider client={queryClient}>
        <GcodeFileBrowser />
      </QueryClientProvider>,
    );
    fireEvent.click(screen.getByTitle('Upload G-code files'));
    const input = document.getElementById('gcode-file-upload');
    if (!input) throw new Error('G-code file input not found');
    const file = new File(['G28\n'], 'part.gcode', { type: 'text/plain' });
    fireEvent.change(input, { target: { files: [file] } });
    fireEvent.click(screen.getByRole('button', { name: 'Upload 1 File' }));
    return file;
  };

  it('completes uploads and refreshes the library without a harvest connection', async () => {
    vi.mocked(apiClient.uploadGcodeLibraryFile).mockResolvedValue({
      id: 'uploaded-file',
      fileName: 'part.gcode',
      fileSize: 4,
      uploadedAt: new Date('2026-10-25T00:00:00Z'),
      source: GcodeSource.Upload,
    });
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    const file = uploadFile();

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Uploaded 1 file'));
    expect(apiClient.uploadGcodeLibraryFile).toHaveBeenCalledWith(file, '/', expect.any(Function));
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['file-browser'] });
    expect(screen.queryByRole('dialog', { name: 'Upload G-Code Files' })).not.toBeInTheDocument();
  });

  it('reports HTTP failures without claiming success or refreshing the library', async () => {
    vi.mocked(apiClient.uploadGcodeLibraryFile).mockRejectedValue(new Error('Upload unavailable'));
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    uploadFile();

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('Failed to upload 1 file'));
    expect(toast.success).not.toHaveBeenCalled();
    expect(invalidate).not.toHaveBeenCalled();
  });
});
