import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { GcodePreviewModal } from '@/features/slicer/components/GcodePreviewModal';

const mockGetArtifactsByJob = vi.fn();
const mockGetArtifactDownloadUrl = vi.fn((id: string) => `/api/artifacts/${id}`);

vi.mock('@/services/sliceJobService', () => ({
  sliceJobService: {
    getArtifactsByJob: (...args: unknown[]) => mockGetArtifactsByJob(...args),
    getArtifactDownloadUrl: (id: string) => mockGetArtifactDownloadUrl(id),
  },
}));

// Capture the gcodeUrl passed to the viewer so we can assert on it
const capturedUrls: string[] = [];

vi.mock('@/features/models3d/components/3d/GCodeViewer3D', () => ({
  GCodeViewer: ({ gcodeUrl }: { gcodeUrl: string }) => {
    capturedUrls.push(gcodeUrl);
    return <div data-testid="gcode-viewer" data-url={gcodeUrl} />;
  },
}));

function renderModal(props: { isOpen: boolean; jobId: string; onClose?: () => void }) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <GcodePreviewModal
        isOpen={props.isOpen}
        jobId={props.jobId}
        onClose={props.onClose ?? vi.fn()}
      />
    </QueryClientProvider>,
  );
}

describe('GcodePreviewModal URL contract', () => {
  beforeEach(() => {
    mockGetArtifactsByJob.mockReset();
    capturedUrls.length = 0;
  });

  it('fetches artifact list and passes the PhysicalFile URL (not the list endpoint) to the viewer', async () => {
    mockGetArtifactsByJob.mockResolvedValue([
      {
        id: 'art-abc',
        jobId: 'job-1',
        fileName: 'model.gcode',
        contentType: 'application/octet-stream',
        sizeBytes: 9000,
        downloadUrl: '/api/artifacts/art-abc',
        createdAt: '2026-05-31T10:00:00Z',
      },
    ]);

    renderModal({ isOpen: true, jobId: 'job-1' });

    // Resolves artifact list for the job
    await waitFor(() => {
      expect(mockGetArtifactsByJob).toHaveBeenCalledWith('job-1');
    });

    // Viewer receives the file-serving URL, NOT the list endpoint
    const viewer = await screen.findByTestId('gcode-viewer');
    const url = viewer.getAttribute('data-url') ?? '';

    expect(url).toBe('/api/artifacts/art-abc');
    expect(url).not.toBe('/api/artifacts/job/job-1');
    expect(url).not.toContain('/download');
  });

  it('prefers a .gcode file when multiple artifacts are present', async () => {
    mockGetArtifactsByJob.mockResolvedValue([
      {
        id: 'art-log',
        jobId: 'job-2',
        fileName: 'slicer.log',
        contentType: 'text/plain',
        sizeBytes: 1000,
        downloadUrl: '/api/artifacts/art-log',
        createdAt: '2026-05-31T10:00:00Z',
      },
      {
        id: 'art-gcode',
        jobId: 'job-2',
        fileName: 'print.gcode',
        contentType: 'application/octet-stream',
        sizeBytes: 50000,
        downloadUrl: '/api/artifacts/art-gcode',
        createdAt: '2026-05-31T10:01:00Z',
      },
    ]);

    renderModal({ isOpen: true, jobId: 'job-2' });

    const viewer = await screen.findByTestId('gcode-viewer');
    expect(viewer.getAttribute('data-url')).toBe('/api/artifacts/art-gcode');
  });

  it('does not fetch when modal is closed', () => {
    renderModal({ isOpen: false, jobId: 'job-3' });
    expect(mockGetArtifactsByJob).not.toHaveBeenCalled();
  });
});
