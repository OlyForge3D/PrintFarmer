import React, { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui';
import { Badge } from '@/common/components/ui/Badge';
import { 
  CheckIcon, 
  PlusIcon,
  DeleteIcon,
  EditIcon,
} from '@/common/components/icons/MdiIcons';
import { projectService } from '@/services/projectService';
import { apiClient } from '@/services/api';
import type { 
  PrintProjectDetailDto,
  PrintProjectFileDto,
  SpoolmanFilament,
  QueueProjectRequest,
  QueueProjectResultDto,
} from '@/types/api';

interface ProjectDetailModalProps {
  project: PrintProjectDetailDto;
  isOpen: boolean;
  onClose: () => void;
  onUpdate: () => void;
  /** Called when the user wants to edit the project (opens the unified form modal) */
  onEdit: () => void;
}

// Status badge color mapping — use string keys to avoid TDZ in production builds
const statusVariantMap: Record<string, 'default' | 'primary' | 'success' | 'warning' | 'error'> = {
  Open: 'default',
  InProgress: 'primary',
  Completed: 'success',
  Cancelled: 'error',
  OnHold: 'warning',
};

const statusLabelMap: Record<string, string> = {
  Open: 'Open',
  InProgress: 'In Progress',
  Completed: 'Completed',
  Cancelled: 'Cancelled',
  OnHold: 'On Hold',
};

const fileStatusVariantMap: Record<string, 'default' | 'primary' | 'success' | 'warning'> = {
  Pending: 'default',
  Printing: 'primary',
  Completed: 'success',
  Skipped: 'warning',
};

export const ProjectDetailModal: React.FC<ProjectDetailModalProps> = ({
  project,
  isOpen,
  onClose,
  onUpdate,
  onEdit,
}) => {
  const queryClient = useQueryClient();
  const [showQueueConfirm, setShowQueueConfirm] = useState(false);
  const [queueResult, setQueueResult] = useState<QueueProjectResultDto | null>(null);

  // Fetch Spoolman filaments for display
  const { data: filaments } = useQuery({
    queryKey: ['spoolman-filaments-project-detail'],
    queryFn: () => apiClient.getFilaments(),
    staleTime: 60_000,
  });

  // Build filament lookup map
  const filamentMap = React.useMemo(() => {
    const map = new Map<number, SpoolmanFilament>();
    if (filaments) {
      for (const filament of filaments) {
        map.set(filament.id, filament);
      }
    }
    return map;
  }, [filaments]);

  // Mark file as printed mutation
  const markPrintedMutation = useMutation({
    mutationFn: (fileId: string) => projectService.markFilePrinted(project.id, fileId),
    onSuccess: () => {
      onUpdate();
      queryClient.invalidateQueries({ queryKey: ['projects'] });
    },
  });

  // Remove file mutation
  const removeFileMutation = useMutation({
    mutationFn: (fileId: string) => projectService.removeFileFromProject(project.id, fileId),
    onSuccess: () => {
      onUpdate();
      queryClient.invalidateQueries({ queryKey: ['projects'] });
    },
  });

  // Queue project mutation
  const queueProjectMutation = useMutation({
    mutationFn: (request: QueueProjectRequest) => projectService.queueProject(project.id, request),
    onSuccess: (result) => {
      setQueueResult(result);
      setShowQueueConfirm(false);
      onUpdate();
      queryClient.invalidateQueries({ queryKey: ['projects'] });
      queryClient.invalidateQueries({ queryKey: ['job-queue'] });
    },
  });

  const handleMarkPrinted = (fileId: string) => {
    markPrintedMutation.mutate(fileId);
  };

  const handleRemoveFile = (fileId: string) => {
    if (confirm('Remove this file from the project?')) {
      removeFileMutation.mutate(fileId);
    }
  };

  const handleQueueProject = () => {
    queueProjectMutation.mutate({
      groupByMaterial: true,
      groupByColor: true,
      priority: 1,
    });
  };

  // Calculate project-level estimates
  const pendingFiles = project.files.filter(f => !f.isComplete);
  const hasPendingFiles = pendingFiles.length > 0;
  const totalEstimatedMinutes = project.files.reduce((sum, f) => {
    return sum + (f.remainingPrintTimeMinutes ?? 0);
  }, 0);

  // Sort files: incomplete first, then by sort order
  const sortedFiles = [...project.files].sort((a, b) => {
    if (a.isComplete !== b.isComplete) return a.isComplete ? 1 : -1;
    return a.sortOrder - b.sortOrder;
  });

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={project.name}
      size="xl"
      footer={
        <div className="flex gap-3 w-full justify-between">
          <div className="flex gap-2">
            <Button
              variant="secondary"
              onClick={onEdit}
              iconLeft={<EditIcon className="w-4 h-4" />}
            >
              Edit
            </Button>
            <Button
              variant="secondary"
              onClick={onEdit}
              iconLeft={<PlusIcon className="w-4 h-4" />}
            >
              Add Files
            </Button>
            {hasPendingFiles && (
              <Button
                variant="primary"
                onClick={() => setShowQueueConfirm(true)}
                disabled={queueProjectMutation.isPending}
              >
                {queueProjectMutation.isPending ? 'Queuing...' : `Queue ${pendingFiles.length} File${pendingFiles.length !== 1 ? 's' : ''}`}
              </Button>
            )}
          </div>
          <div className="flex gap-3">
            <Button variant="secondary" onClick={onClose}>
              Close
            </Button>
          </div>
        </div>
      }
    >
      <div className="space-y-6">
        {/* Queue Result Banner */}
        {queueResult && (
          <div className="bg-pf-success/10 border border-pf-success/30 rounded-lg p-4">
            <p className="font-medium text-pf-success">
              Queued {queueResult.totalJobsQueued} job{queueResult.totalJobsQueued !== 1 ? 's' : ''} successfully
            </p>
            {queueResult.estimatedTotalTimeMinutes && (
              <p className="text-sm text-pf-text-secondary mt-1">
                Estimated total time: {formatDuration(queueResult.estimatedTotalTimeMinutes)}
              </p>
            )}
          </div>
        )}

        {/* Queue Confirmation Dialog */}
        {showQueueConfirm && (
          <div className="bg-pf-bg-2 border border-pf-border rounded-lg p-4 space-y-3">
            <h4 className="font-semibold text-pf-text-primary">Queue Project for Printing</h4>
            <p className="text-sm text-pf-text-secondary">
              {pendingFiles.length} file{pendingFiles.length !== 1 ? 's' : ''} with{' '}
              {pendingFiles.reduce((sum, f) => sum + f.remainingPrints, 0)} total print{pendingFiles.reduce((sum, f) => sum + f.remainingPrints, 0) !== 1 ? 's' : ''} will be added to the job queue.
            </p>
            {totalEstimatedMinutes > 0 && (
              <p className="text-sm text-pf-text-secondary">
                Estimated total print time: <span className="font-medium text-pf-text-primary">{formatDuration(totalEstimatedMinutes)}</span>
              </p>
            )}
            <p className="text-xs text-pf-text-tertiary">
              Jobs will be grouped by filament type and color to minimize filament changes.
            </p>
            {queueProjectMutation.isError && (
              <p className="text-sm text-pf-error">
                Failed to queue project. Check that compatible printers are available.
              </p>
            )}
            <div className="flex gap-2 justify-end">
              <Button variant="secondary" size="sm" onClick={() => setShowQueueConfirm(false)}>
                Cancel
              </Button>
              <Button
                variant="primary"
                size="sm"
                onClick={handleQueueProject}
                disabled={queueProjectMutation.isPending}
              >
                {queueProjectMutation.isPending ? 'Queuing...' : 'Confirm & Queue'}
              </Button>
            </div>
          </div>
        )}

        {/* Project Info */}
        <ProjectInfo project={project} totalEstimatedMinutes={totalEstimatedMinutes} />

        {/* Progress */}
        <div className="bg-pf-bg-2 rounded-lg p-4">
          <div className="flex items-center justify-between mb-2">
            <span className="text-sm font-medium text-pf-text-primary">Overall Progress</span>
            <span className="text-sm text-pf-text-secondary">
              {project.completedPrints} / {project.totalPrints} prints ({project.progressPercent}%)
            </span>
          </div>
          <div className="h-3 bg-pf-bg-1 rounded-full overflow-hidden">
            <div
              className="h-full bg-pf-accent rounded-full transition-all duration-300"
              style={{ width: `${project.progressPercent}%` }}
            />
          </div>
          {(project.estimatedTotalCost != null && project.estimatedTotalCost > 0) && (
            <div className="flex items-center justify-between mt-2 text-sm">
              <span className="text-pf-text-tertiary">Estimated Cost</span>
              <span className="text-pf-text-primary font-medium">${project.estimatedTotalCost.toFixed(2)}</span>
            </div>
          )}
          {(project.completedCost != null && project.completedCost > 0) && (
            <div className="flex items-center justify-between mt-1 text-sm">
              <span className="text-pf-text-tertiary">Spent So Far</span>
              <span className="text-pf-text-primary">${project.completedCost.toFixed(2)}</span>
            </div>
          )}
        </div>

        {/* Files List */}
        <div>
          <div className="flex items-center justify-between mb-3">
            <h3 className="font-semibold text-pf-text-primary">
              Files ({project.files.length})
            </h3>
          </div>

          {sortedFiles.length === 0 ? (
            <div className="text-center py-8 text-pf-text-secondary">
              <p className="mb-2">No files in this project</p>
              <Button
                variant="secondary"
                size="sm"
                iconLeft={<PlusIcon className="w-4 h-4" />}
                onClick={onEdit}
              >
                Add Files
              </Button>
            </div>
          ) : (
            <div className="space-y-2">
              {sortedFiles.map((file) => (
                <FileRow
                  key={file.id}
                  file={file}
                  filament={file.spoolmanFilamentId ? filamentMap.get(file.spoolmanFilamentId) : undefined}
                  onMarkPrinted={() => handleMarkPrinted(file.id)}
                  onRemove={() => handleRemoveFile(file.id)}
                  isMarkingPrinted={markPrintedMutation.isPending}
                />
              ))}
            </div>
          )}
        </div>
      </div>
    </Modal>
  );
};

