/**
 * TaskCatalogTab Component
 *
 * Displays the global maintenance task catalog. Tasks exist independently
 * of any plan and can be filtered by category, searched, and managed with
 * full CRUD. Each task shows scope rules indicating which printer features
 * require it.
 */

import React, { useRef, useMemo, useState } from 'react';
import { toast } from 'sonner';
import { Badge, Button } from '@/common/components/ui';
import { FileUpload } from '@/common/components/ui/FileUpload';
import { Input } from '@/common/components/ui/Input';
import { Select } from '@/common/components/ui/Select';
import { Textarea } from '@/common/components/ui/Textarea';
import { Checkbox } from '@/common/components/ui/Checkbox';
import { Modal } from '@/common/components/modals/Modal';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import {
  EditIcon,
  DeleteIcon,
  PlusIcon,
  SearchIcon,
  FilterIcon,
  DownloadIcon,
  UploadIcon,
  CopyIcon,
} from '@/common/components/icons/MdiIcons';
import {
  useTaskCatalog,
  useTaskCategories,
  useCreateCatalogTask,
  useUpdateCatalogTask,
  useDeleteCatalogTask,
  useAddCatalogTaskComponent,
  useRemoveCatalogTaskComponent,
  useExportTasks,
  useImportTasks,
} from '../hooks/useTaskCatalog';
import { useMaintenanceComponents } from '../hooks/useMaintenanceComponents';
import type {
  MaintenanceTaskDto,
  MaintenanceTaskComponentDto,
  MaintenanceComponentDto,
  CreateMaintenanceTaskDto,
  UpdateMaintenanceTaskDto,
  MaintenanceExportEnvelope,
} from '@/types/maintenance';

// ──────────────────────── Helpers ────────────────────────

function priorityLabel(p: number): string {
  switch (p) {
    case 1: return 'Low';
    case 2: return 'Medium';
    case 3: return 'High';
    case 4: return 'Critical';
    default: return `P${p}`;
  }
}

function priorityVariant(p: number): 'default' | 'success' | 'warning' | 'error' {
  switch (p) {
    case 1: return 'default';
    case 2: return 'success';
    case 3: return 'warning';
    case 4: return 'error';
    default: return 'default';
  }
}

function intervalText(task: MaintenanceTaskDto): string {
  if (task.intervalHours != null) return `Every ${task.intervalHours}h`;
  if (task.intervalDays != null) return `Every ${task.intervalDays}d`;
  return 'Manual';
}

/** Labels for all scope rule boolean flags */
const SCOPE_RULES: { key: keyof MaintenanceTaskDto; label: string }[] = [
  { key: 'requiresEnclosure', label: 'Enclosure' },
  { key: 'requiresCarbonFilter', label: 'Carbon filter' },
  { key: 'requiresHepaFilter', label: 'HEPA filter' },
  { key: 'requiresBowdenTube', label: 'Bowden tube' },
  { key: 'requiresPtfeLiner', label: 'PTFE liner' },
  { key: 'requiresLinearRails', label: 'Linear rails' },
  { key: 'requiresLeadScrews', label: 'Lead screws' },
  { key: 'requiresToolchanger', label: 'Toolchanger' },
  { key: 'requiresFilamentCutter', label: 'Filament cutter' },
  { key: 'requiresHeatedChamber', label: 'Heated chamber' },
  { key: 'requiresHeatedBed', label: 'Heated bed' },
  { key: 'requiresMultiMaterial', label: 'Multi-material' },
];

const priorityOptions = [
  { value: '1', label: 'Low' },
  { value: '2', label: 'Medium' },
  { value: '3', label: 'High' },
  { value: '4', label: 'Critical' },
];

// ──────────────────────── Task Form Modal ────────────────────────

interface TaskFormModalProps {
  isOpen: boolean;
  taskId: string | null;
  tasks: MaintenanceTaskDto[];
  categories: string[];
  cloneSource?: MaintenanceTaskDto | null;
  onClose: () => void;
  onTaskCreated?: (taskId: string) => void;
}

