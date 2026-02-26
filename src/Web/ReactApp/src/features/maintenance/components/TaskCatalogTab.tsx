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
  GearIcon,
} from '@/common/components/icons/MdiIcons';
import { TaskComponentManager } from './TaskComponentManager';
import {
  useTaskCatalog,
  useTaskCategories,
  useCreateCatalogTask,
  useUpdateCatalogTask,
  useDeleteCatalogTask,
} from '../hooks/useTaskCatalog';
import type {
  MaintenanceTaskDto,
  CreateMaintenanceTaskDto,
  UpdateMaintenanceTaskDto,
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
  task?: MaintenanceTaskDto | null;
  categories: string[];
  onClose: () => void;
}

function TaskFormModal({ isOpen, task, categories, onClose }: TaskFormModalProps) {
  const isEdit = !!task;
  const createTask = useCreateCatalogTask();
  const updateTask = useUpdateCatalogTask();

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
  const prevOpenRef = useRef(false);

  React.useEffect(() => {
    if (isOpen && !prevOpenRef.current) {
      setTaskName(task?.taskName ?? '');
      setCategory(task?.category ?? (categories[0] ?? ''));
      setCustomCategory('');
      setDescription(task?.description ?? '');
      if (task?.intervalHours != null) {
        setIntervalType('hours');
        setIntervalValue(String(task.intervalHours));
      } else if (task?.intervalDays != null) {
        setIntervalType('days');
        setIntervalValue(String(task.intervalDays));
      } else {
        setIntervalType('none');
        setIntervalValue('');
      }
      setEstimatedMinutes(task?.estimatedDurationMinutes != null ? String(task.estimatedDurationMinutes) : '');
      setPriority(String(task?.priority ?? 2));
      setIsActive(task?.isActive ?? true);
      setIsDefault(task?.isDefault ?? false);
      const rules: Record<string, boolean | null> = {};
      for (const rule of SCOPE_RULES) {
        const val = task?.[rule.key];
        rules[rule.key] = typeof val === 'boolean' ? val : null;
      }
      setScopeRules(rules);
    }
    prevOpenRef.current = isOpen;
  }, [isOpen, task]); // eslint-disable-line react-hooks/exhaustive-deps

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
      } else {
        await createTask.mutateAsync(data as CreateMaintenanceTaskDto);
        toast.success('Task created');
      }
      onClose();
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to save task');
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <Modal isOpen={isOpen} onClose={onClose} title={isEdit ? 'Edit Task' : 'New Catalog Task'} size="lg">
      <form onSubmit={handleSubmit} className="space-y-4 max-h-[70vh] overflow-y-auto pr-1">
        {/* Name */}
        <div>
          <label htmlFor="task-name" className="block text-sm font-medium text-pf-text-secondary mb-1">
            Name <span className="text-red-400">*</span>
          </label>
          <Input id="task-name" value={taskName} onChange={e => setTaskName(e.target.value)} placeholder="e.g. Clean nozzle" required maxLength={200} />
        </div>

        {/* Category */}
        <div>
          <label htmlFor="task-category" className="block text-sm font-medium text-pf-text-secondary mb-1">
            Category <span className="text-red-400">*</span>
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

        {/* Actions */}
        <div className="flex justify-end gap-2 pt-2">
          <Button type="button" variant="secondary" size="sm" onClick={onClose}>Cancel</Button>
          <Button type="submit" variant="primary" size="sm" disabled={isSubmitting || !taskName.trim()}>
            {isSubmitting ? 'Saving…' : isEdit ? 'Save Changes' : 'Create Task'}
          </Button>
        </div>
      </form>
    </Modal>
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

  const [search, setSearch] = useState('');
  const [categoryFilter, setCategoryFilter] = useState('');
  const [isFormOpen, setIsFormOpen] = useState(false);
  const [editingTask, setEditingTask] = useState<MaintenanceTaskDto | null>(null);
  const [deletingTask, setDeletingTask] = useState<MaintenanceTaskDto | null>(null);
  const [partsTask, setPartsTask] = useState<MaintenanceTaskDto | null>(null);

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
    setEditingTask(task);
    setIsFormOpen(true);
  };

  const handleCreate = () => {
    setEditingTask(null);
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

  if (error) {
    return (
      <div className="text-center py-12 text-red-400" role="alert">
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
        <Button variant="primary" size="sm" onClick={handleCreate} className="gap-1 shrink-0">
          <PlusIcon className="h-4 w-4" aria-hidden="true" />
          New Task
        </Button>
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
              <div key={task.id} className="flex items-start gap-3 p-3 hover:bg-pf-bg-3 transition-colors">
                {/* Info */}
                <div className="flex-1 min-w-0">
                  <div className="flex items-center gap-2">
                    <span className="font-medium text-pf-text truncate">{task.taskName}</span>
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
                    onClick={() => setPartsTask(task)}
                    aria-label={`Manage parts for ${task.taskName}`}
                    title="Manage parts"
                  >
                    <GearIcon className="h-4 w-4" aria-hidden="true" />
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
                    className="text-red-400 hover:text-red-300"
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
        task={editingTask}
        categories={categories}
        onClose={() => { setIsFormOpen(false); setEditingTask(null); }}
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
      {partsTask && (
        <TaskComponentManager
          isOpen={!!partsTask}
          task={partsTask}
          onClose={() => setPartsTask(null)}
        />
      )}
    </div>
  );
}
