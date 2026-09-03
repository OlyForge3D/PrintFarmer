import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactElement, ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SliceProgressOverlay } from '@/features/slicer/components/SliceProgressOverlay';
import type { SliceJobProgressState } from '@/features/slicer/hooks/useSliceJobProgress';

const downloadGcodeArtifact = vi.fn();
const saveGcodeArtifactToLibrary = vi.fn();
const resolveGcodeArtifactForAction = vi.fn();

vi.mock('@/features/slicer/utils/sliceArtifactActions', () => ({
  downloadGcodeArtifact: (...args: unknown[]) => downloadGcodeArtifact(...args),
  saveGcodeArtifactToLibrary: (...args: unknown[]) => saveGcodeArtifactToLibrary(...args),
  resolveGcodeArtifactForAction: (...args: unknown[]) => resolveGcodeArtifactForAction(...args),
}));

vi.mock('@/features/slicer/components/GcodePreviewModal', () => ({
  GcodePreviewModal: ({
    isOpen,
    onClose,
    artifactsRoute,
  }: {
    isOpen: boolean;
    onClose: () => void;
    artifactsRoute: string;
  }) => isOpen ? (
    <div>
      <span>Preview modal for {artifactsRoute}</span>
      <button type="button" onClick={onClose}>Close preview</button>
    </div>
  ) : null,
}));

vi.mock('@/features/slicer/components/SendToPrinterModal', () => ({
  SendToPrinterModal: ({
    isOpen,
    jobId,
    artifactId,
  }: {
    isOpen: boolean;
    jobId: string;
    artifactId: string;
  }) => isOpen
    ? <div data-testid="print-modal">{jobId}:{artifactId}</div>
    : null,
}));

const processingProgress: SliceJobProgressState = {
  progressPercent: 80,
  progressMessage: 'Slicing',
  status: 'Processing',
  estimatedPrintTimeSeconds: null,
  filamentUsedGrams: null,
  artifactsRoute: null,
  error: null,
  isConnected: true,
};

function renderOverlay(ui: ReactElement) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  return { queryClient, ...render(ui, { wrapper: Wrapper }) };
}