function TaskFormModal({ isOpen, taskId, tasks, categories, cloneSource, onClose, onTaskCreated }: TaskFormModalProps) {
  // Derive fresh task from the query-backed array so part mutations auto-refresh
  const task = taskId ? tasks.find(t => t.id === taskId) ?? null : null;
  const isEdit = !!task;
  const source = task ?? cloneSource;

  // Task CRUD mutations
  const createTask = useCreateCatalogTask();
  const updateTask = useUpdateCatalogTask();

  // Parts data & mutations
  const { data: allComponents = [] } = useMaintenanceComponents();
  const addComponent = useAddCatalogTaskComponent();
  const removeComponent = useRemoveCatalogTaskComponent();

  // ── Form state ──
  const [taskName, setTaskName] = useState('');
  const [category, setCategory] = useState('');
  const [customCategory, setCustomCategory] = useState('');
  const [description, setDescription] = useState('');
  const [intervalType, setIntervalType] = useState<'hours' | 'days' | 'none'>('hours');
  const [intervalValue, setIntervalValue] = useState('');
  const [estimatedMinutes, setEstimatedMinutes] = useState('');
  const [priority, setPriority] = useState('2');
  const [isActive, setIsActive] = useState(true);
  const [isDefault, setIsDefault] = useState(false);
  const [scopeRules, setScopeRules] = useState<Record<string, boolean | null>>({});
  const [isSubmitting, setIsSubmitting] = useState(false);

  // ── Parts state ──
  const [removingTc, setRemovingTc] = useState<MaintenanceTaskComponentDto | null>(null);
  const [partSearch, setPartSearch] = useState('');
  const [selectedPartId, setSelectedPartId] = useState('');
  const [partQuantity, setPartQuantity] = useState('1');
  const [partNotes, setPartNotes] = useState('');
  const [isAddingPart, setIsAddingPart] = useState(false);

  // ── Derived data ──
  const componentMap = useMemo(() => {
    const map = new Map<string, MaintenanceComponentDto>();
    for (const c of allComponents) map.set(c.id, c);
    return map;
  }, [allComponents]);

  const existingComponentIds = useMemo(
    () => new Set(task?.taskComponents.map(tc => tc.maintenanceComponentId) ?? []),
    [task?.taskComponents],
  );

  const availableParts = useMemo(() => {
    const q = partSearch.toLowerCase();
    return allComponents.filter(c => {
      if (existingComponentIds.has(c.id)) return false;
      if (q && !c.name.toLowerCase().includes(q) && !c.category.toLowerCase().includes(q) && !(c.sku?.toLowerCase().includes(q))) return false;
      return true;
    });
  }, [allComponents, existingComponentIds, partSearch]);

  const prevOpenRef = useRef(false);

  /* Init form state when modal opens (or task switches from create→edit) */
  React.useEffect(() => {
    if (isOpen && !prevOpenRef.current) {
      setTaskName(cloneSource ? `${cloneSource.taskName} (Copy)` : (source?.taskName ?? ''));
      setCategory(source?.category ?? (categories[0] ?? ''));
      setCustomCategory('');
      setDescription(source?.description ?? '');
      if (source?.intervalHours != null) {
        setIntervalType('hours');
        setIntervalValue(String(source.intervalHours));
      } else if (source?.intervalDays != null) {
        setIntervalType('days');
        setIntervalValue(String(source.intervalDays));
      } else {
        setIntervalType('none');
        setIntervalValue('');
      }
      setEstimatedMinutes(source?.estimatedDurationMinutes != null ? String(source.estimatedDurationMinutes) : '');
      setPriority(String(source?.priority ?? 2));
      setIsActive(cloneSource ? true : (source?.isActive ?? true));
      setIsDefault(source?.isDefault ?? false);
      const rules: Record<string, boolean | null> = {};
      for (const rule of SCOPE_RULES) {
        const val = source?.[rule.key];
        rules[rule.key] = typeof val === 'boolean' ? val : null;
      }
      setScopeRules(rules);
      // Reset parts picker state
      setRemovingTc(null);
      setPartSearch('');
      setSelectedPartId('');
      setPartQuantity('1');
      setPartNotes('');
    }
    prevOpenRef.current = isOpen;
  }, [isOpen, task, cloneSource]); // eslint-disable-line react-hooks/exhaustive-deps

  // ── Task form submit ──
  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    const resolvedCategory = category === '__custom__' ? customCategory.trim() : category;
    if (!taskName.trim() || !resolvedCategory) return;

    setIsSubmitting(true);
    try {
      const data: CreateMaintenanceTaskDto | UpdateMaintenanceTaskDto = {
        taskName: taskName.trim(),
        category: resolvedCategory,
        description: description.trim() || undefined,
        intervalHours: intervalType === 'hours' && intervalValue ? Number(intervalValue) : undefined,
        intervalDays: intervalType === 'days' && intervalValue ? Number(intervalValue) : undefined,
        estimatedDurationMinutes: estimatedMinutes ? Number(estimatedMinutes) : undefined,
        priority: Number(priority),
        isActive,
        isDefault,
        ...Object.fromEntries(
          SCOPE_RULES.map(r => [r.key, scopeRules[r.key] ?? null])
        ),
      };

      if (isEdit && task) {
        await updateTask.mutateAsync({ id: task.id, data: data as UpdateMaintenanceTaskDto });
        toast.success('Task updated');
        onClose();
      } else {
        const newTask = await createTask.mutateAsync(data as CreateMaintenanceTaskDto);
        toast.success('Task created — add required parts below');
        onTaskCreated?.(newTask.id);
      }
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to save task');
    } finally {
      setIsSubmitting(false);
    }
  };

  // ── Part handlers ──
  const handleAddPart = async () => {
    if (!selectedPartId || !task) return;
    setIsAddingPart(true);
    try {
      await addComponent.mutateAsync({
        taskId: task.id,
        data: {
          componentId: selectedPartId,
          quantity: Math.max(1, Number(partQuantity) || 1),
          notes: partNotes.trim() || null,
        },
      });
      const part = allComponents.find(c => c.id === selectedPartId);
      toast.success(`Added "${part?.name ?? 'part'}"`);
      setSelectedPartId('');
      setPartQuantity('1');
      setPartNotes('');
      setPartSearch('');
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to add part');
    } finally {
      setIsAddingPart(false);
    }
  };

  const handleRemoveConfirm = async () => {
    if (!removingTc || !task) return;
    try {
      await removeComponent.mutateAsync({
        taskId: task.id,
        componentId: removingTc.maintenanceComponentId,
      });
      toast.success(`Removed "${removingTc.componentName ?? 'part'}"`);
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to remove part');
    } finally {
      setRemovingTc(null);
    }
  };

  return (
    <>
      <Modal isOpen={isOpen} onClose={onClose} title={isEdit ? `Edit "${task!.taskName}"` : cloneSource ? 'Clone Task' : 'New Catalog Task'} size="full">
        <div className="max-h-[80vh] overflow-y-auto space-y-6 pr-1">
          {/* ── Task Details Form ── */}
          <form onSubmit={handleSubmit} className="space-y-4">
            {/* Name */}
            <div>
              <label htmlFor="task-name" className="block text-sm font-medium text-pf-text-secondary mb-1">
                Name <span className="text-pf-error">*</span>
              </label>
              <Input id="task-name" value={taskName} onChange={e => setTaskName(e.target.value)} placeholder="e.g. Clean nozzle" required maxLength={200} />
            </div>

            {/* Category */}
            <div>
              <label htmlFor="task-category" className="block text-sm font-medium text-pf-text-secondary mb-1">
                Category <span className="text-pf-error">*</span>
              </label>
              <Select id="task-category" value={category} onChange={e => setCategory(e.target.value)}>
                {categories.map(c => (
                  <option key={c} value={c}>{c}</option>
                ))}
                <option value="__custom__">+ New category…</option>
              </Select>
              {category === '__custom__' && (
                <Input className="mt-2" value={customCategory} onChange={e => setCustomCategory(e.target.value)} placeholder="Enter new category name" required maxLength={100} />
              )}
            </div>

            {/* Description */}
            <div>
              <label htmlFor="task-desc" className="block text-sm font-medium text-pf-text-secondary mb-1">Description</label>
              <Textarea id="task-desc" value={description} onChange={e => setDescription(e.target.value)} placeholder="What does this task involve?" rows={2} maxLength={1000} />
            </div>

            {/* Interval */}
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label htmlFor="interval-type" className="block text-sm font-medium text-pf-text-secondary mb-1">Interval type</label>
                <Select id="interval-type" value={intervalType} onChange={e => setIntervalType(e.target.value as 'hours' | 'days' | 'none')}>
                  <option value="hours">Print hours</option>
                  <option value="days">Calendar days</option>
                  <option value="none">Manual only</option>
                </Select>
              </div>
              {intervalType !== 'none' && (
                <div>
                  <label htmlFor="interval-val" className="block text-sm font-medium text-pf-text-secondary mb-1">
                    {intervalType === 'hours' ? 'Hours' : 'Days'}
                  </label>
                  <Input id="interval-val" type="number" min="1" value={intervalValue} onChange={e => setIntervalValue(e.target.value)} placeholder={intervalType === 'hours' ? '500' : '90'} />
                </div>
              )}
            </div>

            {/* Estimated duration + Priority */}
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label htmlFor="est-min" className="block text-sm font-medium text-pf-text-secondary mb-1">Estimated (min)</label>
                <Input id="est-min" type="number" min="1" value={estimatedMinutes} onChange={e => setEstimatedMinutes(e.target.value)} placeholder="15" />
              </div>
              <div>
                <label htmlFor="task-priority" className="block text-sm font-medium text-pf-text-secondary mb-1">Priority</label>
                <Select id="task-priority" value={priority} onChange={e => setPriority(e.target.value)}>
                  {priorityOptions.map(o => (
                    <option key={o.value} value={o.value}>{o.label}</option>
                  ))}
                </Select>
              </div>
            </div>

            {/* Flags */}
            <div className="flex gap-6">
              <Checkbox label="Active" checked={isActive} onChange={e => setIsActive(e.target.checked)} />
              <Checkbox label="Default (seed task)" checked={isDefault} onChange={e => setIsDefault(e.target.checked)} />
            </div>

            {/* Scope Rules */}
            <fieldset className="border border-pf-border rounded-lg p-3">
              <legend className="text-sm font-medium text-pf-text-secondary px-2">Scope Rules (applies to printers with…)</legend>
              <div className="grid grid-cols-2 sm:grid-cols-3 gap-2 mt-1">
                {SCOPE_RULES.map(rule => {
                  const val = scopeRules[rule.key];
                  return (
                    <Checkbox
                      key={rule.key}
                      label={rule.label}
                      checked={val === true}
                      indeterminate={val === null}
                      onChange={() => {
                        setScopeRules(prev => ({
                          ...prev,
                          [rule.key]: prev[rule.key] === true ? null : true,
                        }));
                      }}
                    />
                  );
                })}
              </div>
              <p className="text-xs text-pf-text-muted mt-2">
                Checked = required. Unchecked/indeterminate = no constraint.
              </p>
            </fieldset>

            {/* Form Actions */}
            <div className="flex justify-end gap-2 pt-2">
              <Button type="button" variant="secondary" size="sm" onClick={onClose}>
                {isEdit ? 'Close' : 'Cancel'}
              </Button>
              <Button type="submit" variant="primary" size="sm" disabled={isSubmitting || !taskName.trim()}>
                {isSubmitting ? 'Saving…' : isEdit ? 'Save Changes' : 'Create Task'}
              </Button>
            </div>
          </form>

          {/* ── Divider ── */}
          <div className="border-t border-pf-border" />

          {/* ── Required Parts Section ── */}
          <section className="space-y-3 pb-2">
            <h3 className="text-sm font-semibold text-pf-text-secondary uppercase tracking-wide">
              Required Parts
            </h3>

            {!isEdit ? (
              <p className="text-sm text-pf-text-muted py-6 text-center border border-dashed border-pf-border rounded-lg">
                Save the task first to manage required parts.
              </p>
            ) : (
              <>
                {/* Summary */}
                <p className="text-sm text-pf-text-muted">
                  {task!.taskComponents.length === 0
                    ? 'No parts linked to this task yet. Add parts from your inventory below.'
                    : `${task!.taskComponents.length} part${task!.taskComponents.length !== 1 ? 's' : ''} required for this task.`}
                </p>

                {/* Parts Table */}
                {task!.taskComponents.length > 0 && (
                  <div className="overflow-x-auto rounded-lg border border-pf-border">
                    <table className="w-full text-sm" role="grid" aria-label="Required parts">
                      <thead>
                        <tr className="bg-pf-bg-3 text-left text-xs font-medium text-pf-text-secondary uppercase tracking-wide">
                          <th scope="col" className="px-3 py-2">Part</th>
                          <th scope="col" className="px-3 py-2">Category</th>
                          <th scope="col" className="px-3 py-2 text-right">Qty</th>
                          <th scope="col" className="px-3 py-2">SKU</th>
                          <th scope="col" className="px-3 py-2 text-right">Stock</th>
                          <th scope="col" className="px-3 py-2">Notes</th>
                          <th scope="col" className="px-3 py-2 w-10"><span className="sr-only">Actions</span></th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-pf-border">
                        {task!.taskComponents.map(tc => {
                          const comp = componentMap.get(tc.maintenanceComponentId);
                          const isLow = comp ? comp.inStock < comp.minimumStock : false;
                          return (
                            <tr key={tc.id} className="hover:bg-pf-bg-2/50 transition-colors">
                              <td className="px-3 py-2 font-medium text-pf-text-primary">
                                {tc.componentName ?? comp?.name ?? 'Unknown'}
                              </td>
                              <td className="px-3 py-2">
                                {comp && <Badge variant="default" className="text-[10px]">{comp.category}</Badge>}
                              </td>
                              <td className="px-3 py-2 text-right">{tc.quantity}</td>
                              <td className="px-3 py-2 text-pf-text-muted">{comp?.sku ?? '—'}</td>
                              <td className="px-3 py-2 text-right">
                                <span className={isLow ? 'text-pf-warning' : ''}>{comp?.inStock ?? '—'}</span>
                                {isLow && <Badge variant="warning" className="text-[10px] ml-1">Low</Badge>}
                              </td>
                              <td className="px-3 py-2 text-pf-text-muted italic text-xs">{tc.notes ?? '—'}</td>
                              <td className="px-3 py-2">
                                <Button
                                  variant="ghost"
                                  size="sm"
                                  onClick={() => setRemovingTc(tc)}
                                  aria-label={`Remove ${tc.componentName ?? 'part'}`}
                                  className="text-pf-error hover:text-pf-error"
                                >
                                  <DeleteIcon className="h-4 w-4" aria-hidden="true" />
                                </Button>
                              </td>
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>
                )}

                {/* Add Part Picker */}
                <div className="space-y-3 border border-pf-border rounded-lg p-3 bg-pf-bg-3">
                  <h4 className="text-sm font-medium text-pf-text-secondary flex items-center gap-1.5">
                    <PlusIcon className="h-4 w-4" aria-hidden="true" />
                    Add Part from Inventory
                  </h4>

                  <div className="relative">
                    <SearchIcon className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-pf-text-muted" aria-hidden="true" />
                    <Input
                      className="pl-9"
                      placeholder="Search parts…"
                      value={partSearch}
                      onChange={e => setPartSearch(e.target.value)}
                      aria-label="Search inventory parts"
                    />
                  </div>

                  {availableParts.length === 0 ? (
                    <p className="text-xs text-pf-text-muted py-2 text-center">
                      {allComponents.length === 0
                        ? 'No parts in inventory yet. Add parts in the Parts Inventory tab first.'
                        : 'No matching parts available (all may already be linked).'}
                    </p>
                  ) : (
                    <>
                      <Select
                        value={selectedPartId}
                        onChange={e => setSelectedPartId(e.target.value)}
                        aria-label="Select part to add"
                      >
                        <option value="">Select a part…</option>
                        {availableParts.map(c => (
                          <option key={c.id} value={c.id}>
                            {c.name} ({c.category}){c.sku ? ` [${c.sku}]` : ''}{c.inStock < c.minimumStock ? ' ⚠ Low' : ''}
                          </option>
                        ))}
                      </Select>

                      {selectedPartId && (
                        <div className="flex items-end gap-2">
                          <div className="w-20">
                            <label htmlFor="part-qty" className="block text-xs text-pf-text-muted mb-0.5">Qty</label>
                            <Input id="part-qty" type="number" min="1" value={partQuantity} onChange={e => setPartQuantity(e.target.value)} />
                          </div>
                          <div className="flex-1">
                            <label htmlFor="part-notes" className="block text-xs text-pf-text-muted mb-0.5">Notes (optional)</label>
                            <Input id="part-notes" value={partNotes} onChange={e => setPartNotes(e.target.value)} placeholder="e.g. use PTFE-coated variant" maxLength={500} />
                          </div>
                          <Button variant="primary" size="sm" onClick={handleAddPart} disabled={isAddingPart} className="shrink-0">
                            {isAddingPart ? 'Adding…' : 'Add'}
                          </Button>
                        </div>
                      )}
                    </>
                  )}
                </div>
              </>
            )}
          </section>
        </div>
      </Modal>

      {/* Remove part confirmation */}
      <ConfirmationModal
        isOpen={!!removingTc}
        title="Remove Part"
        message={`Remove "${removingTc?.componentName ?? 'this part'}" from task "${task?.taskName ?? ''}"? The part remains in your inventory.`}
        confirmButtonText="Remove"
        isDangerous
        onConfirm={handleRemoveConfirm}
        onCancel={() => setRemovingTc(null)}
      />
    </>
  );
}

// ──────────────────────── Scope Rule Badges ────────────────────────

function ScopeRuleBadges({ task }: { task: MaintenanceTaskDto }) {
  const active = SCOPE_RULES.filter(r => task[r.key] === true);
  if (active.length === 0) return null;
  return (
    <div className="flex flex-wrap gap-1 mt-1">
      {active.map(r => (
        <Badge key={r.key} variant="default" className="text-[10px] px-1.5 py-0">
          {r.label}
        </Badge>
      ))}
    </div>
  );
}

// ──────────────────────── Main Component ────────────────────────

export function TaskCatalogTab() {
  const { data: tasks = [], isLoading, error } = useTaskCatalog();
  const { data: categories = [] } = useTaskCategories();
  const deleteTask = useDeleteCatalogTask();
  const exportMutation = useExportTasks();
  const importMutation = useImportTasks();
  const importFileRef = useRef<HTMLInputElement>(null);

  const [search, setSearch] = useState('');
  const [categoryFilter, setCategoryFilter] = useState('');
  const [isFormOpen, setIsFormOpen] = useState(false);
  const [editingTaskId, setEditingTaskId] = useState<string | null>(null);
  const [cloneSource, setCloneSource] = useState<MaintenanceTaskDto | null>(null);
  const [deletingTask, setDeletingTask] = useState<MaintenanceTaskDto | null>(null);

  const filtered = useMemo(() => {
    const q = search.toLowerCase();
    return tasks.filter(t => {
      if (categoryFilter && t.category !== categoryFilter) return false;
      if (q && !t.taskName.toLowerCase().includes(q) && !t.description?.toLowerCase().includes(q) && !t.category.toLowerCase().includes(q)) return false;
      return true;
    });
  }, [tasks, search, categoryFilter]);

  // Group by category for display
  const grouped = useMemo(() => {
    const map = new Map<string, MaintenanceTaskDto[]>();
    for (const t of filtered) {
      const list = map.get(t.category) ?? [];
      list.push(t);
      map.set(t.category, list);
    }
    return Array.from(map.entries()).sort(([a], [b]) => a.localeCompare(b));
  }, [filtered]);

  const handleEdit = (task: MaintenanceTaskDto) => {
    setEditingTaskId(task.id);
    setCloneSource(null);
    setIsFormOpen(true);
  };

  const handleCreate = () => {
    setEditingTaskId(null);
    setCloneSource(null);
    setIsFormOpen(true);
  };

  const handleFormClose = () => {
    setIsFormOpen(false);
    setEditingTaskId(null);
    setCloneSource(null);
  };

  const handleClone = (task: MaintenanceTaskDto) => {
    setEditingTaskId(null);
    setCloneSource(task);
    setIsFormOpen(true);
  };

  const handleDelete = async () => {
    if (!deletingTask) return;
    try {
      await deleteTask.mutateAsync(deletingTask.id);
      toast.success(`Deleted "${deletingTask.taskName}"`);
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to delete task');
    } finally {
      setDeletingTask(null);
    }
  };

  const handleExport = async () => {
    try {
      const envelope = await exportMutation.mutateAsync();
      const blob = new Blob([JSON.stringify(envelope, null, 2)], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `maintenance-tasks-${new Date().toISOString().slice(0, 10)}.json`;
      a.click();
      URL.revokeObjectURL(url);
      toast.success(`Exported ${envelope.tasks?.length ?? 0} tasks`);
    } catch {
      toast.error('Failed to export tasks');
    }
  };

  const handleImportFile = async (files: FileList | null) => {
    const file = files?.[0];
    if (!file) return;
    try {
      const text = await file.text();
      const envelope = JSON.parse(text) as MaintenanceExportEnvelope;
      const result = await importMutation.mutateAsync(envelope);
      toast.success(`Import complete: ${result.createdCount} created, ${result.updatedCount} updated`);
      if (result.warnings.length > 0) {
        toast.warning(result.warnings.join('\n'));
      }
      if (result.errorCount > 0) {
        toast.error(`${result.errorCount} errors: ${result.errors.join(', ')}`);
      }
    } catch {
      toast.error('Failed to import tasks — check the JSON format');
    } finally {
      if (importFileRef.current) { importFileRef.current.value = ''; }
    }
  };

  if (error) {
    return (
      <div className="text-center py-12 text-pf-error" role="alert">
        Failed to load task catalog. {error instanceof Error ? error.message : ''}
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {/* Toolbar */}
      <div className="flex flex-col sm:flex-row sm:items-center gap-3">
        <div className="relative flex-1">
          <SearchIcon className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-pf-text-muted" aria-hidden="true" />
          <Input
            className="pl-9"
            placeholder="Search tasks…"
            value={search}
            onChange={e => setSearch(e.target.value)}
            aria-label="Search task catalog"
          />
        </div>
        <div className="flex items-center gap-2">
          <FilterIcon className="h-4 w-4 text-pf-text-muted shrink-0" aria-hidden="true" />
          <Select
            value={categoryFilter}
            onChange={e => setCategoryFilter(e.target.value)}
            aria-label="Filter by category"
            className="min-w-[140px]"
          >
            <option value="">All categories</option>
            {categories.map(c => (
              <option key={c} value={c}>{c}</option>
            ))}
          </Select>
        </div>
        <Button variant="primary" size="sm" onClick={handleCreate} iconLeft={<PlusIcon className="h-4 w-4" />} className="shrink-0">
          New Task
        </Button>
        <Button variant="secondary" size="sm" onClick={handleExport} iconLeft={<DownloadIcon className="h-4 w-4" />} loading={exportMutation.isPending} className="shrink-0">
          Export
        </Button>
        <Button variant="secondary" size="sm" onClick={() => importFileRef.current?.click()} iconLeft={<UploadIcon className="h-4 w-4" />} loading={importMutation.isPending} className="shrink-0">
          Import
        </Button>
        <FileUpload ref={importFileRef} accept=".json" className="hidden" onChange={handleImportFile} />
      </div>

      {/* Summary */}
      <p className="text-sm text-pf-text-muted">
        {filtered.length} task{filtered.length !== 1 ? 's' : ''}
        {categoryFilter ? ` in "${categoryFilter}"` : ''}
        {search ? ` matching "${search}"` : ''}
        {' '}• {grouped.length} categor{grouped.length !== 1 ? 'ies' : 'y'}
      </p>

      {/* Loading */}
      {isLoading && (
        <div className="text-center py-8 text-pf-text-muted">Loading task catalog…</div>
      )}

      {/* Empty */}
      {!isLoading && filtered.length === 0 && (
        <div className="text-center py-12 text-pf-text-muted">
          {tasks.length === 0
            ? 'No tasks in the catalog yet. Create one to get started.'
            : 'No tasks match your filters.'}
        </div>
      )}

      {/* Grouped task list */}
      {grouped.map(([cat, catTasks]) => (
        <section key={cat} className="space-y-2">
          <h3 className="text-sm font-semibold text-pf-text-secondary uppercase tracking-wide">
            {cat} <span className="text-pf-text-muted font-normal">({catTasks.length})</span>
          </h3>
          <div className="divide-y divide-pf-border rounded-lg border border-pf-border bg-pf-bg-2">
            {catTasks.map(task => (
              <div key={task.id} className="flex items-start gap-3 p-3 hover:bg-pf-bg-2 transition-colors">
                {/* Info */}
                <div className="flex-1 min-w-0">
                  <div className="flex items-center gap-2">
                    <span className="font-medium text-pf-text-primary truncate">{task.taskName}</span>
                    <Badge variant={priorityVariant(task.priority)} className="text-[10px] shrink-0">
                      {priorityLabel(task.priority)}
                    </Badge>
                    {!task.isActive && (
                      <Badge variant="default" className="text-[10px] shrink-0">Inactive</Badge>
                    )}
                    {task.isDefault && (
                      <Badge variant="success" className="text-[10px] shrink-0">Seed</Badge>
                    )}
                  </div>
                  {task.description && (
                    <p className="text-xs text-pf-text-muted mt-0.5 line-clamp-1">{task.description}</p>
                  )}
                  <div className="flex items-center gap-3 mt-1 text-xs text-pf-text-muted">
                    <span>{intervalText(task)}</span>
                    {task.estimatedDurationMinutes != null && <span>~{task.estimatedDurationMinutes} min</span>}
                    {task.taskComponents.length > 0 && (
                      <span>{task.taskComponents.length} part{task.taskComponents.length !== 1 ? 's' : ''}</span>
                    )}
                  </div>
                  <ScopeRuleBadges task={task} />
                </div>
                {/* Actions */}
                <div className="flex items-center gap-1 shrink-0">
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => handleClone(task)}
                    aria-label={`Clone ${task.taskName}`}
                    title="Clone task"
                  >
                    <CopyIcon className="h-4 w-4" aria-hidden="true" />
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => handleEdit(task)}
                    aria-label={`Edit ${task.taskName}`}
                  >
                    <EditIcon className="h-4 w-4" aria-hidden="true" />
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => setDeletingTask(task)}
                    aria-label={`Delete ${task.taskName}`}
                    className="text-pf-error hover:text-pf-error"
                  >
                    <DeleteIcon className="h-4 w-4" aria-hidden="true" />
                  </Button>
                </div>
              </div>
            ))}
          </div>
        </section>
      ))}

      {/* Modals */}
      <TaskFormModal
        isOpen={isFormOpen}
        taskId={editingTaskId}
        tasks={tasks}
        categories={categories}
        cloneSource={cloneSource}
        onClose={handleFormClose}
        onTaskCreated={(newId) => setEditingTaskId(newId)}
      />
      <ConfirmationModal
        isOpen={!!deletingTask}
        title="Delete Task"
        message={`Delete "${deletingTask?.taskName}"? This cannot be undone. Tasks referenced by plans cannot be deleted.`}
        confirmButtonText="Delete"
        isDangerous
        onConfirm={handleDelete}
        onCancel={() => setDeletingTask(null)}
      />
    </div>
  );
}
