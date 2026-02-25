import React, { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import { Input } from '@/common/components/ui/Input';
import { Textarea } from '@/common/components/ui/Textarea';
import { maintenanceService } from '@/services/maintenanceService';
import type { MaintenanceSchedule, UpdateMaintenanceScheduleRequest } from '@/types/maintenance';

interface TemplateFormState {
  taskName: string;
  description: string;
  component: string;
  intervalHours: string;
  intervalDays: string;
}

function getMotionTypeLabel(motionType: number | null | undefined): string {
  switch (motionType) {
    case 0:
      return 'Cartesian';
    case 1:
      return 'CoreXY';
    case 2:
      return 'Delta';
    default:
      return 'Unknown';
  }
}

function getScopeLabel(template: MaintenanceSchedule): string {
  if (template.printerModelId) {
    return 'Model';
  }

  if (template.motionType != null) {
    return `Motion: ${getMotionTypeLabel(template.motionType)}`;
  }

  if (template.manufacturerId) {
    return 'Manufacturer';
  }

  return 'Global';
}

function getIntervalLabel(template: MaintenanceSchedule): string {
  const parts: string[] = [];
  if (template.intervalHours != null) {
    parts.push(`${template.intervalHours}h`);
  }

  if (template.intervalDays != null) {
    parts.push(`${template.intervalDays}d`);
  }

  return parts.length > 0 ? parts.join(' or ') : 'Not set';
}

function toFormState(template: MaintenanceSchedule): TemplateFormState {
  return {
    taskName: template.taskName,
    description: template.description ?? '',
    component: template.component ?? '',
    intervalHours: template.intervalHours?.toString() ?? '',
    intervalDays: template.intervalDays?.toString() ?? '',
  };
}

function parsePositiveNumber(value: string): number | null {
  if (!value.trim()) {
    return null;
  }

  const parsed = Number(value);
  if (!Number.isFinite(parsed) || parsed <= 0) {
    return null;
  }

  return Math.floor(parsed);
}

export function MaintenanceTemplateManager() {
  const queryClient = useQueryClient();
  const [editingTemplate, setEditingTemplate] = useState<MaintenanceSchedule | null>(null);
  const [formState, setFormState] = useState<TemplateFormState | null>(null);

  const { data: templates = [], isLoading, error } = useQuery({
    queryKey: ['maintenanceScheduleTemplates', 'all'],
    queryFn: () => maintenanceService.getAllScheduleTemplates(),
  });

  const sortedTemplates = useMemo(() => {
    return [...templates].sort((a, b) => {
      const scopeCompare = getScopeLabel(a).localeCompare(getScopeLabel(b));
      if (scopeCompare !== 0) {
        return scopeCompare;
      }

      const componentCompare = (a.component ?? '').localeCompare(b.component ?? '');
      if (componentCompare !== 0) {
        return componentCompare;
      }

      return a.taskName.localeCompare(b.taskName);
    });
  }, [templates]);

  const updateTemplateMutation = useMutation({
    mutationFn: ({ id, request }: { id: string; request: UpdateMaintenanceScheduleRequest }) =>
      maintenanceService.updateSchedule(id, request),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['maintenanceScheduleTemplates'] }),
        queryClient.invalidateQueries({ queryKey: ['upcoming-maintenance'] }),
      ]);
      toast.success('Maintenance template updated');
      setEditingTemplate(null);
      setFormState(null);
    },
    onError: (err: Error) => {
      toast.error(`Failed to update template: ${err.message}`);
    },
  });

  const openEditModal = (template: MaintenanceSchedule) => {
    setEditingTemplate(template);
    setFormState(toFormState(template));
  };

  const closeEditModal = () => {
    setEditingTemplate(null);
    setFormState(null);
  };

  const handleSubmit = async (event: React.FormEvent) => {
    event.preventDefault();

    if (!editingTemplate || !formState) {
      return;
    }

    const taskName = formState.taskName.trim();
    if (!taskName) {
      toast.error('Task name is required');
      return;
    }

    const intervalHours = parsePositiveNumber(formState.intervalHours);
    const intervalDays = parsePositiveNumber(formState.intervalDays);
    if (intervalHours == null && intervalDays == null) {
      toast.error('At least one interval (hours or days) is required');
      return;
    }

    const request: UpdateMaintenanceScheduleRequest = {
      taskName,
      description: formState.description.trim() || null,
      componentName: formState.component.trim() || null,
      intervalHours,
      intervalDays,
    };

    await updateTemplateMutation.mutateAsync({ id: editingTemplate.id, request });
  };

  return (
    <div className="space-y-4">
      <p className="text-sm text-pf-text-secondary">
        Recommended templates are used by <strong>Apply Recommended</strong> to create printer-specific schedules.
      </p>

      {isLoading ? (
        <div className="rounded-lg border border-pf-border bg-pf-bg-1 p-6 text-sm text-pf-text-secondary">
          Loading maintenance templates...
        </div>
      ) : error ? (
        <div className="rounded-lg border border-red-500/30 bg-red-500/10 p-6 text-sm text-red-400">
          Failed to load templates: {error.message}
        </div>
      ) : sortedTemplates.length === 0 ? (
        <div className="rounded-lg border border-yellow-500/30 bg-yellow-500/10 p-6 text-sm text-yellow-300">
          No templates found. Verify maintenance schedule seeding has completed.
        </div>
      ) : (
        <div className="overflow-x-auto rounded-lg border border-pf-border">
          <table className="min-w-full text-sm">
            <thead className="bg-pf-bg-2 text-pf-text-secondary">
              <tr>
                <th className="px-4 py-3 text-left font-medium">Task</th>
                <th className="px-4 py-3 text-left font-medium">Scope</th>
                <th className="px-4 py-3 text-left font-medium">Component</th>
                <th className="px-4 py-3 text-left font-medium">Interval</th>
                <th className="px-4 py-3 text-left font-medium">Active</th>
                <th className="px-4 py-3 text-right font-medium">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-pf-border">
              {sortedTemplates.map((template) => (
                <tr key={template.id} className="bg-pf-bg-0">
                  <td className="px-4 py-3">
                    <div className="font-medium text-pf-text-primary">{template.taskName}</div>
                    {template.description && (
                      <div className="mt-1 max-w-xl truncate text-xs text-pf-text-tertiary">{template.description}</div>
                    )}
                  </td>
                  <td className="px-4 py-3 text-pf-text-secondary">{getScopeLabel(template)}</td>
                  <td className="px-4 py-3 text-pf-text-secondary">{template.component ?? 'General'}</td>
                  <td className="px-4 py-3 text-pf-text-secondary">{getIntervalLabel(template)}</td>
                  <td className="px-4 py-3 text-pf-text-secondary">{template.isActive ? 'Yes' : 'No'}</td>
                  <td className="px-4 py-3 text-right">
                    <Button size="sm" variant="secondary" onClick={() => openEditModal(template)}>
                      Edit
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {editingTemplate && formState && (
        <Modal
          isOpen
          onClose={closeEditModal}
          title={`Edit Template: ${editingTemplate.taskName}`}
          size="lg"
          isDisabled={updateTemplateMutation.isPending}
          footer={
            <>
              <Button type="button" variant="subtle" onClick={closeEditModal} disabled={updateTemplateMutation.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                form="edit-maintenance-template-form"
                variant="primary"
                disabled={updateTemplateMutation.isPending}
              >
                {updateTemplateMutation.isPending ? 'Saving...' : 'Save Changes'}
              </Button>
            </>
          }
        >
          <form id="edit-maintenance-template-form" onSubmit={handleSubmit} className="space-y-4">
            <div>
              <label className="mb-1 block text-sm font-medium text-pf-text-primary">Task Name</label>
              <Input
                value={formState.taskName}
                onChange={(e) => setFormState((prev) => (prev ? { ...prev, taskName: e.target.value } : prev))}
                required
              />
            </div>

            <div>
              <label className="mb-1 block text-sm font-medium text-pf-text-primary">Description</label>
              <Textarea
                value={formState.description}
                onChange={(e) => setFormState((prev) => (prev ? { ...prev, description: e.target.value } : prev))}
                className="h-20 w-full"
              />
            </div>

            <div>
              <label className="mb-1 block text-sm font-medium text-pf-text-primary">Component</label>
              <Input
                value={formState.component}
                onChange={(e) => setFormState((prev) => (prev ? { ...prev, component: e.target.value } : prev))}
                placeholder="e.g., Motion System"
              />
            </div>

            <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
              <div>
                <label className="mb-1 block text-sm font-medium text-pf-text-primary">Interval (Hours)</label>
                <Input
                  type="number"
                  min="1"
                  value={formState.intervalHours}
                  onChange={(e) => setFormState((prev) => (prev ? { ...prev, intervalHours: e.target.value } : prev))}
                />
              </div>
              <div>
                <label className="mb-1 block text-sm font-medium text-pf-text-primary">Interval (Days)</label>
                <Input
                  type="number"
                  min="1"
                  value={formState.intervalDays}
                  onChange={(e) => setFormState((prev) => (prev ? { ...prev, intervalDays: e.target.value } : prev))}
                />
              </div>
            </div>
          </form>
        </Modal>
      )}
    </div>
  );
}

export default MaintenanceTemplateManager;