// Helper to format minutes into human-readable duration
function formatDuration(minutes: number): string {
  if (minutes < 60) return `${Math.round(minutes)}m`;
  const hours = Math.floor(minutes / 60);
  const mins = Math.round(minutes % 60);
  if (hours < 24) return mins > 0 ? `${hours}h ${mins}m` : `${hours}h`;
  const days = Math.floor(hours / 24);
  const remainingHours = hours % 24;
  return remainingHours > 0 ? `${days}d ${remainingHours}h` : `${days}d`;
}

// Project info display
const ProjectInfo: React.FC<{ project: PrintProjectDetailDto; totalEstimatedMinutes: number }> = ({ project, totalEstimatedMinutes }) => (
  <div className="space-y-3">
    <div className="flex items-center gap-2">
      <Badge variant={statusVariantMap[project.status]}>
        {statusLabelMap[project.status]}
      </Badge>
      {project.priority > 0 && (
        <Badge variant="warning">
          {project.priority === 2 ? 'Urgent' : 'High Priority'}
        </Badge>
      )}
      {totalEstimatedMinutes > 0 && (
        <Badge variant="default">
          ~{formatDuration(totalEstimatedMinutes)} remaining
        </Badge>
      )}
    </div>

    {project.description && (
      <p className="text-pf-text-secondary">{project.description}</p>
    )}

    <div className="grid grid-cols-2 gap-4 text-sm">
      {project.dueDate && (
        <div>
          <span className="text-pf-text-tertiary">Due: </span>
          <span className="text-pf-text-primary">
            {new Date(project.dueDate).toLocaleDateString()}
          </span>
        </div>
      )}
      <div>
        <span className="text-pf-text-tertiary">Created: </span>
        <span className="text-pf-text-primary">
          {new Date(project.createdAt).toLocaleDateString()}
        </span>
      </div>
    </div>

    {project.notes && (
      <div className="bg-pf-bg-2 rounded-lg p-3">
        <p className="text-sm text-pf-text-secondary whitespace-pre-wrap">{project.notes}</p>
      </div>
    )}
  </div>
);