describe('SliceProgressOverlay completion actions', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    downloadGcodeArtifact.mockResolvedValue(undefined);
    resolveGcodeArtifactForAction.mockReset();
  });

  it('reveals persistent actions from a live completion and auto-opens preview only once', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    const windowOpen = vi.spyOn(window, 'open');

    try {
      const { rerender } = renderOverlay(
        <SliceProgressOverlay
          jobId="job-1"
          progress={processingProgress}
          onNewJob={vi.fn()}
          onRetry={vi.fn()}
        />,
      );

      const completedProgress: SliceJobProgressState = {
        ...processingProgress,
        status: 'Completed',
        progressPercent: 100,
        artifactsRoute: '/api/artifacts/job/job-1',
      };
      rerender(
        <SliceProgressOverlay
          jobId="job-1"
          progress={completedProgress}
          onNewJob={vi.fn()}
          onRetry={vi.fn()}
        />,
      );

      expect(await screen.findByText(/Preview modal for/)).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Preview' })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Save to Library' })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Print' })).toBeInTheDocument();
      expect(saveGcodeArtifactToLibrary).not.toHaveBeenCalled();
      const downloadButton = screen.getByRole('button', { name: 'Download G-code' });

      await user.click(downloadButton);
      expect(downloadGcodeArtifact).toHaveBeenCalledWith('/api/artifacts/job/job-1');
      expect(windowOpen).not.toHaveBeenCalled();

      await user.click(screen.getByRole('button', { name: 'Close preview' }));
      rerender(
        <SliceProgressOverlay
          jobId="job-1"
          progress={{ ...completedProgress }}
          onNewJob={vi.fn()}
          onRetry={vi.fn()}
        />,
      );
      expect(screen.queryByText(/Preview modal for/)).not.toBeInTheDocument();

      await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
      expect(screen.getByRole('button', { name: 'Preview' })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Download G-code' })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Save to Library' })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Print' })).toBeInTheDocument();
    } finally {
      windowOpen.mockRestore();
      vi.useRealTimers();
    }
  });

  it('saves explicitly with single-flight protection and invalidates a newly created file', async () => {
    let resolveSave!: (value: { createdNew: boolean; gcodeFileId: string }) => void;
    saveGcodeArtifactToLibrary.mockImplementation(
      () => new Promise(resolve => {
        resolveSave = resolve;
      }),
    );

    const completedProgress: SliceJobProgressState = {
      ...processingProgress,
      status: 'Completed',
      progressPercent: 100,
      artifactsRoute: '/api/artifacts/job/job-1',
    };
    const { queryClient } = renderOverlay(
      <SliceProgressOverlay
        jobId="job-1"
        progress={completedProgress}
        onNewJob={vi.fn()}
        onRetry={vi.fn()}
      />,
    );
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    const saveButton = screen.getByRole('button', { name: 'Save to Library' });

    fireEvent.click(saveButton);
    fireEvent.click(saveButton);

    expect(saveGcodeArtifactToLibrary).toHaveBeenCalledOnce();
    expect(saveGcodeArtifactToLibrary).toHaveBeenCalledWith(
      '/api/artifacts/job/job-1',
      'job-1',
    );

    await act(async () => {
      resolveSave({ createdNew: true, gcodeFileId: 'file-1' });
    });

    expect(invalidate).toHaveBeenCalledOnce();
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['file-browser'] });
    expect(await screen.findByRole('status')).toHaveTextContent('Saved to Library');
  });

  it('surfaces save errors and allows retry without invalidating an existing file', async () => {
    saveGcodeArtifactToLibrary
      .mockRejectedValueOnce(new Error('Promotion failed'))
      .mockResolvedValueOnce({ createdNew: false, gcodeFileId: 'file-1' });

    const completedProgress: SliceJobProgressState = {
      ...processingProgress,
      status: 'Completed',
      progressPercent: 100,
      artifactsRoute: '/api/artifacts/job/job-1',
    };
    const { queryClient } = renderOverlay(
      <SliceProgressOverlay
        jobId="job-1"
        progress={completedProgress}
        onNewJob={vi.fn()}
        onRetry={vi.fn()}
      />,
    );
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');

    fireEvent.click(screen.getByRole('button', { name: 'Save to Library' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Promotion failed');

    fireEvent.click(screen.getByRole('button', { name: 'Save to Library' }));
    await waitFor(() => expect(saveGcodeArtifactToLibrary).toHaveBeenCalledTimes(2));
    expect(await screen.findByRole('status')).toHaveTextContent('Already in File Library');
    expect(invalidate).not.toHaveBeenCalled();
  });

  it('opens Print with the exact selected artifact when multiple artifacts exist', async () => {
    const artifacts = [
      { id: 'newest-artifact', fileName: 'newest.gcode', isPrimary: false },
      { id: 'primary-artifact', fileName: 'primary.gcode', isPrimary: true },
    ];
    resolveGcodeArtifactForAction.mockResolvedValue(artifacts[1]);
    const completedProgress: SliceJobProgressState = {
      ...processingProgress,
      status: 'Completed',
      progressPercent: 100,
      artifactsRoute: '/api/artifacts/job/job-1',
    };
    renderOverlay(
      <SliceProgressOverlay
        jobId="job-1"
        progress={completedProgress}
        onNewJob={vi.fn()}
        onRetry={vi.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Print' }));

    expect(await screen.findByTestId('print-modal')).toHaveTextContent(
      'job-1:primary-artifact',
    );
    expect(resolveGcodeArtifactForAction).toHaveBeenCalledWith(
      '/api/artifacts/job/job-1',
    );
    expect(screen.getByTestId('print-modal')).not.toHaveTextContent('newest-artifact');
  });
});
