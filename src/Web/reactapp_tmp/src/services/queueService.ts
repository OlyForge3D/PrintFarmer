// Queue service interfaces and types
export interface QueueOverview {
  printerId: string;
  printerName: string;
  printerModel: string;
  isAvailable: boolean;
  queuedJobsCount: number;
  currentJobId?: string;
  currentJobName?: string;
  estimatedCompletionTime?: string;
}

export interface PrintJob {
  id: string;
  gcodeFileId: string;
  gcodeFileName: string;
  assignedPrinterId?: string;
  assignedPrinterName?: string;
  status: 'Queued' | 'Assigned' | 'Starting' | 'Printing' | 'Paused' | 'Completed' | 'Failed' | 'Cancelled';
  priority: number;
  queuePosition: number;
  requiredNozzleDiameter?: number;
  requiredMaterialType?: string;
  estimatedPrintTime?: string; // ISO duration
  estimatedFilamentUsage?: number;
  actualStartTime?: string;
  actualEndTime?: string;
  failureReason?: string;
  createdAt: string;
  updatedAt: string;
}

export interface AddJobRequest {
  gcodeFileId: string;
  assignedPrinterId?: string;
  priority?: 'Low' | 'Normal' | 'High' | 'Urgent';
  requiredNozzleDiameter?: number;
  requiredMaterialType?: string;
}

export interface UpdateJobPriorityRequest {
  priority: number;
}

class QueueService {
  private baseUrl = '/api';

  async getQueueOverview(): Promise<QueueOverview[]> {
    const response = await fetch(`${this.baseUrl}/job-queue`);
    if (!response.ok) {
      throw new Error(`Failed to fetch queue overview: ${response.statusText}`);
    }
    return response.json();
  }

  async getPrinterQueue(printerId: string): Promise<PrintJob[]> {
    const response = await fetch(`${this.baseUrl}/job-queue/printer/${printerId}`);
    if (!response.ok) {
      throw new Error(`Failed to fetch printer queue: ${response.statusText}`);
    }
    return response.json();
  }

  async addJobToQueue(request: AddJobRequest): Promise<PrintJob> {
    const response = await fetch(`${this.baseUrl}/job-queue`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${localStorage.getItem('auth-token')}`
      },
      body: JSON.stringify(request)
    });

    if (!response.ok) {
      throw new Error(`Failed to add job to queue: ${response.statusText}`);
    }

    return response.json();
  }

  async getJob(jobId: string): Promise<PrintJob> {
    const response = await fetch(`${this.baseUrl}/queue/jobs/${jobId}`);
    if (!response.ok) {
      throw new Error(`Failed to fetch job: ${response.statusText}`);
    }
    return response.json();
  }

  async removeJobFromQueue(jobId: string): Promise<void> {
    const response = await fetch(`${this.baseUrl}/queue/jobs/${jobId}`, {
      method: 'DELETE',
      headers: {
        'Authorization': `Bearer ${localStorage.getItem('auth-token')}`
      }
    });

    if (!response.ok) {
      throw new Error(`Failed to remove job from queue: ${response.statusText}`);
    }
  }

  async updateJobPriority(jobId: string, priority: number): Promise<PrintJob> {
    const response = await fetch(`${this.baseUrl}/queue/jobs/${jobId}/priority`, {
      method: 'PATCH',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${localStorage.getItem('auth-token')}`
      },
      body: JSON.stringify({ priority })
    });

    if (!response.ok) {
      throw new Error(`Failed to update job priority: ${response.statusText}`);
    }

    return response.json();
  }

  // Helper methods for status formatting
  getStatusColor(status: PrintJob['status']): string {
    switch (status) {
      case 'Queued':
      case 'Assigned':
        return 'bg-yellow-100 text-yellow-800';
      case 'Starting':
      case 'Printing':
        return 'bg-blue-100 text-blue-800';
      case 'Paused':
        return 'bg-orange-100 text-orange-800';
      case 'Completed':
        return 'bg-green-100 text-green-800';
      case 'Failed':
      case 'Cancelled':
        return 'bg-red-100 text-red-800';
      default:
        return 'bg-gray-100 text-gray-800';
    }
  }

  getPriorityColor(priority: number): string {
    if (priority >= 3) return 'bg-red-100 text-red-800';
    if (priority === 2) return 'bg-orange-100 text-orange-800';
    if (priority === 1) return 'bg-blue-100 text-blue-800';
    return 'bg-gray-100 text-gray-800';
  }

  getPriorityLabel(priority: number): string {
    switch (priority) {
      case 3: return 'Urgent';
      case 2: return 'High';
      case 1: return 'Normal';
      case 0: return 'Low';
      default: return 'Normal';
    }
  }

  formatDuration(duration?: string): string {
    if (!duration) return 'Unknown';
    
    // Simple duration parsing for ISO format like "PT2H30M"
    const match = duration.match(/PT(?:(\d+)H)?(?:(\d+)M)?/);
    if (!match) return duration;
    
    const hours = parseInt(match[1] || '0');
    const minutes = parseInt(match[2] || '0');
    
    if (hours > 0) {
      return `${hours}h ${minutes}m`;
    }
    return `${minutes}m`;
  }

  formatFilamentUsage(grams?: number): string {
    if (!grams) return 'Unknown';
    if (grams < 1000) return `${grams}g`;
    return `${(grams / 1000).toFixed(1)}kg`;
  }
}

export const queueService = new QueueService();