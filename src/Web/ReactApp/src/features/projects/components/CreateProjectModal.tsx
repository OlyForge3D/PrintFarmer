import React, { useState } from 'react';
import { useQuery, useMutation } from '@tanstack/react-query';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui';
import { 
  PlusIcon, 
  DeleteIcon,
  SearchIcon,
} from '@/common/components/icons/MdiIcons';
import { projectService } from '@/services/projectService';
import { apiClient } from '@/services/api';
import type { 
  CreatePrintProjectRequest, 
  AddFileToProjectRequest,
  PrintColorRequirement,
  GcodeFile,
} from '@/types/api';

interface CreateProjectModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSuccess: () => void;
  /** Pre-selected file IDs to add to the project */
  initialFileIds?: string[];
}

export const CreateProjectModal: React.FC<CreateProjectModalProps> = ({
  isOpen,
  onClose,
  onSuccess,
  initialFileIds = [],
}) => {
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [priority, setPriority] = useState(0);
  const [dueDate, setDueDate] = useState('');
  const [notes, setNotes] = useState('');
  const [selectedFiles, setSelectedFiles] = useState<AddFileToProjectRequest[]>(
    initialFileIds.map(id => ({
      gcodeFileId: id,
      colorRequirement: 'Base' as PrintColorRequirement,
      printCount: 1,
    }))
  );
  const [showFilePicker, setShowFilePicker] = useState(false);
  const [fileSearch, setFileSearch] = useState('');

  // Fetch available gcode files for picker
  const { data: gcodeFiles = [] } = useQuery({
    queryKey: ['gcode-files-for-project', fileSearch],
    queryFn: async () => {
      // Use the query endpoint that supports filtering
      const result = await apiClient.getGcodeFilesQuery({ 
        search: fileSearch, 
        pageSize: 50 
      });
      return result.files || [];
    },
    enabled: showFilePicker,
    staleTime: 30 * 1000,
  });

  // Create project mutation
  const createMutation = useMutation({
    mutationFn: (request: CreatePrintProjectRequest) => projectService.createProject(request),
    onSuccess: () => {
      resetForm();
      onSuccess();
    },
  });

  const resetForm = () => {
    setName('');
    setDescription('');
    setPriority(0);
    setDueDate('');
    setNotes('');
    setSelectedFiles([]);
    setShowFilePicker(false);
    setFileSearch('');
  };

  const handleClose = () => {
    resetForm();
    onClose();
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!name.trim()) return;

    const request: CreatePrintProjectRequest = {
      name: name.trim(),
      description: description.trim() || undefined,
      priority,
      dueDate: dueDate || undefined,
      notes: notes.trim() || undefined,
      files: selectedFiles.length > 0 ? selectedFiles : undefined,
    };

    createMutation.mutate(request);
  };

  const addFile = (file: GcodeFile) => {
    if (selectedFiles.some(f => f.gcodeFileId === file.id)) return;
    
    setSelectedFiles([
      ...selectedFiles,
      {
        gcodeFileId: file.id,
        colorRequirement: 'Base' as PrintColorRequirement,
        printCount: 1,
      },
    ]);
    setShowFilePicker(false);
    setFileSearch('');
  };

  const removeFile = (fileId: string) => {
    setSelectedFiles(selectedFiles.filter(f => f.gcodeFileId !== fileId));
  };

  const updateFileSettings = (
    fileId: string,
    updates: Partial<AddFileToProjectRequest>
  ) => {
    setSelectedFiles(
      selectedFiles.map(f =>
        f.gcodeFileId === fileId ? { ...f, ...updates } : f
      )
    );
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title="Create New Project"
      size="lg"
      footer={
        <div className="flex gap-3 w-full justify-end">
          <Button variant="secondary" onClick={handleClose} disabled={createMutation.isPending}>
            Cancel
          </Button>
          <Button
            variant="primary"
            onClick={handleSubmit}
            disabled={!name.trim() || createMutation.isPending}
          >
            {createMutation.isPending ? 'Creating...' : 'Create Project'}
          </Button>
        </div>
      }
    >
      <form onSubmit={handleSubmit} className="space-y-4">
        {/* Name */}
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1">
            Project Name <span className="text-pf-error">*</span>
          </label>
          <input
            type="text"
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="e.g., Voron 2.4 Build Kit"
            className="w-full px-3 py-2 bg-pf-bg-2 border border-pf-border rounded-lg text-pf-text-primary placeholder:text-pf-text-tertiary focus:outline-none focus:ring-2 focus:ring-pf-accent"
            required
          />
        </div>

        {/* Description */}
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1">
            Description
          </label>
          <textarea
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="Brief description of the project..."
            rows={2}
            className="w-full px-3 py-2 bg-pf-bg-2 border border-pf-border rounded-lg text-pf-text-primary placeholder:text-pf-text-tertiary focus:outline-none focus:ring-2 focus:ring-pf-accent resize-none"
          />
        </div>

        {/* Priority and Due Date row */}
        <div className="grid grid-cols-2 gap-4">
          <div>
            <label className="block text-sm font-medium text-pf-text-primary mb-1">
              Priority
            </label>
            <select
              value={priority}
              onChange={(e) => setPriority(Number(e.target.value))}
              className="w-full px-3 py-2 bg-pf-bg-2 border border-pf-border rounded-lg text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent"
            >
              <option value={0}>Normal</option>
              <option value={1}>High</option>
              <option value={2}>Urgent</option>
              <option value={-1}>Low</option>
            </select>
          </div>

          <div>
            <label className="block text-sm font-medium text-pf-text-primary mb-1">
              Due Date
            </label>
            <input
              type="date"
              value={dueDate}
              onChange={(e) => setDueDate(e.target.value)}
              className="w-full px-3 py-2 bg-pf-bg-2 border border-pf-border rounded-lg text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent"
            />
          </div>
        </div>

        {/* Notes */}
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1">
            Notes
          </label>
          <textarea
            value={notes}
            onChange={(e) => setNotes(e.target.value)}
            placeholder="Additional notes about the project..."
            rows={2}
            className="w-full px-3 py-2 bg-pf-bg-2 border border-pf-border rounded-lg text-pf-text-primary placeholder:text-pf-text-tertiary focus:outline-none focus:ring-2 focus:ring-pf-accent resize-none"
          />
        </div>

        {/* Files Section */}
        <div>
          <div className="flex items-center justify-between mb-2">
            <label className="block text-sm font-medium text-pf-text-primary">
              G-Code Files
            </label>
            <Button
              type="button"
              variant="secondary"
              size="sm"
              onClick={() => setShowFilePicker(!showFilePicker)}
              iconLeft={<PlusIcon className="w-4 h-4" />}
            >
              Add Files
            </Button>
          </div>

          {/* File Picker */}
          {showFilePicker && (
            <div className="mb-3 p-3 bg-pf-bg-2 border border-pf-border rounded-lg">
              <div className="relative mb-2">
                <SearchIcon className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-pf-text-tertiary" />
                <input
                  type="text"
                  value={fileSearch}
                  onChange={(e) => setFileSearch(e.target.value)}
                  placeholder="Search files..."
                  className="w-full pl-9 pr-3 py-2 bg-pf-bg-1 border border-pf-border rounded-lg text-sm text-pf-text-primary placeholder:text-pf-text-tertiary focus:outline-none focus:ring-2 focus:ring-pf-accent"
                />
              </div>
              <div className="max-h-40 overflow-y-auto space-y-1">
                {gcodeFiles.length === 0 ? (
                  <p className="text-sm text-pf-text-tertiary p-2">No files found</p>
                ) : (
                  gcodeFiles
                    .filter(f => !selectedFiles.some(sf => sf.gcodeFileId === f.id))
                    .map((file) => (
                      <button
                        key={file.id}
                        type="button"
                        onClick={() => addFile(file)}
                        className="w-full text-left px-3 py-2 rounded hover:bg-pf-bg-1 text-sm text-pf-text-primary truncate"
                      >
                        {file.fileName}
                      </button>
                    ))
                )}
              </div>
            </div>
          )}

          {/* Selected Files List */}
          {selectedFiles.length > 0 ? (
            <div className="space-y-2">
              {selectedFiles.map((file) => (
                <SelectedFileRow
                  key={file.gcodeFileId}
                  file={file}
                  gcodeFiles={gcodeFiles}
                  onUpdate={(updates) => updateFileSettings(file.gcodeFileId, updates)}
                  onRemove={() => removeFile(file.gcodeFileId)}
                />
              ))}
            </div>
          ) : (
            <p className="text-sm text-pf-text-tertiary py-2">
              No files added yet. You can add files now or after creating the project.
            </p>
          )}
        </div>

        {createMutation.isError && (
          <p className="text-sm text-pf-error">
            Failed to create project: {String(createMutation.error)}
          </p>
        )}
      </form>
    </Modal>
  );
};

