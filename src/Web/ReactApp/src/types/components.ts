/**
 * Type definitions for component props interfaces
 * Consolidated from scattered prop interface definitions across features
 */

import type { Model } from './models';
import type { HistoryJob, HistoryStats, JobAction, JobStatus, JobDetails } from './queue';
import type { FileEntry } from './gcode';
import type { GcodeFile, Printer, JobQueuePrintJob } from './api';

// ====================================
// Models3D Component Props
// ====================================

export interface ModelGridViewProps {
  models: Model[];
  isLoading: boolean;
  onViewerModel: (model: Model) => void;
  onTagModel: (model: Model) => void;
  formatFileSize: (bytes: number) => string;
}

export interface ModelListViewProps {
  models: Model[];
  isLoading: boolean;
  onViewerModel: (model: Model) => void;
  onTagModel: (model: Model) => void;
  formatFileSize: (bytes: number) => string;
  onDelete?: (model: Model) => void;
  onDownload?: (model: Model) => void;
  selectedModelIds?: string[];
  onSelectModel?: (modelId: string, selected: boolean) => void;
  onSelectAllModels?: (modelIds: string[]) => void;
}

// ====================================
// Queue Component Props
// ====================================

export interface JobTagsEditorProps {
  jobId: string;
  initialTags?: string[];
  onSave?: (tags: string[]) => void;
}

export interface JobDetailsModalProps {
  isOpen: boolean;
  onClose: () => void;
  jobId: string;
  jobDetails?: JobDetails;
  onSave?: (job: JobDetails) => void;
}

export interface JobNotesEditorProps {
  jobId: string;
  initialNotes?: string;
  onSave?: (notes: string) => void;
}

export interface TableFiltersBarProps {
  onFilterChange?: (filters: Record<string, unknown>) => void;
  availableFilters?: string[];
}

export interface HistoryJobCardProps {
  job: HistoryJob;
  onRerun?: (jobId: string) => void;
  onViewDetails?: (jobId: string) => void;
}

export interface QueueJobsTableProps {
  jobs: JobQueuePrintJob[];
  isLoading: boolean;
  onJobAction?: (jobId: string, action: JobAction) => void;
}

export interface HistoryStatisticsPanelProps {
  stats: HistoryStats;
}

export interface ModelFiltersBarProps {
  models: string[];
  selectedModel: string | null;
  onModelChange: (model: string | null) => void;
  selectedStatuses: JobStatus[];
  onStatusChange: (statuses: JobStatus[]) => void;
  sortBy: "name" | "queue" | "waitTime" | "printing";
  onSortChange: (sort: "name" | "queue" | "waitTime" | "printing") => void;
  onRefresh: () => void;
  isLoading: boolean;
}

export interface QueueHistoryTabProps {
  onRerun?: (jobId: string) => Promise<void>;
  onViewDetails?: (jobId: string) => void;
  dateFrom: Date | null;
  dateTo: Date | null;
}

export interface HistoryFiltersBarProps {
  onFilterChange?: (filters: Record<string, unknown>) => void;
}

// ====================================
// G-code Component Props
// ====================================

export interface GcodeFileCardProps {
  file: GcodeFile;
  onSelect?: (file: GcodeFile) => void;
  onDelete?: (file: GcodeFile) => void;
  onDownload?: (file: GcodeFile) => void;
}

export interface PrinterCardProps {
  printer: Printer;
  onSelect?: (printer: Printer) => void;
  selected?: boolean;
}

export interface QueueGcodeModalProps {
  isOpen: boolean;
  onClose: () => void;
  gcodeFile?: GcodeFile;
}

export interface HealthGaugeProps {
  score: number;
  size?: 'sm' | 'md' | 'lg';
}

export interface HealthStatisticsProps {
  statistics: Record<string, unknown>;
}

export interface AuditTimelineProps {
  audits: Array<Record<string, unknown>>;
}

export interface IssuesListProps {
  issues: Array<Record<string, unknown>>;
}

export interface ExplorerFileBrowserProps {
  onFileSelect?: (file: FileEntry) => void;
  onNavigate?: (path: string) => void;
}

export interface FileBrowserProps {
  printerId?: string;
}

// ====================================
// Slicer Component Props
// ====================================

export interface ProfileSelectorProps {
  selectedProfileId: string;
  onProfileChange: (profileId: string) => void;
  profileType: 'machine' | 'process' | 'filament';
  filterByManufacturer?: string;
}
