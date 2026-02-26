/**
 * MaintenancePlansTab Component (v2 - Hierarchical)
 *
 * Displays maintenance plans with their nested tasks. Plans can be created, edited,
 * deleted. Tasks can be added/edited/deleted within each plan. Expandable plan rows
 * reveal the task list with interval and priority details.
 */

import React, { useMemo, useState, useCallback } from 'react';
import { toast } from 'sonner';
import { format } from 'date-fns';
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
  GearIcon,
  SearchIcon,
  ClockIcon,
  AlertIcon,
  ChevronRightIcon,
  ChevronDownIcon,
} from '@/common/components/icons/MdiIcons';
import {
  useMaintenancePlans,
  useCreatePlan,
  useUpdatePlan,
  useDeletePlan,
  useCreateTask,
  useUpdateTask,
  useDeleteTask,
} from '../hooks/useMaintenancePlans';
import type {
  MaintenancePlanDto,
  MaintenanceTaskDto,
  CreateMaintenancePlanDto,
  UpdateMaintenancePlanDto,
  CreateMaintenanceTaskDto,
  UpdateMaintenanceTaskDto,
} from '@/types/maintenance';
import { useTaskCategories } from '../hooks/useTaskCatalog';

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

const priorityOptions = [
  { value: '1', label: 'Low' },
  { value: '2', label: 'Medium' },
  { value: '3', label: 'High' },
  { value: '4', label: 'Critical' },
];

// ──────────────────────── Plan Form Modal ────────────────────────

interface PlanFormModalProps {
  isOpen: boolean;
  plan?: MaintenancePlanDto | null;
  onClose: () => void;
}

function PlanFormModal({ isOpen, plan, onClose }: PlanFormModalProps) {
  const isEdit = !!plan;
  const createPlan = useCreatePlan();
  const updatePlan = useUpdatePlan();

  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [isActive, setIsActive] = useState(true);
  const [isSubmitting, setIsSubmitting] = useState(false);

  React.useEffect(() => {
    if (isOpen) {
      setName(plan?.name ?? '');
      setDescription(plan?.description ?? '');
      setIsActive(plan?.isActive ?? true);
    }
  }, [isOpen, plan]);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!name.trim()) return;
    setIsSubmitting(true);
    try {
      if (isEdit && plan) {
        const data: UpdateMaintenancePlanDto = { name: name.trim(), description: description.trim() || null, isActive };
        await updatePlan.mutateAsync({ id: plan.id, data });
        toast.success('Plan updated');
      } else {
        const data: CreateMaintenancePlanDto = { name: name.trim(), description: description.trim() || null, isActive };
        await createPlan.mutateAsync(data);
        toast.success('Plan created');
      }
      onClose();
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to save plan');
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <Modal isOpen={isOpen} onClose={onClose} title={isEdit ? 'Edit Plan' : 'New Maintenance Plan'} size="md">
      <form onSubmit={handleSubmit} className="space-y-4">
        <div>
          <label htmlFor="plan-name" className="block text-sm font-medium text-pf-text-secondary mb-1">
            Name <span className="text-red-400">*</span>
          </label>
          <Input id="plan-name" value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. Prusa MK4 Preventive Maintenance" required maxLength={200} />
        </div>
        <div>
          <label htmlFor="plan-desc" className="block text-sm font-medium text-pf-text-secondary mb-1">Description</label>
          <Textarea id="plan-desc" value={description} onChange={(e) => setDescription(e.target.value)} placeholder="What does this plan cover?" rows={3} maxLength={1000} />
        </div>
        <Checkbox label="Active" checked={isActive} onChange={(e) => setIsActive(e.target.checked)} />
        <div className="flex justify-end gap-2 pt-2">
          <Button type="button" variant="secondary" size="sm" onClick={onClose}>Cancel</Button>
          <Button type="submit" variant="primary" size="sm" disabled={isSubmitting || !name.trim()}>
            {isSubmitting ? 'Saving…' : isEdit ? 'Save Changes' : 'Create Plan'}
          </Button>
        </div>
      </form>
    </Modal>
  );
}

// ──────────────────────── Task Form Modal ────────────────────────

interface TaskFormModalProps {
  isOpen: boolean;
  planId: string;
  task?: MaintenanceTaskDto | null;
  onClose: () => void;
}

const DEFAULT_CATEGORY = 'General';