// Selected file row component
interface SelectedFileRowProps {
  file: AddFileToProjectRequest;
  gcodeFiles: GcodeFile[];
  onUpdate: (updates: Partial<AddFileToProjectRequest>) => void;
  onRemove: () => void;
}

const SelectedFileRow: React.FC<SelectedFileRowProps> = ({
  file,
  gcodeFiles,
  onUpdate,
  onRemove,
}) => {
  const gcodeFile = gcodeFiles.find(f => f.id === file.gcodeFileId);
  const fileName = gcodeFile?.fileName || file.gcodeFileId;

  return (
    <div className="flex items-center gap-2 p-2 bg-pf-bg-2 border border-pf-border rounded-lg">
      <div className="flex-1 min-w-0">
        <p className="text-sm text-pf-text-primary truncate">{fileName}</p>
      </div>

      {/* Color requirement */}
      <select
        value={file.colorRequirement}
        onChange={(e) => onUpdate({ colorRequirement: e.target.value as PrintColorRequirement })}
        className="px-2 py-1 text-xs bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary"
        title="Color requirement"
      >
        <option value="Base">Base</option>
        <option value="Accent">Accent</option>
        <option value="Custom">Custom</option>
      </select>

      {/* Print count */}
      <div className="flex items-center gap-1">
        <span className="text-xs text-pf-text-secondary">×</span>
        <input
          type="number"
          min={1}
          value={file.printCount}
          onChange={(e) => onUpdate({ printCount: Math.max(1, parseInt(e.target.value) || 1) })}
          className="w-12 px-2 py-1 text-xs text-center bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary"
        />
      </div>

      {/* Remove button */}
      <Button
        type="button"
        variant="subtle"
        size="sm"
        onClick={onRemove}
        className="!p-1 text-pf-text-tertiary hover:text-pf-error"
        title="Remove file"
      >
        <DeleteIcon className="w-4 h-4" />
      </Button>
    </div>
  );
};

export default CreateProjectModal;
