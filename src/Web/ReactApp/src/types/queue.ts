/**
 * Type definitions for print queue management
 * Consolidated from scattered definitions across the queue feature
 */

/**
 * Status of a job in the print queue or history
 */
export type JobStatus = 'queued' | 'printing' | 'paused' | 'completed' | 'failed';

/**
 * Available actions that can be performed on a queue job
 */
export type JobAction = 'pause' | 'resume' | 'cancel' | 'priority';

/**
 * Represents a job in the history tab
 */
export interface HistoryJob {
  id: string;
  name: string;
  printerName: string;
  status: 'completed' | 'failed' | 'cancelled';
  completionPercentage: number;
  startedAt: string;
  completedAt: string | null;
  durationSeconds: number;
  failureReason?: string;
}

/**
 * Statistics for the history tab
 */
export interface HistoryStats {
  totalCompleted: number;
  totalFailed: number;
  totalCancelled: number;
  successRate: number;
  averageDurationMinutes: number;
  failureReasons: { [key: string]: number };
}

/**
 * Statistics calculated for a single printer model
 */
export interface ModelStats {
  name: string;
  queuedCount: number;
  printingCount: number;
  pausedCount: number;
  totalCount: number;
  averageWaitTimeMinutes: number;
  jobs: any[]; // QueueJob from api.ts
}

/**
 * Detailed job information for the job details modal
 */
export interface JobDetails {
  id: string;
  name: string;
  status: string;
  progress?: number;
  printer?: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  estimatedTime?: number;
  actualTime?: number;
  // Additional fields as needed
  [key: string]: any;
}

/**
 * Type for tab selection in job details modal
 */
export type JobDetailsTabType = 'overview' | 'details' | 'timing' | 'history';
