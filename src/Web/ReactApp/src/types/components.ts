/**
 * Type definitions for component props interfaces
 * Consolidated from scattered prop interface definitions across features
 */

import type { Model } from './models';
import type { HistoryJob, HistoryStats, ModelStats, JobAction, JobStatus, JobDetails } from './queue';
import type { FileEntry, HarvestDiscoveredFile, HarvestOptions, FileImportStatus } from './gcode';
import type { MachineProfile, SliceCompleteResult } from './slicer';
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

export interface CompletionPredictionProps {
  jobId: string;
  estimatedCompletionTime?: string;
}

export interface JobTimelineProps {
  jobId: string;
  events?: Array<{ timestamp: string; event: string }>;
}

export interface JobStateHistoryViewProps {
  jobId: string;
  stateHistory?: Array<{ state: string; timestamp: string }>;
}

export interface DurationComparisonProps {
  estimatedDuration: number;
  actualDuration?: number;
}

export interface ModelFilteredJobsTabProps {
  onViewAllJobs?: (modelName: string) => void;
  onJobAction?: (jobId: string, action: JobAction) => Promise<void>;
}

export interface JobDetailsSectionProps {
  jobDetails: JobDetails;
}

export interface ModelJobsCardProps {
  modelStats: ModelStats;
  onViewAllJobs?: (modelName: string) => void;
  onJobAction?: (jobId: string, action: JobAction) => Promise<void>;
}

export interface ModelStatisticsPanelProps {
  stats: ModelStats[];
}

export interface QueueHistoryTabProps {
  onRerun?: (jobId: string) => Promise<void>;
  onViewDetails?: (jobId: string) => void;
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

export interface IndexedFilesListProps {
  files: FileEntry[];
  onFileSelect?: (file: FileEntry) => void;
  selectedFiles?: string[];
}

export interface HarvestWizardStep2OptionsProps {
  options: HarvestOptions;
  onOptionsChange: (options: HarvestOptions) => void;
}

export interface HarvestWizardStep2OptionsRef {
  validate: () => boolean;
}

export interface HarvestWizardStep4ProgressProps {
  files: HarvestDiscoveredFile[];
  importStatus: FileImportStatus[];
  onComplete?: () => void;
}

export interface HarvestWizardStep3FileSelectionProps {
  files: HarvestDiscoveredFile[];
  onFilesChange: (files: HarvestDiscoveredFile[]) => void;
}

export interface HarvestWizardStep1SelectionProps {
  selectedPrinters: string[];
  onPrintersChange: (printers: string[]) => void;
}

export interface ErrorIconProps {
  message?: string;
  size?: 'sm' | 'md' | 'lg';
}

export interface HarvestOperationDetailsProps {
  operationId: string;
  onClose?: () => void;
}

export interface VirtualizedPrinterGridProps {
  printers: Printer[];
  onPrinterSelect?: (printer: Printer) => void;
  selectedPrinters?: string[];
}

export interface HarvestOperationCardProps {
  operation: Record<string, unknown>;
  onViewDetails?: (operationId: string) => void;
}

export interface HarvestWizardProps {
  isOpen: boolean;
  onClose: () => void;
  onComplete?: () => void;
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
  harvestId?: string;
  printerId?: string;
}

// ====================================
// Slicer Component Props
// ====================================

export interface WorkerSelectorProps {
  selectedWorkerId: string;
  onWorkerChange: (workerId: string) => void;
  requiredCapabilities?: string[];
}

export interface CloneProfilesModalProps {
  isOpen: boolean;
  onClose: () => void;
  sourceProfile: MachineProfile;
  onSuccess?: () => void;
}

export interface SlicerJobStatusProps {
  jobId: string;
  onComplete?: (result: SliceCompleteResult) => void;
}

export interface SlicerConfigModalProps {
  isOpen: boolean;
  onClose: () => void;
  modelId: string;
  onSliceComplete?: (result: SliceCompleteResult) => void;
}

export interface SlicerConfirmModalProps {
  isOpen: boolean;
  onClose: () => void;
  onConfirm: () => void;
  title?: string;
  message?: string;
}

export interface ProfileSelectorProps {
  selectedProfileId: string;
  onProfileChange: (profileId: string) => void;
  profileType: 'machine' | 'process' | 'filament';
  filterByManufacturer?: string;
}
