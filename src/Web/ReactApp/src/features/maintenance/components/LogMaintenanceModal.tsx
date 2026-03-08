import React, { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import { Input } from '@/common/components/ui/Input';
import { Select } from '@/common/components/ui/Select';
import { Textarea } from '@/common/components/ui/Textarea';
import type { CreateMaintenanceLogRequest, PrinterMaintenanceScheduleDto, MaintenanceTaskDto } from '@/types/maintenance';
import { maintenancePlanService } from '@/services/maintenancePlanService';
import { WrenchIcon } from '@heroicons/react/24/outline';

interface LogMaintenanceModalProps {
  isOpen: boolean;
  printerId: string;
  printerName: string;
  deployments: PrinterMaintenanceScheduleDto[];
  onSubmit: (data: CreateMaintenanceLogRequest) => Promise<void>;
  onClose: () => void;
}

/**
 * Modal for logging maintenance activity on a printer.
 * Uses the V3 task catalog for task selection and links to deployed plans.
 */
export function LogMaintenanceModal({
  isOpen,
  printerId,
  printerName,
  deployments,
  onSubmit,
  onClose,
}: LogMaintenanceModalProps) {
  const { user } = useAuth();
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Form state
  const [selectedTaskId, setSelectedTaskId] = useState<string>('');
  const [taskName, setTaskName] = useState('');
  const [component, setComponent] = useState('');
  const [notes, setNotes] = useState('');
  const [durationMinutes, setDurationMinutes] = useState<string>('');
  const [cost, setCost] = useState<string>('');
  const [partsReplaced, setPartsReplaced] = useState('');

  // Load V3 task catalog for task selection
  const { data: catalogTasks = [] } = useQuery({
    queryKey: ['catalogTasks'],
    enabled: isOpen,
    queryFn: () => maintenancePlanService.getCatalogTasks(undefined, true),
  });

  // Group catalog tasks by category for the dropdown
  const tasksByCategory = useMemo(() => {
    const grouped = new Map<string, MaintenanceTaskDto[]>();
    for (const task of catalogTasks) {
      const cat = task.category || 'Other';
      const existing = grouped.get(cat) || [];
      existing.push(task);
      grouped.set(cat, existing);
    }
    return grouped;
  }, [catalogTasks]);

  // Derive unique components from catalog tasks
  const commonComponents = useMemo(() => {
    const unique = new Set<string>();
    for (const task of catalogTasks) {
      if (task.category?.trim()) unique.add(task.category);
    }
    const result = Array.from(unique).sort((a, b) => a.localeCompare(b));
    if (!result.includes('Other')) result.push('Other');
    return result;
  }, [catalogTasks]);

  // When task selection changes, update form fields
  const handleTaskChange = (value: string) => {
    if (value === '') {
      setSelectedTaskId('');
      setTaskName('');
      setComponent('');
    } else {
      const task = catalogTasks.find(t => t.id === value);
      if (task) {
        setSelectedTaskId(task.id);
        setTaskName(task.taskName);
        setComponent(task.category || '');
      }
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    
    if (!taskName.trim()) {
      setError('Task name is required');
      return;
    }

    setIsSubmitting(true);
    setError(null);

    try {
      // Link to the first active deployment for this printer (if any)
      const activeDeployment = deployments.find(d => d.isActive);

      const request: CreateMaintenanceLogRequest = {
        printerId,
        deploymentId: activeDeployment?.id ?? null,
        taskId: selectedTaskId || null,
        taskName: taskName.trim(),
        componentName: component.trim() || null,
        performedBy: user?.username || user?.email || 'Unknown',
        notes: notes.trim() || null,
        durationMinutes: durationMinutes ? parseInt(durationMinutes, 10) : null,
        cost: cost ? parseFloat(cost) : null,
        partsReplaced: partsReplaced.trim() || null,
        performedAt: new Date().toISOString(),
      };

      await onSubmit(request);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to log maintenance');
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Log Maintenance"
      titleIcon={<WrenchIcon className="h-6 w-6 text-pf-primary" />}
      size="xl"
      isDisabled={isSubmitting}
      footer={
        <>
          <Button
            type="button"
            variant="subtle"
            onClick={onClose}
            disabled={isSubmitting}
          >
            Cancel
          </Button>
          <Button
            type="submit"
            form="log-maintenance-form"
            variant="primary"
            disabled={isSubmitting || !taskName.trim()}
          >
            {isSubmitting ? 'Logging...' : 'Log Maintenance'}
          </Button>
        </>
      }
    >
      <div className="mb-2">
        <p className="text-sm text-pf-text-secondary">{printerName}</p>
      </div>

      {/* Form */}
      <form id="log-maintenance-form" onSubmit={handleSubmit} className="space-y-4">
        {error && (
          <div className="p-3 bg-pf-error/10 border border-pf-error/30 rounded-lg text-pf-error text-sm">
            {error}
          </div>
        )}

        {/* Maintenance Type Selection */}
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1">
            Maintenance Task
          </label>
          <Select
            value={selectedTaskId}
            onChange={(e) => handleTaskChange(e.target.value)}
            className="w-full"
          >
            <option value="">Custom / Ad-hoc Maintenance</option>
            
            {/* Tasks grouped by category from the V3 catalog */}
            {Array.from(tasksByCategory.entries()).map(([category, tasks]) => (
              <optgroup key={category} label={`🔧 ${category}`}>
                {tasks.map(task => (
                  <option key={task.id} value={task.id}>
                    {task.taskName}
                  </option>
                ))}
              </optgroup>
            ))}
          </Select>
          <p className="text-xs text-pf-text-tertiary mt-1">
            Select a task from the catalog, or enter custom maintenance
          </p>
        </div>

        {/* Task Name (editable if custom) */}
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1">
            Task Name <span className="text-pf-error">*</span>
          </label>
          <Input
            value={taskName}
            onChange={(e) => setTaskName(e.target.value)}
            placeholder="e.g., Nozzle Replacement, Belt Tensioning"
            disabled={!!selectedTaskId}
            required
          />
        </div>

        {/* Component */}
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1">
            Component
          </label>
          <div className="space-y-2">
            <Select
              value={component}
              onChange={(e) => setComponent(e.target.value)}
              className="w-full"
              disabled={!!selectedTaskId}
            >
              <option value="">Select component...</option>
              {commonComponents.map(c => (
                <option key={c} value={c}>{c}</option>
              ))}
            </Select>
            {!selectedTaskId && (
              <Input
                value={component}
                onChange={(e) => setComponent(e.target.value)}
                placeholder="Or type custom component"
              />
            )}
          </div>
        </div>

        {/* Notes */}
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1">
            Notes
          </label>
          <Textarea
            value={notes}
            onChange={(e) => setNotes(e.target.value)}
            placeholder="Describe what was done, any issues found, etc."
            rows={3}
          />
        </div>

        {/* Duration and Cost Row */}
        <div className="grid grid-cols-2 gap-4">
          <div>
            <label className="block text-sm font-medium text-pf-text-primary mb-1">
              Duration (minutes)
            </label>
            <Input
              type="number"
              min="0"
              value={durationMinutes}
              onChange={(e) => setDurationMinutes(e.target.value)}
              placeholder="30"
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-pf-text-primary mb-1">
              Cost ($)
            </label>
            <Input
              type="number"
              min="0"
              step="0.01"
              value={cost}
              onChange={(e) => setCost(e.target.value)}
              placeholder="0.00"
            />
          </div>
        </div>

        {/* Parts Replaced */}
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1">
            Parts Replaced
          </label>
          <Input
            value={partsReplaced}
            onChange={(e) => setPartsReplaced(e.target.value)}
            placeholder="e.g., 0.4mm brass nozzle, GT2 belt"
          />
        </div>

        {/* Performed By (read-only) */}
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1">
            Performed By
          </label>
          <Input
            value={user?.username || user?.email || 'Unknown'}
            disabled
            className="bg-pf-bg-dark/50"
          />
        </div>
      </form>
    </Modal>
  );
}

export default LogMaintenanceModal;
