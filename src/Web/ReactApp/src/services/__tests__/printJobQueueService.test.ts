import { describe, it, expect, vi, beforeEach } from 'vitest';
import { printJobQueueService, EnqueuePrintJobRequest } from '../printJobQueueService';
import { apiClient } from '../api';

vi.mock('../api', () => ({
  apiClient: {
    post: vi.fn(),
  },
}));

describe('printJobQueueService', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('enqueue', () => {
    it('should enqueue a print job with minimal requirements', async () => {
      const request: EnqueuePrintJobRequest = {
        gcodeFileId: 'file-123',
      };

      const mockResponse = {
        id: 'job-1',
        gcodeFileId: 'file-123',
        gcodeFileName: 'test-model.gcode',
        status: 'Queued',
        queuePosition: 1,
        createdAt: '2024-01-01T00:00:00Z',
      };

      vi.mocked(apiClient.post).mockResolvedValue({ data: mockResponse });

      const result = await printJobQueueService.enqueue(request);

      expect(apiClient.post).toHaveBeenCalledWith('/job-queue', request);
      expect(result).toEqual(mockResponse);
    });

    it('should enqueue a print job with assigned printer', async () => {
      const request: EnqueuePrintJobRequest = {
        gcodeFileId: 'file-456',
        assignedPrinterId: 'printer-789',
      };

      const mockResponse = {
        id: 'job-2',
        gcodeFileId: 'file-456',
        gcodeFileName: 'model.gcode',
        assignedPrinterId: 'printer-789',
        assignedPrinterName: 'Printer 1',
        status: 'Queued',
        queuePosition: 2,
        createdAt: '2024-01-02T00:00:00Z',
      };

      vi.mocked(apiClient.post).mockResolvedValue({ data: mockResponse });

      const result = await printJobQueueService.enqueue(request);

      expect(result.assignedPrinterId).toBe('printer-789');
      expect(result.assignedPrinterName).toBe('Printer 1');
    });

    it('should enqueue a print job with priority', async () => {
      const request: EnqueuePrintJobRequest = {
        gcodeFileId: 'file-urgent',
        priority: 'Urgent',
      };

      const mockResponse = {
        id: 'job-urgent',
        gcodeFileId: 'file-urgent',
        gcodeFileName: 'urgent-model.gcode',
        status: 'Queued',
        queuePosition: 0, // High priority goes first
        createdAt: '2024-01-03T00:00:00Z',
      };

      vi.mocked(apiClient.post).mockResolvedValue({ data: mockResponse });

      const result = await printJobQueueService.enqueue(request);

      expect(result.queuePosition).toBe(0);
    });

    it('should enqueue a print job with material requirements', async () => {
      const request: EnqueuePrintJobRequest = {
        gcodeFileId: 'file-material',
        requiredNozzleDiameter: 0.4,
        requiredMaterialType: 'PLA',
      };

      const mockResponse = {
        id: 'job-material',
        gcodeFileId: 'file-material',
        gcodeFileName: 'pla-model.gcode',
        status: 'Queued',
        queuePosition: 3,
        requiredNozzleDiameter: 0.4,
        requiredMaterialType: 'PLA',
        createdAt: '2024-01-04T00:00:00Z',
      };

      vi.mocked(apiClient.post).mockResolvedValue({ data: mockResponse });

      const result = await printJobQueueService.enqueue(request);

      expect(result.requiredNozzleDiameter).toBe(0.4);
      expect(result.requiredMaterialType).toBe('PLA');
    });

    it('should enqueue a print job with printer model requirement', async () => {
      const request: EnqueuePrintJobRequest = {
        gcodeFileId: 'file-model-specific',
        requiredPrinterModel: 'QIDI X-Plus 4',
      };

      const mockResponse = {
        id: 'job-model-specific',
        gcodeFileId: 'file-model-specific',
        gcodeFileName: 'qidi-model.gcode',
        status: 'Queued',
        queuePosition: 4,
        createdAt: '2024-01-05T00:00:00Z',
      };

      vi.mocked(apiClient.post).mockResolvedValue({ data: mockResponse });

      const result = await printJobQueueService.enqueue(request);

      expect(result).toBeDefined();
      expect(apiClient.post).toHaveBeenCalledWith('/job-queue', request);
    });

    it('should enqueue a print job with all optional parameters', async () => {
      const request: EnqueuePrintJobRequest = {
        gcodeFileId: 'file-complete',
        assignedPrinterId: 'printer-complete',
        priority: 'High',
        requiredNozzleDiameter: 0.6,
        requiredMaterialType: 'PETG',
        requiredPrinterModel: 'COREONEL',
      };

      const mockResponse = {
        id: 'job-complete',
        gcodeFileId: 'file-complete',
        gcodeFileName: 'complete-model.gcode',
        assignedPrinterId: 'printer-complete',
        assignedPrinterName: 'Complete Printer',
        status: 'Queued',
        queuePosition: 0,
        requiredNozzleDiameter: 0.6,
        requiredMaterialType: 'PETG',
        createdAt: '2024-01-06T00:00:00Z',
      };

      vi.mocked(apiClient.post).mockResolvedValue({ data: mockResponse });

      const result = await printJobQueueService.enqueue(request);

      expect(result.assignedPrinterId).toBe('printer-complete');
      expect(result.requiredNozzleDiameter).toBe(0.6);
      expect(result.requiredMaterialType).toBe('PETG');
    });

    it('should handle different priority levels', async () => {
      const priorities: Array<'Low' | 'Normal' | 'High' | 'Urgent'> = ['Low', 'Normal', 'High', 'Urgent'];

      for (const priority of priorities) {
        const request: EnqueuePrintJobRequest = {
          gcodeFileId: `file-${priority.toLowerCase()}`,
          priority,
        };

        const mockResponse = {
          id: `job-${priority.toLowerCase()}`,
          gcodeFileId: `file-${priority.toLowerCase()}`,
          gcodeFileName: `${priority.toLowerCase()}-model.gcode`,
          status: 'Queued',
          queuePosition: 1,
          createdAt: '2024-01-07T00:00:00Z',
        };

        vi.mocked(apiClient.post).mockResolvedValue({ data: mockResponse });

        const result = await printJobQueueService.enqueue(request);

        expect(result).toBeDefined();
        expect(apiClient.post).toHaveBeenCalledWith('/job-queue', request);
      }
    });
  });
});
