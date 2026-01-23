import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';

const base = `${getApiBaseUrl()}/job-queue`;

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

async function handle<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const text = await res.text();
    throw new Error(text || `Request failed (${res.status})`);
  }
  return res.json() as Promise<T>;
}

export const printJobQueueService = {
  async enqueue(req: EnqueuePrintJobRequest): Promise<PrintJobDto> {
    const res = await fetch(base, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${localStorage.getItem('auth-token')}`
      },
      body: JSON.stringify(req)
    });
    return handle<PrintJobDto>(res);
  }
};

export default printJobQueueService;
