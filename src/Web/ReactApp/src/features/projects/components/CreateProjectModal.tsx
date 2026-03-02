import React, { useState, useMemo, useEffect } from 'react';
import { useQuery, useMutation } from '@tanstack/react-query';
import { Modal } from '@/common/components/modals/Modal';
import { FilePickerModal } from '@/common/components/modals/FilePickerModal';
import { Button, Select, Textarea } from '@/common/components/ui';
import { 
  PlusIcon, 
  DeleteIcon,
} from '@/common/components/icons/MdiIcons';
import { ColorSwatch } from '@/features/filamentManagement/components/ColorSwatch';
import { projectService } from '@/services/projectService';
import { templateService } from '@/services/templateService';
import { apiClient } from '@/services/api';
import { PrintProjectStatus } from '@/types/api';
import type { 
  CreatePrintProjectRequest, 
  AddFileToProjectRequest,
  GcodeFile,
  PrintProjectDetailDto,
  PrintProjectTemplateListDto,
  SpoolmanFilament,
} from '@/types/api';

interface CreateProjectModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSuccess: () => void;
  /** Pre-selected file IDs to add to the project */
  initialFileIds?: string[];
  /** When provided, the modal operates in edit mode for this project */
  editProject?: PrintProjectDetailDto;
}

export const CreateProjectModal: React.FC<CreateProjectModalProps> = ({
  isOpen,
  onClose,
  onSuccess,
  initialFileIds = [],
  editProject,
}) => {
  const isEditMode = !!editProject;

  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [status, setStatus] = useState<PrintProjectStatus>(PrintProjectStatus.Open);
  const [priority, setPriority] = useState(0);
  const [dueDate, setDueDate] = useState('');
  const [notes, setNotes] = useState('');
  const [selectedTemplate, setSelectedTemplate] = useState<string>('');
  const [selectedFiles, setSelectedFiles] = useState<AddFileToProjectRequest[]>([]);
  const [showFilePicker, setShowFilePicker] = useState(false);
  // Cache gcode file metadata so it persists after picker closes
  const [gcodeFileCache, setGcodeFileCache] = useState<Record<string, GcodeFile>>({});
  // Track which filament is assigned to each file (keyed by gcodeFileId)
  const [filamentAssignments, setFilamentAssignments] = useState<Record<string, number>>({});

  // Initialize form when modal opens
  useEffect(() => {
    if (!isOpen) return;

    if (editProject) {
      // Edit mode: populate from existing project
      setName(editProject.name);
      setDescription(editProject.description || '');
      setStatus(editProject.status);
      setPriority(editProject.priority);
      setDueDate(editProject.dueDate ? editProject.dueDate.split('T')[0] : '');
      setNotes(editProject.notes || '');
      setSelectedTemplate('');

      const cache: Record<string, GcodeFile> = {};
      const assignments: Record<string, number> = {};
      const files: AddFileToProjectRequest[] = [];

      for (const f of editProject.files) {
        cache[f.gcodeFileId] = {
          id: f.gcodeFileId,
          name: f.fileName,
          thumbnailUrl: f.thumbnailUrl,
          extractedMaterial: f.materialRequirement,
        } as GcodeFile;

        files.push({
          gcodeFileId: f.gcodeFileId,
          printCount: f.printCount,
          materialRequirement: f.materialRequirement || undefined,
          notes: f.notes || undefined,
        });

        if (f.spoolmanFilamentId) {
          assignments[f.gcodeFileId] = f.spoolmanFilamentId;
        }
      }

      setGcodeFileCache(cache);
      setFilamentAssignments(assignments);
      setSelectedFiles(files);
    } else if (initialFileIds.length > 0) {
      // Create mode with pre-selected files
      setSelectedFiles(initialFileIds.map(id => ({
        gcodeFileId: id,
        printCount: 1,
      })));
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isOpen, editProject?.id]);

  // Fetch available templates
  const { data: templates = [] } = useQuery({
    queryKey: ['project-templates'],
    queryFn: () => templateService.getTemplates(),
    staleTime: 5 * 60 * 1000, // 5 minutes
  });

  // Fetch available filaments from Spoolman
  const { data: filaments = [] } = useQuery({
    queryKey: ['spoolman-filaments-for-project'],
    queryFn: () => apiClient.getFilaments(),
    staleTime: 60 * 1000,
  });

  // Index filaments by ID for quick lookup
  const filamentsById = useMemo(() => {
    const map = new Map<number, SpoolmanFilament>();
    for (const filament of filaments) {
      map.set(filament.id, filament);
    }
    return map;
  }, [filaments]);

  // Create project mutation
  const createMutation = useMutation({
    mutationFn: (request: CreatePrintProjectRequest) => projectService.createProject(request),
    onSuccess: () => {
      resetForm();
      onSuccess();
    },
  });

  // Edit project mutation (handles metadata update + file reconciliation)
  const editMutation = useMutation({
    mutationFn: async () => {
      if (!editProject) return;

      // 1. Update project metadata
      await projectService.updateProject(editProject.id, {
        name: name.trim(),
        description: description.trim() || undefined,
        status,
        priority,
        dueDate: dueDate || undefined,
        notes: notes.trim() || undefined,
      });

      // 2. Reconcile files
      const originalMap = new Map(editProject.files.map(f => [f.gcodeFileId, f]));
      const currentIds = new Set(selectedFiles.map(f => f.gcodeFileId));

      // Remove files that were deleted
      for (const origFile of editProject.files) {
        if (!currentIds.has(origFile.gcodeFileId)) {
          await projectService.removeFileFromProject(editProject.id, origFile.id);
        }
      }

      // Add files that are new
      for (const file of selectedFiles) {
        if (!originalMap.has(file.gcodeFileId)) {
          await projectService.addFileToProject(editProject.id, {
            ...file,
            spoolmanFilamentId: filamentAssignments[file.gcodeFileId] ?? undefined,
          });
        }
      }

      // Update existing files that changed
      for (const file of selectedFiles) {
        const orig = originalMap.get(file.gcodeFileId);
        if (!orig) continue; // new file, already handled above

        const newFilamentId = filamentAssignments[file.gcodeFileId] ?? null;
        const changed =
          file.printCount !== orig.printCount ||
          newFilamentId !== orig.spoolmanFilamentId ||
          (file.materialRequirement || null) !== (orig.materialRequirement || null);

        if (changed) {
          await projectService.updateProjectFile(editProject.id, orig.id, {
            spoolmanFilamentId: newFilamentId,
            printCount: file.printCount,
            materialRequirement: file.materialRequirement,
          });
        }
      }
    },
    onSuccess: () => {
      resetForm();
      onSuccess();
    },
  });

  const activeMutation = isEditMode ? editMutation : createMutation;

  const resetForm = () => {
    setName('');
    setDescription('');
    setStatus(PrintProjectStatus.Open);
    setPriority(0);
    setDueDate('');
    setNotes('');
    setSelectedTemplate('');
    setSelectedFiles([]);
    setShowFilePicker(false);
    setGcodeFileCache({});
    setFilamentAssignments({});
  };

  const handleTemplateChange = async (templateId: string) => {
    setSelectedTemplate(templateId);
    if (!templateId) return;

    // Load template details and apply defaults
    try {
      const template = await templateService.getTemplate(templateId);
      if (!name.trim()) {
        setName(template.name);
      }
      if (!description.trim() && template.description) {
        setDescription(template.description);
      }
      if (template.defaultPriority) {
        setPriority(template.defaultPriority);
      }
      if (template.defaultNotes) {
        setNotes(template.defaultNotes);
      }
    } catch (err) {
      console.error('Failed to load template:', err);
    }
  };

  const handleClose = () => {
    resetForm();
    onClose();
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!name.trim()) return;

    if (isEditMode) {
      editMutation.mutate();
      return;
    }

    // Merge filament assignments into the file requests
    const filesWithFilaments = selectedFiles.map(f => ({
      ...f,
      spoolmanFilamentId: filamentAssignments[f.gcodeFileId] ?? undefined,
    }));

    const request: CreatePrintProjectRequest = {
      name: name.trim(),
      description: description.trim() || undefined,
      priority,
      dueDate: dueDate || undefined,
      notes: notes.trim() || undefined,
      files: filesWithFilaments.length > 0 ? filesWithFilaments : undefined,
    };

    createMutation.mutate(request);
  };

  const addFiles = (files: GcodeFile[]) => {
    const newEntries: AddFileToProjectRequest[] = [];
    const cacheUpdates: Record<string, GcodeFile> = {};
    for (const file of files) {
      if (selectedFiles.some(f => f.gcodeFileId === file.id)) continue;
      cacheUpdates[file.id] = file;
      newEntries.push({
        gcodeFileId: file.id,
        materialRequirement: file.extractedMaterial || undefined,
        printCount: 1,
      });
    }
    if (newEntries.length > 0) {
      setGcodeFileCache(prev => ({ ...prev, ...cacheUpdates }));
      setSelectedFiles(prev => [...prev, ...newEntries]);
    }
    setShowFilePicker(false);
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
      title={isEditMode ? 'Edit Project' : 'Create New Project'}
      size="full"
      footer={
        <div className="flex gap-3 w-full justify-end">
          <Button variant="secondary" onClick={handleClose} disabled={activeMutation.isPending}>
            Cancel
          </Button>
          <Button
            variant="primary"
            onClick={handleSubmit}
            disabled={!name.trim() || activeMutation.isPending}
          >
            {activeMutation.isPending
              ? (isEditMode ? 'Saving...' : 'Creating...')
              : (isEditMode ? 'Save Changes' : 'Create Project')}
          </Button>
        </div>
      }
    >
      <form onSubmit={handleSubmit} className="space-y-4">
        {/* Template selector (create mode only) */}
        {!isEditMode && templates.length > 0 && (
          <div>
            <label className="block text-sm font-medium text-pf-text-primary mb-1">
              Start from Template
            </label>
            <Select
              value={selectedTemplate}
              onChange={(e) => handleTemplateChange(e.target.value)}
              className="bg-pf-bg-2 border-pf-border rounded-lg! px-3! py-2!"
            >
              <option value="">-- No template (blank project) --</option>
              {templates.map((template: PrintProjectTemplateListDto) => (
                <option key={template.id} value={template.id}>
                  {template.name}
                  {template.category ? ` (${template.category})` : ''}
                  {template.fileCount > 0 ? ` - ${template.fileCount} files` : ''}
                </option>
              ))}
            </Select>
            <p className="mt-1 text-xs text-pf-text-tertiary">
              Templates pre-fill project details and expected file entries
            </p>
          </div>
        )}

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
          <Textarea
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="Brief description of the project..."
            rows={2}
            className="w-full bg-pf-bg-2 border-pf-border rounded-lg! px-3! py-2! resize-none! min-h-0!"
          />
        </div>

        {/* Priority, Status, and Due Date row */}
        <div className={`grid gap-4 ${isEditMode ? 'grid-cols-3' : 'grid-cols-2'}`}>
          <div>
            <label className="block text-sm font-medium text-pf-text-primary mb-1">
              Priority
            </label>
            <Select
              value={priority}
              onChange={(e) => setPriority(Number(e.target.value))}
              className="bg-pf-bg-2 border-pf-border rounded-lg! px-3! py-2!"
            >
              <option value={0}>Normal</option>
              <option value={1}>High</option>
              <option value={2}>Urgent</option>
              <option value={-1}>Low</option>
            </Select>
          </div>

          {/* Status (edit mode only) */}
          {isEditMode && (
            <div>
              <label className="block text-sm font-medium text-pf-text-primary mb-1">
                Status
              </label>
              <Select
                value={status}
                onChange={(e) => setStatus(e.target.value as PrintProjectStatus)}
                className="bg-pf-bg-2 border-pf-border rounded-lg! px-3! py-2!"
              >
                <option value="Open">Open</option>
                <option value="InProgress">In Progress</option>
                <option value="OnHold">On Hold</option>
                <option value="Completed">Completed</option>
                <option value="Cancelled">Cancelled</option>
              </Select>
            </div>
          )}

          <div>
            <label htmlFor="project-due-date" className="block text-sm font-medium text-pf-text-primary mb-1">
              Due Date
            </label>
            <input
              id="project-due-date"
              type="date"
              value={dueDate}
              onChange={(e) => setDueDate(e.target.value)}
              className="w-full px-3 py-2 bg-pf-bg-2 border border-pf-border rounded-lg text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent"
            />
          </div>
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
              onClick={() => setShowFilePicker(true)}
              iconLeft={<PlusIcon className="w-4 h-4" />}
            >
              Add Files
            </Button>
          </div>

          {/* File Picker Modal */}
          <FilePickerModal
            isOpen={showFilePicker}
            onClose={() => setShowFilePicker(false)}
            onSelect={addFiles}
            excludeIds={selectedFiles.map(f => f.gcodeFileId)}
            title="Add Files to Project"
          />

          {/* Selected Files Table */}
          {selectedFiles.length > 0 ? (
            <div className="bg-pf-bg-1 rounded-lg border border-pf-border overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-pf-border bg-pf-bg-2">
                    <th className="px-3 py-2 text-left font-medium text-pf-text-primary">File</th>
                    <th className="px-3 py-2 text-left font-medium text-pf-text-primary">Material</th>
                    <th className="px-3 py-2 text-left font-medium text-pf-text-primary">Filament</th>
                    <th className="px-3 py-2 text-center font-medium text-pf-text-primary w-16">Qty</th>
                    <th className="px-3 py-2 text-right font-medium text-pf-text-primary w-24">Est. Cost</th>
                    <th className="px-3 py-2 w-10"><span className="sr-only">Actions</span></th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-pf-border">
                  {selectedFiles.map((file) => {
                    const gcodeFile = gcodeFileCache[file.gcodeFileId];
                    const fileName = gcodeFile?.name || gcodeFile?.fileName || file.gcodeFileId;
                    const material = gcodeFile?.extractedMaterial || file.materialRequirement || '—';
                    const assignedFilamentId = filamentAssignments[file.gcodeFileId];
                    const assignedFilament = assignedFilamentId ? filamentsById.get(assignedFilamentId) : undefined;
                    const filamentLengthMm = gcodeFile?.extractedFilamentLength;
                    const estimatedCost = computeEstimatedCost(assignedFilament, filamentLengthMm, file.printCount);

                    return (
                      <tr key={file.gcodeFileId}>
                        {/* Thumbnail + File name */}
                        <td className="px-3 py-2">
                          <div className="flex items-center gap-2">
                            {gcodeFile?.thumbnailUrl ? (
                              <img
                                src={gcodeFile.thumbnailUrl}
                                alt=""
                                className="w-8 h-8 rounded object-cover bg-pf-bg-2 shrink-0"
                              />
                            ) : (
                              <div className="w-8 h-8 rounded bg-pf-bg-2 flex items-center justify-center text-pf-text-tertiary shrink-0">
                                <span className="text-[10px]">GC</span>
                              </div>
                            )}
                            <p className="text-pf-text-primary truncate max-w-65" title={fileName}>
                              {fileName}
                            </p>
                          </div>
                        </td>

                        {/* Material from gcode metadata */}
                        <td className="px-3 py-2 text-pf-text-secondary">
                          {material}
                        </td>

                        {/* Filament selector */}
                        <td className="px-3 py-2">
                          <FilamentSelector
                            filaments={filaments}
                            materialFilter={gcodeFile?.extractedMaterial || undefined}
                            selectedFilamentId={assignedFilamentId}
                            onChange={(filamentId) => {
                              setFilamentAssignments(prev => {
                                const next = { ...prev };
                                if (filamentId) {
                                  next[file.gcodeFileId] = filamentId;
                                } else {
                                  delete next[file.gcodeFileId];
                                }
                                return next;
                              });
                              // Auto-fill material requirement from filament
                              if (filamentId) {
                                const filament = filamentsById.get(filamentId);
                                if (filament?.material) {
                                  updateFileSettings(file.gcodeFileId, { materialRequirement: filament.material });
                                }
                              }
                            }}
                          />
                        </td>

                        {/* Print count */}
                        <td className="px-3 py-2 text-center">
                          <input
                            type="number"
                            min={1}
                            value={file.printCount}
                            onChange={(e) => updateFileSettings(file.gcodeFileId, { printCount: Math.max(1, parseInt(e.target.value) || 1) })}
                            className="w-14 px-2 py-1 text-xs text-center bg-pf-bg-2 border border-pf-border rounded text-pf-text-primary"
                            aria-label={`Print count for ${fileName}`}
                          />
                        </td>

                        {/* Estimated cost */}
                        <td className="px-3 py-2 text-right text-pf-text-secondary whitespace-nowrap">
                          {estimatedCost !== null ? (
                            <span title="Estimated material cost based on spool price and filament usage">
                              ${estimatedCost.toFixed(2)}
                            </span>
                          ) : (
                            <span className="text-pf-text-tertiary">—</span>
                          )}
                        </td>

                        {/* Remove */}
                        <td className="px-3 py-2 text-center">
                          <Button
                            type="button"
                            variant="subtle"
                            size="sm"
                            onClick={() => removeFile(file.gcodeFileId)}
                            className="p-1! text-pf-text-tertiary hover:text-pf-error"
                            title={`Remove ${fileName}`}
                          >
                            <DeleteIcon className="w-4 h-4" />
                          </Button>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          ) : (
            <p className="text-sm text-pf-text-tertiary py-2">
              No files added yet. You can add files now or after creating the project.
            </p>
          )}
        </div>

        {activeMutation.isError && (
          <p className="text-sm text-pf-error">
            Failed to {isEditMode ? 'update' : 'create'} project: {String(activeMutation.error)}
          </p>
        )}
      </form>
    </Modal>
  );
};

/**
 * Estimate material cost from filament price and gcode filament usage.
 * Uses filament length (mm) and filament weight to derive per-unit cost.
 */
function computeEstimatedCost(
  filament: SpoolmanFilament | undefined,
  filamentLengthMm: number | undefined,
  printCount: number | undefined,
): number | null {
  if (!filament?.price || !filamentLengthMm || filamentLengthMm <= 0) return null;
  const count = Math.max(1, printCount ?? 1);

  // Use filament weight (g per spool) to estimate total length
  // Standard 1.75mm diameter filament density ~2.98 g/m for PLA
  const weight = filament.weight; // grams per spool
  if (weight && weight > 0) {
    const gramsPerMeter = 2.98; // approximate for 1.75mm filament
    const totalLengthM = weight / gramsPerMeter;
    const totalLengthMm = totalLengthM * 1000;
    const costPerMm = filament.price / totalLengthMm;
    return costPerMm * filamentLengthMm * count;
  }

  return null;
}

/** Compact filament selector with color swatch, filtered by material type */
interface FilamentSelectorProps {
  filaments: SpoolmanFilament[];
  /** When set, filaments are filtered to match this material (case-insensitive). */
  materialFilter?: string;
  selectedFilamentId?: number;
  onChange: (filamentId: number | null) => void;
}

const FilamentSelector: React.FC<FilamentSelectorProps> = ({ filaments, materialFilter, selectedFilamentId, onChange }) => {
  const selectedFilament = selectedFilamentId ? filaments.find(f => f.id === selectedFilamentId) : undefined;

  // Filter filaments by material type
  const availableFilaments = useMemo(() => {
    if (!materialFilter) return filaments;
    const needle = materialFilter.toLowerCase();
    return filaments.filter(f => f.material?.toLowerCase() === needle);
  }, [filaments, materialFilter]);

  if (availableFilaments.length === 0) {
    return <span className="text-xs text-pf-text-tertiary italic">No filaments</span>;
  }

  return (
    <div className="flex items-center gap-1.5">
      {selectedFilament?.colorHex && (
        <ColorSwatch
          color={`#${selectedFilament.colorHex.replace('#', '')}`}
          label={selectedFilament.material ?? undefined}
          className="shrink-0"
        />
      )}
      <Select
        value={selectedFilamentId ?? ''}
        onChange={(e) => onChange(e.target.value ? Number(e.target.value) : null)}
        className="bg-pf-bg-2 border-pf-border rounded px-2! py-1! text-xs! w-auto!"
        containerClassName="w-auto!"
        title="Assign filament type"
      >
        <option value="">— None —</option>
        {availableFilaments.map(filament => (
          <option key={filament.id} value={filament.id}>
            {filament.name ?? 'Unnamed'}
            {filament.material ? ` (${filament.material})` : ''}
            {filament.vendor ? ` — ${filament.vendor}` : ''}
          </option>
        ))}
      </Select>
    </div>
  );
};

export default CreateProjectModal;
