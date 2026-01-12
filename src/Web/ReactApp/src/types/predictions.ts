/**
 * Phase 4.2: Predictive Completion Estimates TypeScript types
 */

export type ConfidenceLevel = 'High' | 'Medium' | 'Low';

export interface CompletionPredictionDto {
  jobId: string;
  estimatedCompletionTime: string; // ISO datetime
  estimatedDuration: string | null; // ISO duration (PT format) or null
  confidence: ConfidenceLevel;
  sampleSize: number;
  variancePercent: number | null;
  note: string | null;
}

export interface DurationStatsDto {
  totalJobs: number;
  successfulJobs: number;
  successRate: number; // 0.0 to 1.0
  averageDuration: string; // ISO duration
  medianDuration: string; // ISO duration
  minDuration: string; // ISO duration
  maxDuration: string; // ISO duration
  standardDeviation: number;
  variance: number;
  material: string | null;
  printerModelName: string | null;
}

export interface PrintJobStatisticsDto {
  jobId: string;
  actualDurationMs: number | null;
  estimatedDurationMs: number | null;
  material: string | null;
  nozzleTemperature: number | null;
  bedTemperature: number | null;
  speedPercentage: number;
  isSuccess: boolean;
  failureReason: string | null;
  completedAtUtc: string | null; // ISO datetime
}

export interface RecordCompletionRequest {
  actualDurationMs: number;
  isSuccess: boolean;
  failureReason?: string | null;
}
