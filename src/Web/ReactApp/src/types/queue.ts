/**
 * Type definitions for print queue management
 * Consolidated from scattered definitions across the queue feature
 */

import type { JobQueuePrintJob } from './api';

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
  jobs: JobQueuePrintJob[];
}

/**
 * Detailed job information for the job details modal
 */
export interface JobDetails {
  id: string;
  name: string;
  status: string;
  priority: number;
  queuePosition: number;
  gcodeFileId: string;
  fileName?: string; // Original G-code filename for display
  printerId: string;
  printerName: string;
  printerModel: string;
  notes: string;
  tags: string[];
  materialType?: string;
  nozzleDiameter?: number;
  estimatedPrintTimeSeconds: number;
  estimatedFilamentUsage?: string;
  createdAt: string;
  queuedAt?: string;
  startedAt?: string;
  completedAt?: string;
}

/**
 * Type for tab selection in job details modal
 */
export type JobDetailsTabType = 'overview' | 'details' | 'timing' | 'history';
