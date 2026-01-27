import { apiClient } from './api';

export interface EnqueuePrintJobRequest {
  gcodeFileId: string; // UUID string
  assignedPrinterId?: string; // UUID string
  priority?: 'Low' | 'Normal' | 'High' | 'Urgent';
  requiredNozzleDiameter?: number;
  requiredMaterialType?: string;
}

export interface PrintJobDto {
  id: string;
  gcodeFileId: string;
  gcodeFileName: string;
  assignedPrinterId?: string;
  assignedPrinterName?: string;
  status: string;
  queuePosition: number;
  requiredNozzleDiameter?: number;
  requiredMaterialType?: string;
  createdAt: string;
}

export const printJobQueueService = {
  async enqueue(req: EnqueuePrintJobRequest): Promise<PrintJobDto> {
    const response = await apiClient.post<PrintJobDto>('/job-queue', req);
    return response.data;
  }
};