function TaskFormModal({ isOpen, planId, task, onClose }: TaskFormModalProps) {
  const isEdit = !!task;
  const createTask = useCreateTask(planId);
  const updateTask = useUpdateTask(planId);

  const { data: categories = [] } = useTaskCategories();

  const [taskName, setTaskName] = useState('');
  const [category, setCategory] = useState(DEFAULT_CATEGORY);
  const [description, setDescription] = useState('');
  const [intervalType, setIntervalType] = useState<'hours' | 'days' | 'none'>('hours');
  const [intervalValue, setIntervalValue] = useState('');
  const [estimatedMinutes, setEstimatedMinutes] = useState('');
  const [priority, setPriority] = useState('2');
  const [isActive, setIsActive] = useState(true);
  const [isSubmitting, setIsSubmitting] = useState(false);

  React.useEffect(() => {
    if (isOpen) {
      setTaskName(task?.taskName ?? '');
      setCategory(task?.category ?? (categories[0] ?? DEFAULT_CATEGORY));
      setDescription(task?.description ?? '');
      setEstimatedMinutes(task?.estimatedDurationMinutes?.toString() ?? '');
      setPriority((task?.priority ?? 2).toString());
      setIsActive(task?.isActive ?? true);
      if (task?.intervalHours != null) {
        setIntervalType('hours');
        setIntervalValue(task.intervalHours.toString());
      } else if (task?.intervalDays != null) {
        setIntervalType('days');
        setIntervalValue(task.intervalDays.toString());
      } else {
        setIntervalType('none');
        setIntervalValue('');
      }
    }
  }, [isOpen, task]);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!taskName.trim()) return;
    setIsSubmitting(true);
    try {
      const base = {
        taskName: taskName.trim(),
        category,
        description: description.trim() || null,
        intervalHours: intervalType === 'hours' && intervalValue ? Number(intervalValue) : null,
        intervalDays: intervalType === 'days' && intervalValue ? Number(intervalValue) : null,
        estimatedDurationMinutes: estimatedMinutes ? Number(estimatedMinutes) : null,
        priority: Number(priority),
        isActive,
      };
      if (isEdit && task) {
        await updateTask.mutateAsync({ taskId: task.id, data: base as UpdateMaintenanceTaskDto });
        toast.success('Task updated');
      } else {
        await createTask.mutateAsync(base as CreateMaintenanceTaskDto);
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
    <Modal isOpen={isOpen} onClose={onClose} title={isEdit ? 'Edit Task' : 'Add Task'} size="md">
      <form onSubmit={handleSubmit} className="space-y-4">
        <div>
          <label htmlFor="task-name" className="block text-sm font-medium text-pf-text-secondary mb-1">
            Task Name <span className="text-red-400">*</span>
          </label>
          <Input id="task-name" value={taskName} onChange={(e) => setTaskName(e.target.value)} placeholder="e.g. Check belt tension" required maxLength={200} />
        </div>
        <div>
          <label htmlFor="task-cat" className="block text-sm font-medium text-pf-text-secondary mb-1">Category <span className="text-red-400">*</span></label>
          <Select id="task-cat" value={category} onChange={(e) => setCategory(e.target.value)}>
            {categories.length > 0 ? categories.map(c => (<option key={c} value={c}>{c}</option>)) : <option value={DEFAULT_CATEGORY}>{DEFAULT_CATEGORY}</option>}
          </Select>
        </div>
        <div>
          <label htmlFor="task-desc" className="block text-sm font-medium text-pf-text-secondary mb-1">Description</label>
          <Textarea id="task-desc" value={description} onChange={(e) => setDescription(e.target.value)} placeholder="What does this task involve?" rows={2} maxLength={1000} />
        </div>
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label htmlFor="interval-type" className="block text-sm font-medium text-pf-text-secondary mb-1">Interval</label>
            <Select id="interval-type" value={intervalType} onChange={(e) => setIntervalType(e.target.value as 'hours' | 'days' | 'none')}>
              <option value="hours">Print Hours</option>
              <option value="days">Calendar Days</option>
              <option value="none">Manual (no interval)</option>
            </Select>
          </div>
          {intervalType !== 'none' && (
            <div>
              <label htmlFor="interval-val" className="block text-sm font-medium text-pf-text-secondary mb-1">
                Every ({intervalType === 'hours' ? 'hours' : 'days'})
              </label>
              <Input id="interval-val" type="number" min="1" value={intervalValue} onChange={(e) => setIntervalValue(e.target.value)} placeholder={intervalType === 'hours' ? '500' : '30'} />
            </div>
          )}
        </div>
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label htmlFor="task-priority" className="block text-sm font-medium text-pf-text-secondary mb-1">Priority</label>
            <Select id="task-priority" value={priority} onChange={(e) => setPriority(e.target.value)}>
              {priorityOptions.map((o) => (
                <option key={o.value} value={o.value}>{o.label}</option>
              ))}
            </Select>
          </div>
          <div>
            <label htmlFor="task-duration" className="block text-sm font-medium text-pf-text-secondary mb-1">Est. Duration (min)</label>
            <Input id="task-duration" type="number" min="1" value={estimatedMinutes} onChange={(e) => setEstimatedMinutes(e.target.value)} placeholder="30" />
          </div>
        </div>
        <Checkbox label="Active" checked={isActive} onChange={(e) => setIsActive(e.target.checked)} />
        <div className="flex justify-end gap-2 pt-2">
          <Button type="button" variant="secondary" size="sm" onClick={onClose}>Cancel</Button>
          <Button type="submit" variant="primary" size="sm" disabled={isSubmitting || !taskName.trim()}>
            {isSubmitting ? 'Saving…' : isEdit ? 'Save Changes' : 'Add Task'}
          </Button>
        </div>
      </form>
    </Modal>
  );
}

