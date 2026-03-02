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
  gcodeFileId?: string;
  fileName?: string; // Original G-code filename for display
  assignedPrinterId?: string;
  printerName?: string;
  printerModel?: string;
  notes?: string;
  tags?: string[];
  // Nozzle and material from gcode metadata
  requiredMaterialType?: string;
  requiredNozzleDiameter?: number;
  // Estimated values from slicing
  estimatedPrintTimeSeconds?: number;
  estimatedFilamentUsageGrams?: number;
  // Actual values from printing
  actualPrintTimeSeconds?: number;
  actualFilamentUsageGrams?: number;
  actualStartTimeUtc?: string;
  actualEndTimeUtc?: string;
  // Legacy compatibility aliases
  materialType?: string;
  nozzleDiameter?: number;
  estimatedFilamentUsage?: string;
  // Timestamps
  createdAt?: string;
  createdAtUtc?: string;
  queuedAt?: string;
  queuedAtUtc?: string;
  startedAt?: string;
  completedAt?: string;
  // Spoolman filament assignment
  spoolmanFilamentId?: number;
  filamentName?: string;
  filamentVendor?: string;
  filamentColor?: string;
  // Cost
  estimatedCost?: number;
  actualCost?: number;
  // Thumbnail
  thumbnailUrl?: string;
}


