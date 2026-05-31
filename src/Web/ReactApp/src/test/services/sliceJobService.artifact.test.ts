import { describe, it, expect, vi, beforeEach } from 'vitest';
import { SliceJobService } from '@/services/sliceJobService';

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
      expect(url).toBe('/api/artifacts/abc-123');
    });

    it('handles UUIDs', () => {
      const url = service.getArtifactDownloadUrl('f47ac10b-58cc-4372-a567-0e02b2c3d479');
      expect(url).toBe('/api/artifacts/f47ac10b-58cc-4372-a567-0e02b2c3d479');
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
      expect(url).toBe('/api/artifacts/art-2');
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
  });
});