// ──────────────────────── Task Row ────────────────────────

interface TaskRowProps {
  task: MaintenanceTaskDto;
  onEdit: () => void;
  onDelete: () => void;
}

function TaskRow({ task, onEdit, onDelete }: TaskRowProps) {
  return (
    <div
      className={`flex items-center gap-3 px-4 py-2.5 rounded border transition-colors ${
        task.isActive
          ? 'bg-pf-bg-1 border-pf-border/60 hover:border-pf-accent/30'
          : 'bg-pf-bg-1/50 border-pf-border/30 opacity-60'
      }`}
    >
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="font-medium text-sm text-pf-text-primary truncate">{task.taskName}</span>
          <Badge variant={priorityVariant(task.priority)} className="text-[10px] leading-tight">{priorityLabel(task.priority)}</Badge>
          {!task.isActive && <Badge variant="default" className="text-[10px]">Inactive</Badge>}
        </div>
        <div className="flex items-center gap-3 mt-0.5 text-xs text-pf-text-tertiary">
          <span className="flex items-center gap-1">
            <ClockIcon className="h-3 w-3" />
            {intervalText(task)}
          </span>
          {task.estimatedDurationMinutes != null && <span>~{task.estimatedDurationMinutes}min</span>}
          {task.taskComponents.length > 0 && (
            <span className="flex items-center gap-1">
              <GearIcon className="h-3 w-3" />
              {task.taskComponents.length} part{task.taskComponents.length !== 1 ? 's' : ''}
            </span>
          )}
        </div>
      </div>
      <div className="flex items-center gap-1 shrink-0">
        <Button variant="subtle" size="sm" onClick={onEdit} aria-label={`Edit ${task.taskName}`}>
          <EditIcon className="h-3.5 w-3.5" />
        </Button>
        <Button variant="subtle" size="sm" onClick={onDelete} aria-label={`Delete ${task.taskName}`} className="hover:text-red-400">
          <DeleteIcon className="h-3.5 w-3.5" />
        </Button>
      </div>
    </div>
  );
}

// ──────────────────────── Plan Row ────────────────────────

interface PlanRowProps {
  plan: MaintenancePlanDto;
  isExpanded: boolean;
  onToggle: () => void;
  onEditPlan: () => void;
  onDeletePlan: () => void;
}

