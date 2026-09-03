import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { GcodePreviewModal } from '@/features/slicer/components/GcodePreviewModal';

const mockGetArtifactsByRoute = vi.fn();
const mockGetArtifactDownloadUrl = vi.fn((id: string) => `/api/artifacts/${id}`);

vi.mock('@/services/sliceJobService', () => ({
  sliceJobService: {
    getArtifactsByRoute: (...args: unknown[]) => mockGetArtifactsByRoute(...args),
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

function renderModal(props: { isOpen: boolean; artifactsRoute: string; onClose?: () => void }) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <GcodePreviewModal
        isOpen={props.isOpen}
        artifactsRoute={props.artifactsRoute}
        onClose={props.onClose ?? vi.fn()}
      />
    </QueryClientProvider>,
  );
}

describe('GcodePreviewModal URL contract', () => {
  beforeEach(() => {
    mockGetArtifactsByRoute.mockReset();
    capturedUrls.length = 0;
  });

  it('fetches artifact list and passes the PhysicalFile URL (not the list endpoint) to the viewer', async () => {
    mockGetArtifactsByRoute.mockResolvedValue([
      {
        id: 'art-abc',
        jobId: 'job-1',
        fileName: 'model.gcode',
        contentType: 'application/octet-stream',
        sizeBytes: 9000,
        downloadUrl: '/api/artifacts/art-abc',
        createdAt: '2026-05-31T10:00:00Z',
        isPrimary: false,
      },
    ]);

    renderModal({ isOpen: true, artifactsRoute: '/api/artifacts/job/job-1' });

    // Resolves artifact list for the job
    await waitFor(() => {
      expect(mockGetArtifactsByRoute).toHaveBeenCalledWith('/api/artifacts/job/job-1');
    });

    // Viewer receives the file-serving URL, NOT the list endpoint
    const viewer = await screen.findByTestId('gcode-viewer');
    const url = viewer.getAttribute('data-url') ?? '';

    expect(url).toBe('/api/artifacts/art-abc');
    expect(url).not.toBe('/api/artifacts/job/job-1');
    expect(url).not.toContain('/download');
  });

  it('prefers a .gcode file when multiple artifacts are present', async () => {
    mockGetArtifactsByRoute.mockResolvedValue([
      {
        id: 'art-log',
        jobId: 'job-2',
        fileName: 'slicer.log',
        contentType: 'text/plain',
        sizeBytes: 1000,
        downloadUrl: '/api/artifacts/art-log',
        createdAt: '2026-05-31T10:00:00Z',
        isPrimary: false,
      },
      {
        id: 'art-gcode',
        jobId: 'job-2',
        fileName: 'print.gcode',
        contentType: 'application/octet-stream',
        sizeBytes: 50000,
        downloadUrl: '/api/artifacts/art-gcode',
        createdAt: '2026-05-31T10:01:00Z',
        isPrimary: false,
      },
    ]);

    renderModal({ isOpen: true, artifactsRoute: '/api/artifacts/job/job-2' });

    const viewer = await screen.findByTestId('gcode-viewer');
    expect(viewer.getAttribute('data-url')).toBe('/api/artifacts/art-gcode');
  });

  it('does not feed binary G-code into the text preview parser', async () => {
    mockGetArtifactsByRoute.mockResolvedValue([
      {
        id: 'art-stl',
        jobId: 'job-4',
        fileName: 'model.stl',
        contentType: 'model/stl',
        sizeBytes: 20000,
        downloadUrl: '/api/artifacts/art-stl',
        createdAt: '2026-05-31T10:00:00Z',
        isPrimary: false,
      },
      {
        id: 'art-bgcode',
        jobId: 'job-4',
        fileName: 'output.bgcode',
        contentType: 'application/octet-stream',
        sizeBytes: 40000,
        downloadUrl: '/api/artifacts/art-bgcode',
        createdAt: '2026-05-31T10:01:00Z',
        isPrimary: false,
      },
    ]);

    renderModal({ isOpen: true, artifactsRoute: '/api/artifacts/job/job-4' });

    await waitFor(() => {
      expect(screen.getByText(/no g-code artifact available/i)).toBeDefined();
    });
    expect(screen.queryByTestId('gcode-viewer')).toBeNull();
  });

  it('previews the declared text primary instead of the newest non-primary output', async () => {
    mockGetArtifactsByRoute.mockResolvedValue([
      {
        id: 'art-newest',
        jobId: 'job-6',
        fileName: 'newest.gcode',
        contentType: 'application/octet-stream',
        sizeBytes: 40000,
        downloadUrl: '/api/artifacts/art-newest',
        createdAt: '2026-05-31T10:02:00Z',
        isPrimary: false,
      },
      {
        id: 'art-primary',
        jobId: 'job-6',
        fileName: 'primary.gcode',
        contentType: 'text/plain',
        sizeBytes: 50000,
        downloadUrl: '/api/artifacts/art-primary',
        createdAt: '2026-05-31T10:01:00Z',
        isPrimary: true,
      },
    ]);

    renderModal({ isOpen: true, artifactsRoute: '/api/artifacts/job/job-6' });

    const viewer = await screen.findByTestId('gcode-viewer');
    expect(viewer.getAttribute('data-url')).toBe('/api/artifacts/art-primary');
  });

  it('surfaces selection-required state when multiple outputs have no primary', async () => {
    mockGetArtifactsByRoute.mockResolvedValue([
      {
        id: 'art-1',
        jobId: 'job-7',
        fileName: 'first.gcode',
        contentType: 'text/plain',
        sizeBytes: 40000,
        downloadUrl: '/api/artifacts/art-1',
        createdAt: '2026-05-31T10:01:00Z',
        isPrimary: false,
      },
      {
        id: 'art-2',
        jobId: 'job-7',
        fileName: 'second.gcode',
        contentType: 'text/plain',
        sizeBytes: 50000,
        downloadUrl: '/api/artifacts/art-2',
        createdAt: '2026-05-31T10:02:00Z',
        isPrimary: false,
      },
    ]);

    renderModal({ isOpen: true, artifactsRoute: '/api/artifacts/job/job-7' });

    expect(
      await screen.findByText(/did not declare exactly one valid primary artifact/i),
    ).toBeInTheDocument();
    expect(screen.queryByTestId('gcode-viewer')).not.toBeInTheDocument();
  });

  it('does not render viewer when only non-G-code artifacts exist', async () => {
    mockGetArtifactsByRoute.mockResolvedValue([
      {
        id: 'art-img',
        jobId: 'job-5',
        fileName: 'thumbnail.png',
        contentType: 'image/png',
        sizeBytes: 3000,
        downloadUrl: '/api/artifacts/art-img',
        createdAt: '2026-05-31T10:00:00Z',
        isPrimary: false,
      },
      {
        id: 'art-3mf',
        jobId: 'job-5',
        fileName: 'project.3mf',
        contentType: 'application/octet-stream',
        sizeBytes: 15000,
        downloadUrl: '/api/artifacts/art-3mf',
        createdAt: '2026-05-31T10:01:00Z',
        isPrimary: false,
      },
    ]);

    renderModal({ isOpen: true, artifactsRoute: '/api/artifacts/job/job-5' });

    // Wait for loading to finish and message to appear
    await waitFor(() => {
      expect(screen.getByText(/no g-code artifact is available/i)).toBeDefined();
    });

    // Viewer should NOT be rendered
    expect(screen.queryByTestId('gcode-viewer')).toBeNull();
  });

  it('does not fetch when modal is closed', () => {
    renderModal({ isOpen: false, artifactsRoute: '/api/artifacts/job/job-3' });
    expect(mockGetArtifactsByRoute).not.toHaveBeenCalled();
  });
});
