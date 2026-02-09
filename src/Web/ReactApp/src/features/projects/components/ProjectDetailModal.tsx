import React, { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Select, Textarea } from '@/common/components/ui';
import { Badge } from '@/common/components/ui/Badge';
import { 
  CheckIcon, 
  PlusIcon,
  DeleteIcon,
  EditIcon,
} from '@/common/components/icons/MdiIcons';
import { projectService } from '@/services/projectService';
import type { 
  PrintProjectDetailDto,
  PrintProjectFileDto,
  PrintProjectStatus,
  PrintColorRequirement,
  PrintProjectFileStatus,
  UpdatePrintProjectRequest,
} from '@/types/api';

interface ProjectDetailModalProps {
  project: PrintProjectDetailDto;
  isOpen: boolean;
  onClose: () => void;
  onUpdate: () => void;
}

// Status badge color mapping
const statusVariantMap: Record<PrintProjectStatus, 'default' | 'primary' | 'success' | 'warning' | 'error'> = {
  Open: 'default',
  InProgress: 'primary',
  Completed: 'success',
  Cancelled: 'error',
  OnHold: 'warning',
};

const statusLabelMap: Record<PrintProjectStatus, string> = {
  Open: 'Open',
  InProgress: 'In Progress',
  Completed: 'Completed',
  Cancelled: 'Cancelled',
  OnHold: 'On Hold',
};

const colorVariantMap: Record<PrintColorRequirement, 'default' | 'primary' | 'warning'> = {
  Base: 'default',
  Accent: 'primary',
  Custom: 'warning',
};

