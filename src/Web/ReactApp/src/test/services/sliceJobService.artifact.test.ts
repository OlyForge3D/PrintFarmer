import { describe, it, expect, vi, beforeEach } from 'vitest';
import { SliceJobService } from '@/services/sliceJobService';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';

const mockRequest = vi.fn();

vi.mock('@/services/api', () => ({
  apiClient: {
    request: (...args: unknown[]) => mockRequest(...args),
  },
}));

describe('SliceJobService artifact URL helpers', () => {
  let service: SliceJobService;

  beforeEach(() => {
    service = new SliceJobService();
    mockRequest.mockReset();
  });

  describe('getArtifactDownloadUrl', () => {
    it('builds correct download URL from artifact ID', () => {
      const url = service.getArtifactDownloadUrl('abc-123');
      expect(url).toBe(`${getApiBaseUrl()}/artifacts/abc-123`);
    });

    it('handles UUIDs', () => {
      const url = service.getArtifactDownloadUrl('f47ac10b-58cc-4372-a567-0e02b2c3d479');
      expect(url).toBe(`${getApiBaseUrl()}/artifacts/f47ac10b-58cc-4372-a567-0e02b2c3d479`);
    });
  });

  describe('getArtifactMetadata', () => {
    it('calls GET /artifacts/{id}/metadata', async () => {
      const mockMetadata = {
        id: 'art-1',
        sliceJobId: 'job-1',
        fileName: 'model.gcode',
        contentType: 'application/octet-stream',
        sizeBytes: 12345,
        downloadUrl: '/api/artifacts/art-1/download',
        createdAt: '2026-05-31T10:00:00Z',
      };
      mockRequest.mockResolvedValue(mockMetadata);

      const result = await service.getArtifactMetadata('art-1');

      expect(mockRequest).toHaveBeenCalledWith({
        url: '/artifacts/art-1/metadata',
        method: 'GET',
      });
      expect(result).toEqual(mockMetadata);
    });
  });

  describe('getArtifactGcodeUrl', () => {
    it('returns downloadUrl from metadata when available', async () => {
      mockRequest.mockResolvedValue({
        id: 'art-1',
        sliceJobId: 'job-1',
        fileName: 'model.gcode',
        contentType: 'application/octet-stream',
        sizeBytes: 12345,
        downloadUrl: 'https://cdn.example.com/artifacts/art-1.gcode',
        createdAt: '2026-05-31T10:00:00Z',
      });

      const url = await service.getArtifactGcodeUrl('art-1');
      expect(url).toBe('https://cdn.example.com/artifacts/art-1.gcode');
    });

    it('falls back to constructed URL when downloadUrl is empty', async () => {
      mockRequest.mockResolvedValue({
        id: 'art-2',
        sliceJobId: 'job-2',
        fileName: 'model.gcode',
        contentType: 'application/octet-stream',
        sizeBytes: 5000,
        downloadUrl: '',
        createdAt: '2026-05-31T10:00:00Z',
      });

      const url = await service.getArtifactGcodeUrl('art-2');
      expect(url).toBe(`${getApiBaseUrl()}/artifacts/art-2`);
    });
  });

  describe('getArtifactsByJob', () => {
    it('calls GET /artifacts/job/{jobId} and returns artifact list', async () => {
      const mockList = [
        {
          id: 'art-1',
          jobId: 'job-1',
          fileName: 'model.gcode',
          contentType: 'application/octet-stream',
          sizeBytes: 12345,
          downloadUrl: '/api/artifacts/art-1',
          createdAt: '2026-05-31T10:00:00Z',
          isPrimary: true,
        },
      ];
      mockRequest.mockResolvedValue(mockList);

      const result = await service.getArtifactsByJob('job-1');

      expect(mockRequest).toHaveBeenCalledWith({
        url: '/artifacts/job/job-1',
        method: 'GET',
      });
      expect(result).toEqual(mockList);
    });

    it('returns empty array when job has no artifacts', async () => {
      mockRequest.mockResolvedValue([]);
      const result = await service.getArtifactsByJob('job-empty');
      expect(result).toEqual([]);
    });

    describe('getArtifactsByRoute', () => {
      it('normalizes the canonical /api route through the authenticated API client', async () => {
        mockRequest.mockResolvedValue([]);
        const jobId = 'f47ac10b-58cc-4372-a567-0e02b2c3d479';

        await service.getArtifactsByRoute(`/api/artifacts/job/${jobId}`);

        expect(mockRequest).toHaveBeenCalledWith({
          url: `/artifacts/job/${jobId}`,
          method: 'GET',
        });
      });

      it('accepts the canonical configured API base form', async () => {
        mockRequest.mockResolvedValue([]);
        const jobId = 'f47ac10b-58cc-4372-a567-0e02b2c3d479';

        await service.getArtifactsByRoute(
          `${getApiBaseUrl()}/artifacts/job/${jobId}`,
        );

        expect(mockRequest).toHaveBeenCalledWith({
          url: `/artifacts/job/${jobId}`,
          method: 'GET',
        });
      });

      it.each([
        'https://evil.example/api/artifacts/job/f47ac10b-58cc-4372-a567-0e02b2c3d479',
        '//evil.example/api/artifacts/job/f47ac10b-58cc-4372-a567-0e02b2c3d479',
        '/api/artifacts/job/f47ac10b-58cc-4372-a567-0e02b2c3d479/../../secrets',
        '/api/artifacts/job/not-a-guid',
      ])('rejects unsafe artifact route %s before the authenticated request', async (route) => {
        await expect(service.getArtifactsByRoute(route)).rejects.toThrow(
          'Invalid slice artifacts route.',
        );
        expect(mockRequest).not.toHaveBeenCalled();
      });
    });

    describe('downloadArtifact', () => {
      it('requests the selected artifact as a blob through the authenticated API client', async () => {
        const blob = new Blob(['G1 X0 Y0']);
        mockRequest.mockResolvedValue(blob);

        await expect(service.downloadArtifact('art-1')).resolves.toBe(blob);
        expect(mockRequest).toHaveBeenCalledWith({
          url: '/artifacts/art-1',
          method: 'GET',
          responseType: 'blob',
        });
      });
    });

    describe('promoteSliceArtifact', () => {
      it('calls the explicit main-API slice-artifact promotion contract', async () => {
        const response = {
          gcodeFileId: 'file-1',
          name: 'benchy.gcode',
          sizeBytes: 123,
          createdNew: true,
          printable: true,
          sliceJobId: 'job-1',
          sourceArtifactId: 'artifact-1',
        };
        mockRequest.mockResolvedValue(response);

        await expect(
          service.promoteSliceArtifact('job-1', 'artifact-1'),
        ).resolves.toEqual(response);
        expect(mockRequest).toHaveBeenCalledWith({
          url: '/gcode-promotions/slice-artifact',
          method: 'POST',
          data: { sliceJobId: 'job-1', artifactId: 'artifact-1' },
        });
      });

      describe('print contracts', () => {
        it('includes the selected artifact ID in the direct-print request', async () => {
          mockRequest.mockResolvedValue({});

          await service.sendToPrinter(
            'job-1',
            'artifact-selected',
            'printer-1',
            false,
          );

          expect(mockRequest).toHaveBeenCalledWith({
            url: '/slice/job-1/send-to-printer',
            method: 'POST',
            data: {
              artifactId: 'artifact-selected',
              printerId: 'printer-1',
              startPrint: false,
            },
          });
        });

        it('includes the selected artifact ID in the queue request', async () => {
          mockRequest.mockResolvedValue({});

          await service.addSliceToQueue('job-1', {
            artifactId: 'artifact-selected',
            priority: 'Normal',
          });

          expect(mockRequest).toHaveBeenCalledWith({
            url: '/slice/job-1/add-to-queue',
            method: 'POST',
            data: {
              artifactId: 'artifact-selected',
              priority: 'Normal',
            },
          });
        });
      });
    });
  });
});