function PlanRow({ plan, isExpanded, onToggle, onEditPlan, onDeletePlan }: PlanRowProps) {
  const [editingTask, setEditingTask] = useState<MaintenanceTaskDto | null>(null);
  const [isTaskFormOpen, setIsTaskFormOpen] = useState(false);
  const [deletingTask, setDeletingTask] = useState<MaintenanceTaskDto | null>(null);
  const deleteTask = useDeleteTask(plan.id);

  const handleAddTask = useCallback(() => {
    setEditingTask(null);
    setIsTaskFormOpen(true);
  }, []);

  const handleEditTask = useCallback((task: MaintenanceTaskDto) => {
    setEditingTask(task);
    setIsTaskFormOpen(true);
  }, []);

  const handleDeleteTaskConfirm = async () => {
    if (!deletingTask) return;
    try {
      await deleteTask.mutateAsync(deletingTask.id);
      toast.success(`Task "${deletingTask.taskName}" deleted`);
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to delete task');
    }
    setDeletingTask(null);
  };

  const activeTaskCount = plan.planTasks.filter((pt) => pt.task.isActive).length;

  return (
    <div
      className={`rounded-lg border transition-colors ${
        plan.isActive
          ? 'bg-pf-bg-2 border-pf-border'
          : 'bg-pf-bg-1 border-pf-border/50 opacity-60'
      }`}
    >
      {/* Plan header */}
      <div className="flex items-center gap-3 p-4">
        <button
          type="button"
          className="flex items-center gap-3 flex-1 min-w-0 cursor-pointer text-left"
          onClick={onToggle}
          aria-expanded={isExpanded}
          aria-controls={`plan-tasks-${plan.id}`}
        >
          <span className="shrink-0 text-pf-text-tertiary">
            {isExpanded ? <ChevronDownIcon className="h-5 w-5" /> : <ChevronRightIcon className="h-5 w-5" />}
          </span>
          <span className="flex-1 min-w-0">
            <span className="flex items-center gap-2 flex-wrap">
              <span className="font-medium text-pf-text-primary truncate">{plan.name}</span>
              {!plan.isActive && <Badge variant="default" className="text-xs">Inactive</Badge>}
              {plan.isDefault && <Badge variant="success" className="text-xs">Default</Badge>}
            </span>
            {plan.description && (
              <span className="block text-sm text-pf-text-tertiary mt-0.5 line-clamp-1">{plan.description}</span>
            )}
            <span className="flex items-center gap-4 mt-1 text-xs text-pf-text-tertiary">
              <span>{plan.planTasks.length} task{plan.planTasks.length !== 1 ? 's' : ''} ({activeTaskCount} active)</span>
              <span>Created {format(new Date(plan.createdAt), 'MMM d, yyyy')}</span>
            </span>
          </span>
        </button>
        <div className="flex items-center gap-1.5 shrink-0">
          <Button variant="subtle" size="sm" onClick={onEditPlan} aria-label={`Edit plan ${plan.name}`}>
            <EditIcon className="h-4 w-4" />
          </Button>
          <Button variant="subtle" size="sm" onClick={onDeletePlan} aria-label={`Delete plan ${plan.name}`} className="hover:text-red-400">
            <DeleteIcon className="h-4 w-4" />
          </Button>
        </div>
      </div>

      {/* Expanded tasks */}
      {isExpanded && (
        <div id={`plan-tasks-${plan.id}`} className="border-t border-pf-border/60 px-4 py-3 space-y-2">
          {plan.planTasks.length === 0 ? (
            <p className="text-sm text-pf-text-tertiary text-center py-4">No tasks yet. Add tasks to define what this plan covers.</p>
          ) : (
            plan.planTasks.map((planTask) => (
              <TaskRow
                key={planTask.task.id}
                task={planTask.task}
                onEdit={() => handleEditTask(planTask.task)}
                onDelete={() => setDeletingTask(planTask.task)}
              />
            ))
          )}
          <Button variant="secondary" size="sm" onClick={handleAddTask} className="gap-1.5 mt-2">
            <PlusIcon className="h-3.5 w-3.5" />
            Add Task
          </Button>

          {/* Task form modal */}
          <TaskFormModal
            isOpen={isTaskFormOpen}
            planId={plan.id}
            task={editingTask}
            onClose={() => setIsTaskFormOpen(false)}
          />

          {/* Task delete confirmation */}
          <ConfirmationModal
            isOpen={!!deletingTask}
            title="Delete Task"
            message={`Delete "${deletingTask?.taskName}"? This cannot be undone.`}
            confirmButtonText="Delete"
            isDangerous
            onConfirm={handleDeleteTaskConfirm}
            onCancel={() => setDeletingTask(null)}
          />
        </div>
      )}
    </div>
  );
}

// ──────────────────────── Main Component ────────────────────────

