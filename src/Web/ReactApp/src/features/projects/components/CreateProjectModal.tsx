import React, { useState, useMemo, useEffect } from 'react';
import { useQuery, useMutation } from '@tanstack/react-query';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Select, Textarea } from '@/common/components/ui';
import { 
  PlusIcon, 
  DeleteIcon,
  SearchIcon,
} from '@/common/components/icons/MdiIcons';
import { ColorSwatch } from '@/features/catalog/components/ColorSwatch';
import { projectService } from '@/services/projectService';
import { templateService } from '@/services/templateService';
import { apiClient } from '@/services/api';
import type { 
  CreatePrintProjectRequest, 
  AddFileToProjectRequest,
  GcodeFile,
  PrintProjectDetailDto,
  PrintProjectStatus,
  PrintProjectTemplateListDto,
  SpoolmanSpool,
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
  const [status, setStatus] = useState<PrintProjectStatus>('Open');
  const [priority, setPriority] = useState(0);
  const [dueDate, setDueDate] = useState('');
  const [notes, setNotes] = useState('');
  const [selectedTemplate, setSelectedTemplate] = useState<string>('');
  const [selectedFiles, setSelectedFiles] = useState<AddFileToProjectRequest[]>([]);
  const [showFilePicker, setShowFilePicker] = useState(false);
  const [fileSearch, setFileSearch] = useState('');
  // Cache gcode file metadata so it persists after picker closes
  const [gcodeFileCache, setGcodeFileCache] = useState<Record<string, GcodeFile>>({});
  // Track which spool is assigned to each file (keyed by gcodeFileId)
  const [spoolAssignments, setSpoolAssignments] = useState<Record<string, number>>({});

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

        if (f.spoolmanSpoolId) {
          assignments[f.gcodeFileId] = f.spoolmanSpoolId;
        }
      }

      setGcodeFileCache(cache);
      setSpoolAssignments(assignments);
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

  // Fetch available spools from Spoolman
  const { data: spools = [] } = useQuery({
    queryKey: ['spoolman-spools-for-project'],
    queryFn: () => apiClient.getSpools(),
    staleTime: 60 * 1000,
  });

  // Index spools by ID for quick lookup
  const spoolsById = useMemo(() => {
    const map = new Map<number, SpoolmanSpool>();
    for (const spool of spools) {
      map.set(spool.id, spool);
    }
    return map;
  }, [spools]);

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
            spoolmanSpoolId: spoolAssignments[file.gcodeFileId] ?? undefined,
          });
        }
      }

      // Update existing files that changed
      for (const file of selectedFiles) {
        const orig = originalMap.get(file.gcodeFileId);
        if (!orig) continue; // new file, already handled above

        const newSpoolId = spoolAssignments[file.gcodeFileId] ?? null;
        const changed =
          file.printCount !== orig.printCount ||
          newSpoolId !== orig.spoolmanSpoolId ||
          (file.materialRequirement || null) !== (orig.materialRequirement || null);

        if (changed) {
          await projectService.updateProjectFile(editProject.id, orig.id, {
            spoolmanSpoolId: newSpoolId,
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
    setStatus('Open');
    setPriority(0);
    setDueDate('');
    setNotes('');
    setSelectedTemplate('');
    setSelectedFiles([]);
    setShowFilePicker(false);
    setFileSearch('');
    setGcodeFileCache({});
    setSpoolAssignments({});
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

    // Merge spool assignments into the file requests
    const filesWithSpools = selectedFiles.map(f => ({
      ...f,
      spoolmanSpoolId: spoolAssignments[f.gcodeFileId] ?? undefined,
    }));

    const request: CreatePrintProjectRequest = {
      name: name.trim(),
      description: description.trim() || undefined,
      priority,
      dueDate: dueDate || undefined,
      notes: notes.trim() || undefined,
      files: filesWithSpools.length > 0 ? filesWithSpools : undefined,
    };

    createMutation.mutate(request);
  };

  const addFile = (file: GcodeFile) => {
    if (selectedFiles.some(f => f.gcodeFileId === file.id)) return;
    
    // Cache the full gcode file object so metadata persists after picker closes
    setGcodeFileCache(prev => ({ ...prev, [file.id]: file }));
    setSelectedFiles([
      ...selectedFiles,
      {
        gcodeFileId: file.id,
        materialRequirement: file.extractedMaterial || undefined,
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
              className="bg-pf-bg-2 border-pf-border !rounded-lg !px-3 !py-2"
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
            className="w-full bg-pf-bg-2 border-pf-border !rounded-lg !px-3 !py-2 !resize-none !min-h-0"
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
              className="bg-pf-bg-2 border-pf-border !rounded-lg !px-3 !py-2"
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
                className="bg-pf-bg-2 border-pf-border !rounded-lg !px-3 !py-2"
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
                      <Button
                        key={file.id}
                        type="button"
                        onClick={() => addFile(file)}
                        variant="unstyled"
                        className="w-full text-left px-3 py-2 rounded hover:bg-pf-bg-1 text-sm text-pf-text-primary enabled:cursor-pointer focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent"
                      >
                        <span className="flex items-center gap-2">
                          <span className="truncate">{file.name || file.fileName}</span>
                          {file.extractedMaterial && (
                            <span className="shrink-0 text-xs text-pf-text-tertiary">
                              {file.extractedMaterial}
                            </span>
                          )}
                        </span>
                      </Button>
                    ))
                )}
              </div>
            </div>
          )}

          {/* Selected Files Table */}
          {selectedFiles.length > 0 ? (
            <div className="bg-pf-bg-1 rounded-lg border border-pf-border overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-pf-border bg-pf-bg-2">
                    <th className="px-3 py-2 text-left font-medium text-pf-text-primary">File</th>
                    <th className="px-3 py-2 text-left font-medium text-pf-text-primary">Material</th>
                    <th className="px-3 py-2 text-left font-medium text-pf-text-primary">Spool</th>
                    <th className="px-3 py-2 text-center font-medium text-pf-text-primary w-16">Qty</th>
                    <th className="px-3 py-2 text-right font-medium text-pf-text-primary w-24">Est. Cost</th>
                    <th className="px-3 py-2 w-10"><span className="sr-only">Actions</span></th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-pf-border">
                  {selectedFiles.map((file) => {
                    const gcodeFile = gcodeFiles.find(f => f.id === file.gcodeFileId) 
                      || gcodeFileCache[file.gcodeFileId];
                    const fileName = gcodeFile?.name || gcodeFile?.fileName || file.gcodeFileId;
                    const material = gcodeFile?.extractedMaterial || file.materialRequirement || '—';
                    const assignedSpoolId = spoolAssignments[file.gcodeFileId];
                    const assignedSpool = assignedSpoolId ? spoolsById.get(assignedSpoolId) : undefined;
                    const filamentLengthMm = gcodeFile?.extractedFilamentLength;
                    const estimatedCost = computeEstimatedCost(assignedSpool, filamentLengthMm, file.printCount);

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
                            <p className="text-pf-text-primary truncate max-w-[260px]" title={fileName}>
                              {fileName}
                            </p>
                          </div>
                        </td>

                        {/* Material from gcode metadata */}
                        <td className="px-3 py-2 text-pf-text-secondary">
                          {material}
                        </td>

                        {/* Spool selector */}
                        <td className="px-3 py-2">
                          <SpoolSelector
                            spools={spools}
                            materialFilter={gcodeFile?.extractedMaterial || undefined}
                            selectedSpoolId={assignedSpoolId}
                            onChange={(spoolId) => {
                              setSpoolAssignments(prev => {
                                const next = { ...prev };
                                if (spoolId) {
                                  next[file.gcodeFileId] = spoolId;
                                } else {
                                  delete next[file.gcodeFileId];
                                }
                                return next;
                              });
                              // Auto-fill material requirement from spool
                              if (spoolId) {
                                const spool = spoolsById.get(spoolId);
                                if (spool?.material) {
                                  updateFileSettings(file.gcodeFileId, { materialRequirement: spool.material });
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
                            className="!p-1 text-pf-text-tertiary hover:text-pf-error"
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
 * Estimate material cost from spool price and gcode filament usage.
 * Uses filament length (mm) and spool weight/length to derive per-unit cost.
 */
function computeEstimatedCost(
  spool: SpoolmanSpool | undefined,
  filamentLengthMm: number | undefined,
  printCount: number | undefined,
): number | null {
  if (!spool?.price || !filamentLengthMm || filamentLengthMm <= 0) return null;
  const count = Math.max(1, printCount ?? 1);

  // If spool has remaining/used length data, use total length for cost-per-mm
  // Total spool length ≈ remaining + used (or estimate from initial weight assuming ~1.24 g/cm³ PLA density, 1.75mm dia → ~2.98 g/m)
  let totalLengthMm: number | null = null;
  if (spool.remainingLengthMm && spool.usedLengthMm) {
    totalLengthMm = spool.remainingLengthMm + spool.usedLengthMm;
  } else if (spool.remainingLengthMm && spool.initialWeightG && spool.remainingWeightG && spool.remainingWeightG > 0) {
    // Estimate total length from weight ratio
    totalLengthMm = spool.remainingLengthMm * (spool.initialWeightG / spool.remainingWeightG);
  }

  if (totalLengthMm && totalLengthMm > 0) {
    const costPerMm = spool.price / totalLengthMm;
    return costPerMm * filamentLengthMm * count;
  }

  // Fallback: if we have initial weight, estimate using standard 1.75mm filament density
  // ~2.98 g/m for PLA (conservative estimate works for most materials)
  if (spool.initialWeightG && spool.initialWeightG > 0) {
    const gramsPerMeter = 2.98; // approximate for 1.75mm filament
    const totalLengthM = spool.initialWeightG / gramsPerMeter;
    const totalLengthEstMm = totalLengthM * 1000;
    const costPerMm = spool.price / totalLengthEstMm;
    return costPerMm * filamentLengthMm * count;
  }

  return null;
}

/** Compact spool selector with color swatch, filtered by material type */
interface SpoolSelectorProps {
  spools: SpoolmanSpool[];
  /** When set, spools are filtered to match this material (case-insensitive). */
  materialFilter?: string;
  selectedSpoolId?: number;
  onChange: (spoolId: number | null) => void;
}

const SpoolSelector: React.FC<SpoolSelectorProps> = ({ spools, materialFilter, selectedSpoolId, onChange }) => {
  const selectedSpool = selectedSpoolId ? spools.find(s => s.id === selectedSpoolId) : undefined;

  // Only show non-archived spools with remaining material, filtered by material type
  const availableSpools = useMemo(() => {
    const base = spools.filter(s => !s.archived && s.remainingWeightG !== 0);
    if (!materialFilter) return base;
    const needle = materialFilter.toLowerCase();
    return base.filter(s => s.material?.toLowerCase() === needle);
  }, [spools, materialFilter]);

  if (availableSpools.length === 0) {
    return <span className="text-xs text-pf-text-tertiary italic">No spools</span>;
  }

  return (
    <div className="flex items-center gap-1.5">
      {selectedSpool?.colorHex && (
        <ColorSwatch
          color={`#${selectedSpool.colorHex.replace('#', '')}`}
          label={selectedSpool.material}
          className="shrink-0"
        />
      )}
      <Select
        value={selectedSpoolId ?? ''}
        onChange={(e) => onChange(e.target.value ? Number(e.target.value) : null)}
        className="bg-pf-bg-2 border-pf-border rounded !px-2 !py-1 !text-xs !w-auto"
        containerClassName="!w-auto"
        title="Assign filament spool"
      >
        <option value="">— None —</option>
        {availableSpools.map(spool => (
          <option key={spool.id} value={spool.id}>
            {spool.filamentName || spool.name}
            {spool.material ? ` (${spool.material})` : ''}
            {spool.vendor ? ` — ${spool.vendor}` : ''}
            {spool.remainingPercent != null ? ` [${Math.round(spool.remainingPercent)}%]` : ''}
          </option>
        ))}
      </Select>
    </div>
  );
};

export default CreateProjectModal;
