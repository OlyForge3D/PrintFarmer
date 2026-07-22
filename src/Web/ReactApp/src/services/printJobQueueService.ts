import { apiClient } from './api';

export interface EnqueuePrintJobRequest {
  gcodeFileId: string; // UUID string
  assignedPrinterId?: string; // UUID string
  priority?: 'Low' | 'Normal' | 'High' | 'Urgent';
  requiredNozzleDiameter?: number;
  requiredMaterialType?: string;
  /** Required printer model for auto-assign filtering (e.g., "QIDI X-Plus 4", "COREONEL") */
  requiredPrinterModel?: string;
  /** Spoolman filament ID */
  spoolmanFilamentId?: number;
  /** Filament name from Spoolman */
  filamentName?: string;
  /** Filament vendor from Spoolman */
  filamentVendor?: string;
  /** Filament color hex from Spoolman */
  filamentColor?: string;
  /** Optional plate index for multi-plate 3MF models */
  plateIndex?: number;
  /** Optional plate name for multi-plate 3MF models */
  plateName?: string;
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