export function MaintenancePlansTab() {
  const { data: plans = [], isLoading, error } = useMaintenancePlans();
  const deletePlan = useDeletePlan();

  const [search, setSearch] = useState('');
  const [expandedIds, setExpandedIds] = useState<Set<string>>(new Set());
  const [isPlanFormOpen, setIsPlanFormOpen] = useState(false);
  const [editingPlan, setEditingPlan] = useState<MaintenancePlanDto | null>(null);
  const [deletingPlan, setDeletingPlan] = useState<MaintenancePlanDto | null>(null);

  const toggleExpanded = useCallback((id: string) => {
    setExpandedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);

  const filtered = useMemo(() => {
    let result = plans;
    if (search) {
      const q = search.toLowerCase();
      result = result.filter(
        (p) =>
          p.name.toLowerCase().includes(q) ||
          (p.description?.toLowerCase().includes(q) ?? false) ||
          p.planTasks.some((pt) => pt.task.taskName.toLowerCase().includes(q))
      );
    }
    return [...result].sort((a, b) => {
      if (a.isActive !== b.isActive) return a.isActive ? -1 : 1;
      return a.name.localeCompare(b.name);
    });
  }, [plans, search]);

  const handleDeletePlanConfirm = async () => {
    if (!deletingPlan) return;
    try {
      await deletePlan.mutateAsync(deletingPlan.id);
      toast.success(`Plan "${deletingPlan.name}" deleted`);
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to delete plan');
    }
    setDeletingPlan(null);
  };

  if (isLoading) {
    return (
      <div className="space-y-3">
        {Array.from({ length: 4 }).map((_, i) => (
          <div key={i} className="h-24 bg-pf-border/50 rounded-lg animate-pulse" />
        ))}
      </div>
    );
  }

  if (error) {
    return (
      <div className="text-center py-12">
        <AlertIcon className="h-10 w-10 text-red-400 mx-auto mb-3" />
        <p className="text-pf-text-secondary">Failed to load maintenance plans</p>
        <p className="text-xs text-pf-text-tertiary mt-1">{(error as Error).message}</p>
      </div>
    );
  }

  return (
    <>
      {/* Toolbar */}
      <div className="flex flex-col sm:flex-row items-start sm:items-center gap-3 mb-5">
        <div className="relative flex-1 w-full sm:max-w-sm">
          <SearchIcon className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-pf-text-tertiary" />
          <Input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search plans or tasks..."
            className="pl-9 w-full"
            aria-label="Search maintenance plans"
          />
        </div>
        <Button
          variant="primary"
          size="sm"
          onClick={() => { setEditingPlan(null); setIsPlanFormOpen(true); }}
          className="gap-1.5 shrink-0"
        >
          <PlusIcon className="h-4 w-4" />
          New Plan
        </Button>
      </div>

      {/* Summary */}
      <p className="text-sm text-pf-text-tertiary mb-4">
        {filtered.length} plan{filtered.length !== 1 ? 's' : ''}
        {search ? ` matching "${search}"` : ''}
        {' '}• {filtered.reduce((n, p) => n + p.planTasks.length, 0)} total tasks
      </p>

      {/* Plan List */}
      {filtered.length === 0 ? (
        <div className="text-center py-12">
          <GearIcon className="h-12 w-12 text-pf-text-tertiary mx-auto mb-3" />
          <h3 className="font-medium text-pf-text-primary">No Maintenance Plans</h3>
          <p className="text-sm text-pf-text-tertiary mt-1">
            {search ? 'No plans match your search' : 'Create a maintenance plan to group related tasks'}
          </p>
        </div>
      ) : (
        <div className="space-y-2">
          {filtered.map((plan) => (
            <PlanRow
              key={plan.id}
              plan={plan}
              isExpanded={expandedIds.has(plan.id)}
              onToggle={() => toggleExpanded(plan.id)}
              onEditPlan={() => { setEditingPlan(plan); setIsPlanFormOpen(true); }}
              onDeletePlan={() => setDeletingPlan(plan)}
            />
          ))}
        </div>
      )}

      {/* Plan Form Modal */}
      <PlanFormModal
        isOpen={isPlanFormOpen}
        plan={editingPlan}
        onClose={() => setIsPlanFormOpen(false)}
      />

      {/* Plan Delete Confirmation */}
      <ConfirmationModal
        isOpen={!!deletingPlan}
        title="Delete Maintenance Plan"
        message={`Delete "${deletingPlan?.name}" and all its ${deletingPlan?.planTasks.length ?? 0} task(s)? This cannot be undone.`}
        confirmButtonText="Delete"
        isDangerous
        onConfirm={handleDeletePlanConfirm}
        onCancel={() => setDeletingPlan(null)}
      />
    </>
  );
}


