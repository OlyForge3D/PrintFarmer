// Re-export all types from api.ts for backwards compatibility
// Components that import from this file will get types from api.ts

export type {
  QueuedPrintJobWithFileMetaDto,
  QueuedPrintJobDto,
  QueueGcodeFileMetaDto,
  QueuePrinterMetaDto,
  QueueStatsDto,
  QueuePrinterModelStatsDto,
  QueueHistoryPageDto,
  QueueHistoryEntryDto,
  TimelineEventDto,
  StateTransitionDto,
  JobStateHistoryDto,
  DurationStatsDto,
} from '@/types/api';

// Re-export DurationAnalyticsDto which is used by timing components
export interface DurationAnalyticsDto {
  period: string;
  printerStats: DurationStatsDto[];
  overallAccuracyPercent?: number;
  totalJobsAnalyzed: number;
}

// Import DurationStatsDto for use above
import type { DurationStatsDto } from '@/types/api';