const fileStatusVariantMap: Record<PrintProjectFileStatus, 'default' | 'primary' | 'success' | 'warning'> = {
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
}) => {
  const queryClient = useQueryClient();
  const [isEditing, setIsEditing] = useState(false);
  const [editedProject, setEditedProject] = useState({
    name: project.name,
    description: project.description || '',
    status: project.status,
    priority: project.priority,
    notes: project.notes || '',
  });

  // Update project mutation
  const updateMutation = useMutation({
    mutationFn: (request: UpdatePrintProjectRequest) =>
      projectService.updateProject(project.id, request),
    onSuccess: () => {
      setIsEditing(false);
      onUpdate();
      queryClient.invalidateQueries({ queryKey: ['projects'] });
    },
  });

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

  const handleSave = () => {
    updateMutation.mutate({
      name: editedProject.name,
      description: editedProject.description || undefined,
      status: editedProject.status,
      priority: editedProject.priority,
      notes: editedProject.notes || undefined,
    });
  };

  const handleMarkPrinted = (fileId: string) => {
    markPrintedMutation.mutate(fileId);
  };

  const handleRemoveFile = (fileId: string) => {
    if (confirm('Remove this file from the project?')) {
      removeFileMutation.mutate(fileId);
    }
  };

  // Sort files: incomplete first, then by sort order
  const sortedFiles = [...project.files].sort((a, b) => {
    if (a.isComplete !== b.isComplete) return a.isComplete ? 1 : -1;
    return a.sortOrder - b.sortOrder;
  });

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={isEditing ? 'Edit Project' : project.name}
      size="xl"
      footer={
        <div className="flex gap-3 w-full justify-between">
          <div>
            {!isEditing && (
              <Button
                variant="secondary"
                onClick={() => setIsEditing(true)}
                iconLeft={<EditIcon className="w-4 h-4" />}
              >
                Edit
              </Button>
            )}
          </div>
          <div className="flex gap-3">
            {isEditing ? (
              <>
                <Button variant="secondary" onClick={() => setIsEditing(false)}>
                  Cancel
                </Button>
                <Button
                  variant="primary"
                  onClick={handleSave}
                  disabled={updateMutation.isPending}
                >
                  {updateMutation.isPending ? 'Saving...' : 'Save Changes'}
                </Button>
              </>
            ) : (
              <Button variant="secondary" onClick={onClose}>
                Close
              </Button>
            )}
          </div>
        </div>
      }
    >
      <div className="space-y-6">
        {/* Project Info */}
        {isEditing ? (
          <EditProjectForm
            project={editedProject}
            onChange={setEditedProject}
          />
        ) : (
          <ProjectInfo project={project} />
        )}

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
        </div>

        {/* Files List */}
        <div>
          <div className="flex items-center justify-between mb-3">
            <h3 className="font-semibold text-pf-text-primary">
              Files ({project.files.length})
            </h3>
            {/* Could add "Add File" button here in future */}
          </div>

          {sortedFiles.length === 0 ? (
            <div className="text-center py-8 text-pf-text-secondary">
              <p className="mb-2">No files in this project</p>
              <Button
                variant="secondary"
                size="sm"
                iconLeft={<PlusIcon className="w-4 h-4" />}
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

// Project info display
const ProjectInfo: React.FC<{ project: PrintProjectDetailDto }> = ({ project }) => (
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

// Edit project form
interface EditProjectFormProps {
  project: {
    name: string;
    description: string;
    status: PrintProjectStatus;
    priority: number;
    notes: string;
  };
  onChange: (project: EditProjectFormProps['project']) => void;
}

const EditProjectForm: React.FC<EditProjectFormProps> = ({ project, onChange }) => (
  <div className="space-y-4">
    <div>
      <label className="block text-sm font-medium text-pf-text-primary mb-1">Name</label>
      <input
        type="text"
        value={project.name}
        onChange={(e) => onChange({ ...project, name: e.target.value })}
        className="w-full px-3 py-2 bg-pf-bg-2 border border-pf-border rounded-lg text-pf-text-primary"
      />
    </div>

    <div>
      <label className="block text-sm font-medium text-pf-text-primary mb-1">Description</label>
      <Textarea
        value={project.description}
        onChange={(e) => onChange({ ...project, description: e.target.value })}
        rows={2}
        className="bg-pf-bg-2 border-pf-border !rounded-lg !px-3 !py-2 resize-none min-h-0"
      />
    </div>

    <div className="grid grid-cols-2 gap-4">
      <div>
        <label className="block text-sm font-medium text-pf-text-primary mb-1">Status</label>
        <Select
          value={project.status}
          onChange={(e) => onChange({ ...project, status: e.target.value as PrintProjectStatus })}
          className="bg-pf-bg-2 border-pf-border !rounded-lg !px-3 !py-2"
        >
          <option value="Open">Open</option>
          <option value="InProgress">In Progress</option>
          <option value="OnHold">On Hold</option>
          <option value="Completed">Completed</option>
          <option value="Cancelled">Cancelled</option>
        </Select>
      </div>

      <div>
        <label className="block text-sm font-medium text-pf-text-primary mb-1">Priority</label>
        <Select
          value={project.priority}
          onChange={(e) => onChange({ ...project, priority: Number(e.target.value) })}
          className="bg-pf-bg-2 border-pf-border !rounded-lg !px-3 !py-2"
        >
          <option value={-1}>Low</option>
          <option value={0}>Normal</option>
          <option value={1}>High</option>
          <option value={2}>Urgent</option>
        </Select>
      </div>
    </div>

    <div>
      <label className="block text-sm font-medium text-pf-text-primary mb-1">Notes</label>
      <Textarea
        value={project.notes}
        onChange={(e) => onChange({ ...project, notes: e.target.value })}
        rows={3}
        className="bg-pf-bg-2 border-pf-border !rounded-lg !px-3 !py-2 resize-none min-h-0"
      />
    </div>
  </div>
);

// File row component
interface FileRowProps {
  file: PrintProjectFileDto;
  onMarkPrinted: () => void;
  onRemove: () => void;
  isMarkingPrinted: boolean;
}

const FileRow: React.FC<FileRowProps> = ({
  file,
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
      <div className="flex items-center gap-2 mt-1">
        <Badge variant={colorVariantMap[file.colorRequirement]} size="sm">
          {file.colorRequirement}
        </Badge>
        <Badge variant={fileStatusVariantMap[file.status]} size="sm">
          {file.status}
        </Badge>
        {file.materialRequirement && (
          <span className="text-xs text-pf-text-tertiary">{file.materialRequirement}</span>
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
