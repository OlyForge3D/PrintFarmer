/**
 * MaintenancePlansTab Component
 *
 * Displays all maintenance schedules (plans) in a flat, searchable, sortable list.
 * Each schedule can be edited or deleted. Provides full visibility into every schedule
 * without truncation.
 */

import React, { useMemo, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { format } from 'date-fns';
import { Badge, Button } from '@/common/components/ui';
import { Input } from '@/common/components/ui/Input';
import { Select } from '@/common/components/ui/Select';
import {
  EditIcon,
  DeleteIcon,
  PlusIcon,
  GearIcon,
  SearchIcon,
  ClockIcon,
  AlertIcon,
} from '@/common/components/icons/MdiIcons';
import { maintenanceService } from '@/services/maintenanceService';
import type {
  MaintenanceSchedule,
  UpdateMaintenanceScheduleRequest,
  CreateMaintenanceScheduleRequest,
} from '@/types/maintenance';
import { EditScheduleModal } from './EditScheduleModal';
import { CreateScheduleModal } from './CreateScheduleModal';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';

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

function intervalText(schedule: MaintenanceSchedule): string {
  if (schedule.intervalHours != null) return `Every ${schedule.intervalHours}h`;
  if (schedule.intervalDays != null) return `Every ${schedule.intervalDays}d`;
  return 'Manual';
}

export function MaintenancePlansTab() {
  const queryClient = useQueryClient();
  const [search, setSearch] = useState('');
  const [componentFilter, setComponentFilter] = useState('');
  const [editingSchedule, setEditingSchedule] = useState<MaintenanceSchedule | null>(null);
  const [isCreateOpen, setIsCreateOpen] = useState(false);
  const [deletingSchedule, setDeletingSchedule] = useState<MaintenanceSchedule | null>(null);

  const { data: schedules = [], isLoading, error } = useQuery({
    queryKey: ['maintenanceSchedules', 'all'],
    queryFn: () => maintenanceService.getAllSchedules(),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, data }: { id: string; data: UpdateMaintenanceScheduleRequest }) =>
      maintenanceService.updateSchedule(id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['maintenanceSchedules'] });
      queryClient.invalidateQueries({ queryKey: ['componentMaintenance'] });
      toast.success('Schedule updated');
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to update schedule');
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => maintenanceService.deleteSchedule(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['maintenanceSchedules'] });
      queryClient.invalidateQueries({ queryKey: ['componentMaintenance'] });
      toast.success('Schedule deleted');
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to delete schedule');
    },
  });

  const createMutation = useMutation({
    mutationFn: (data: CreateMaintenanceScheduleRequest) =>
      maintenanceService.createSchedule(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['maintenanceSchedules'] });
      queryClient.invalidateQueries({ queryKey: ['componentMaintenance'] });
      toast.success('Schedule created');
      setIsCreateOpen(false);
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to create schedule');
    },
  });

  // Unique component names for filter dropdown
  const componentNames = useMemo(() => {
    const names = new Set<string>();
    for (const s of schedules) {
      if (s.component) names.add(s.component);
    }
    return Array.from(names).sort();
  }, [schedules]);

  // Filter and sort
  const filtered = useMemo(() => {
    let result = schedules;
    if (search) {
      const q = search.toLowerCase();
      result = result.filter(
        (s) =>
          s.taskName.toLowerCase().includes(q) ||
          (s.description?.toLowerCase().includes(q) ?? false) ||
          (s.component?.toLowerCase().includes(q) ?? false)
      );
    }
    if (componentFilter) {
      result = result.filter((s) => s.component === componentFilter);
    }
    // Sort: active first, then by component, then by task name
    return [...result].sort((a, b) => {
      if (a.isActive !== b.isActive) return a.isActive ? -1 : 1;
      const compA = a.component ?? '';
      const compB = b.component ?? '';
      if (compA !== compB) return compA.localeCompare(compB);
      return a.taskName.localeCompare(b.taskName);
    });
  }, [schedules, search, componentFilter]);

  const handleEdit = async (id: string, data: UpdateMaintenanceScheduleRequest) => {
    await updateMutation.mutateAsync({ id, data });
  };

  const handleCreate = async (data: CreateMaintenanceScheduleRequest) => {
    await createMutation.mutateAsync(data);
  };

  const handleDeleteConfirm = async () => {
    if (!deletingSchedule) return;
    await deleteMutation.mutateAsync(deletingSchedule.id);
    setDeletingSchedule(null);
  };

  if (isLoading) {
    return (
      <div className="space-y-3">
        {Array.from({ length: 6 }).map((_, i) => (
          <div key={i} className="h-20 bg-pf-border/50 rounded-lg animate-pulse" />
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
            placeholder="Search plans..."
            className="pl-9 w-full"
          />
        </div>
        <Select
          value={componentFilter}
          onChange={(e) => setComponentFilter(e.target.value)}
          containerClassName="w-full sm:w-48"
        >
          <option value="">All Components</option>
          {componentNames.map((c) => (
            <option key={c} value={c}>{c}</option>
          ))}
        </Select>
        <Button
          variant="primary"
          size="sm"
          onClick={() => setIsCreateOpen(true)}
          className="gap-1.5 shrink-0"
        >
          <PlusIcon className="h-4 w-4" />
          New Plan
        </Button>
      </div>

      {/* Summary */}
      <p className="text-sm text-pf-text-tertiary mb-4">
        {filtered.length} plan{filtered.length !== 1 ? 's' : ''}
        {componentFilter ? ` in ${componentFilter}` : ''}
        {search ? ` matching "${search}"` : ''}
        {' '}• {filtered.filter((s) => s.isActive).length} active
      </p>

      {/* Schedule List */}
      {filtered.length === 0 ? (
        <div className="text-center py-12">
          <GearIcon className="h-12 w-12 text-pf-text-tertiary mx-auto mb-3" />
          <h3 className="font-medium text-pf-text-primary">No Maintenance Plans</h3>
          <p className="text-sm text-pf-text-tertiary mt-1">
            {search || componentFilter
              ? 'No plans match your filters'
              : 'Create a maintenance plan to start tracking'}
          </p>
        </div>
      ) : (
        <div className="space-y-2">
          {filtered.map((schedule) => (
            <div
              key={schedule.id}
              className={`flex items-center gap-4 p-4 rounded-lg border transition-colors ${
                schedule.isActive
                  ? 'bg-pf-bg-2 border-pf-border hover:border-pf-accent/40'
                  : 'bg-pf-bg-1 border-pf-border/50 opacity-60'
              }`}
            >
              {/* Left: Info */}
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 flex-wrap">
                  <h4 className="font-medium text-pf-text-primary truncate">
                    {schedule.taskName}
                  </h4>
                  <Badge variant={priorityVariant(schedule.priority)} className="text-xs">
                    {priorityLabel(schedule.priority)}
                  </Badge>
                  {!schedule.isActive && (
                    <Badge variant="default" className="text-xs">Inactive</Badge>
                  )}
                </div>

                {schedule.description && (
                  <p className="text-sm text-pf-text-tertiary mt-0.5 line-clamp-2">
                    {schedule.description}
                  </p>
                )}

                <div className="flex items-center gap-4 mt-1.5 text-xs text-pf-text-tertiary flex-wrap">
                  {schedule.component && (
                    <span className="flex items-center gap-1">
                      <GearIcon className="h-3.5 w-3.5" />
                      {schedule.component}
                    </span>
                  )}
                  <span className="flex items-center gap-1">
                    <ClockIcon className="h-3.5 w-3.5" />
                    {intervalText(schedule)}
                  </span>
                  {schedule.estimatedDurationMinutes != null && (
                    <span>~{schedule.estimatedDurationMinutes}min</span>
                  )}
                  <span>
                    Created {format(new Date(schedule.createdAt), 'MMM d, yyyy')}
                  </span>
                </div>
              </div>

              {/* Right: Actions */}
              <div className="flex items-center gap-1.5 shrink-0">
                <Button
                  variant="subtle"
                  size="sm"
                  onClick={() => setEditingSchedule(schedule)}
                  aria-label={`Edit ${schedule.taskName}`}
                >
                  <EditIcon className="h-4 w-4" />
                </Button>
                <Button
                  variant="subtle"
                  size="sm"
                  onClick={() => setDeletingSchedule(schedule)}
                  aria-label={`Delete ${schedule.taskName}`}
                  className="hover:text-red-400"
                >
                  <DeleteIcon className="h-4 w-4" />
                </Button>
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Edit Modal */}
      <EditScheduleModal
        isOpen={!!editingSchedule}
        schedule={editingSchedule}
        onSubmit={handleEdit}
        onClose={() => setEditingSchedule(null)}
      />

      {/* Create Modal */}
      <CreateScheduleModal
        isOpen={isCreateOpen}
        onSubmit={handleCreate}
        onClose={() => setIsCreateOpen(false)}
      />

      {/* Delete Confirmation */}
      <ConfirmationModal
        isOpen={!!deletingSchedule}
        title="Delete Maintenance Plan"
        message={`Are you sure you want to delete "${deletingSchedule?.taskName}"? This cannot be undone.`}
        confirmButtonText="Delete"
        isDangerous
        onConfirm={handleDeleteConfirm}
        onCancel={() => setDeletingSchedule(null)}
      />
    </>
  );
}

export default MaintenancePlansTab;