// File row component
interface FileRowProps {
  file: PrintProjectFileDto;
  filament?: SpoolmanFilament;
  onMarkPrinted: () => void;
  onRemove: () => void;
  isMarkingPrinted: boolean;
}

const FileRow: React.FC<FileRowProps> = ({
  file,
  filament,
  onMarkPrinted,
  onRemove,
  isMarkingPrinted,
}) => (
  <div
    className={`flex items-center gap-3 p-3 rounded-lg border ${
      file.isComplete
        ? 'bg-pf-success/5 border-pf-success/20'
        : 'bg-pf-bg-2 border-pf-border'
    }`}
  >
    {/* Thumbnail */}
    {file.thumbnailUrl ? (
      <img
        src={file.thumbnailUrl}
        alt={file.fileName}
        className="w-12 h-12 rounded object-cover bg-pf-bg-1"
      />
    ) : (
      <div className="w-12 h-12 rounded bg-pf-bg-1 flex items-center justify-center text-pf-text-tertiary">
        <span className="text-xs">GC</span>
      </div>
    )}

    {/* File info */}
    <div className="flex-1 min-w-0">
      <p className={`font-medium truncate ${file.isComplete ? 'text-pf-success' : 'text-pf-text-primary'}`}>
        {file.fileName}
      </p>
      <div className="flex items-center gap-2 mt-1 flex-wrap">
        <Badge variant={fileStatusVariantMap[file.status]} size="sm">
          {file.status}
        </Badge>
        {/* Plate info */}
        {(file.plateIndex != null || file.plateName) && (
          <Badge variant="primary" size="sm">
            {file.plateName ?? `Plate ${file.plateIndex! + 1}`}
          </Badge>
        )}
        {/* Material info */}
        {(file.requiredMaterial ?? file.materialRequirement) && (
          <Badge variant="default" size="sm">
            {file.requiredMaterial ?? file.materialRequirement}
          </Badge>
        )}
        {/* Spoolman filament info with color swatch */}
        {filament ? (
          <span className="inline-flex items-center gap-1 text-xs text-pf-text-secondary">
            {filament.colorHex && (
              <span
                className="inline-block w-3 h-3 rounded-full border border-pf-border"
                style={{ backgroundColor: `#${filament.colorHex.replace('#', '')}` }}
                title={filament.colorHex}
                role="img"
                aria-label={`Filament color ${filament.colorHex}`}
              />
            )}
            <span className="truncate max-w-[140px]" title={`${filament.name ?? 'Unnamed'}${filament.vendor ? ` (${filament.vendor})` : ''}`}>
              {filament.name ?? 'Unnamed'}
              {filament.vendor ? ` · ${filament.vendor}` : ''}
            </span>
            {filament.material && (
              <span className="text-pf-text-tertiary">({filament.material})</span>
            )}
          </span>
        ) : file.spoolmanFilamentId ? (
          <span className="text-xs text-pf-text-tertiary">Filament #{file.spoolmanFilamentId}</span>
        ) : null}
        {/* Estimated print time */}
        {file.estimatedPrintTimeMinutes && file.estimatedPrintTimeMinutes > 0 && (
          <span className="text-xs text-pf-text-tertiary">
            ~{formatDuration(file.estimatedPrintTimeMinutes)}/print
          </span>
        )}
      </div>
    </div>

    {/* Print progress */}
    <div className="text-center min-w-[80px]">
      <div className="text-lg font-semibold text-pf-text-primary">
        {file.printedCount} / {file.printCount}
      </div>
      <div className="text-xs text-pf-text-tertiary">
        {file.remainingPrints > 0 ? `${file.remainingPrints} left` : 'Complete'}
      </div>
      {file.remainingPrintTimeMinutes != null && file.remainingPrintTimeMinutes > 0 && (
        <div className="text-xs text-pf-text-tertiary">
          ~{formatDuration(file.remainingPrintTimeMinutes)}
        </div>
      )}
      {file.estimatedCostPerCopy != null && file.estimatedCostPerCopy > 0 && (
        <div className="text-xs text-pf-text-tertiary" title="Estimated cost per copy">
          ${file.estimatedCostPerCopy.toFixed(2)}/ea
          {file.estimatedFileCost != null && file.remainingPrints > 1 && (
            <span className="ml-1">({`$${file.estimatedFileCost.toFixed(2)} total`})</span>
          )}
        </div>
      )}
    </div>

    {/* Actions */}
    <div className="flex items-center gap-1">
      {!file.isComplete && (
        <Button
          variant="primary"
          size="sm"
          onClick={onMarkPrinted}
          disabled={isMarkingPrinted}
          title="Mark one print completed"
        >
          <CheckIcon className="w-4 h-4" />
        </Button>
      )}
      <Button
        variant="subtle"
        size="sm"
        onClick={onRemove}
        className="text-pf-text-tertiary hover:text-pf-error"
        title="Remove from project"
      >
        <DeleteIcon className="w-4 h-4" />
      </Button>
    </div>
  </div>
);

export default ProjectDetailModal;
