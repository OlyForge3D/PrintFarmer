import React, { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import { Input } from '@/common/components/ui/Input';
import { Select } from '@/common/components/ui/Select';
import { Textarea } from '@/common/components/ui/Textarea';
import { Checkbox } from '@/common/components/ui/Checkbox';
import type { CreateMaintenanceScheduleRequest } from '@/types/maintenance';
import type { MaintenanceSchedule } from '@/types/maintenance';
import { maintenanceService } from '@/services/maintenanceService';
import { CalendarDaysIcon } from '@heroicons/react/24/outline';

interface CreateScheduleModalProps {
  isOpen: boolean;
  printerId?: string;
  printerName?: string;
  onSubmit: (data: CreateMaintenanceScheduleRequest) => Promise<void>;
  onClose: () => void;
}

function getTemplateIntervalLabel(schedule: MaintenanceSchedule): string {
  if (schedule.intervalHours != null) return `${schedule.intervalHours}h`;
  if (schedule.intervalDays != null) return `${schedule.intervalDays}d`;
  return '';
}

// Priority options
const priorityOptions = [
  { value: '1', label: 'Low - Nice to have' },
  { value: '2', label: 'Medium - Should do' },
  { value: '3', label: 'High - Important' },
  { value: '4', label: 'Critical - Must do' },
];

/**
 * Modal for creating a new maintenance schedule.
 * Allows setting up recurring maintenance tasks based on print hours or calendar days.
 */
export function CreateScheduleModal({
  isOpen,
  printerId,
  printerName,
  onSubmit,
  onClose,
}: CreateScheduleModalProps) {
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Form state
  const [taskName, setTaskName] = useState('');
  const [description, setDescription] = useState('');
  const [component, setComponent] = useState('');
  const [intervalType, setIntervalType] = useState<'hours' | 'days'>('hours');
  const [intervalValue, setIntervalValue] = useState('');
  const [priority, setPriority] = useState('2'); // Default to Medium
  const [isActive, setIsActive] = useState(true);

  const { data: printerTemplates = [] } = useQuery({
    queryKey: ['maintenanceScheduleTemplates', printerId],
    enabled: isOpen && !!printerId,
    queryFn: () => maintenanceService.getPrinterScheduleTemplates(printerId!),
  });

  const { data: globalTemplates = [] } = useQuery({
    queryKey: ['maintenanceScheduleTemplates', 'global'],
    enabled: isOpen && !printerId,
    queryFn: () => maintenanceService.getAllScheduleTemplates(),
  });

  const templates = printerId ? printerTemplates : globalTemplates;

  const componentOptions = useMemo(() => {
    const unique = new Set<string>();
    for (const schedule of templates) {
      if (schedule.component) unique.add(schedule.component);
    }

    const result = Array.from(unique).sort((a, b) => a.localeCompare(b));
    if (!result.includes('Other')) result.push('Other');
    return result;
  }, [templates]);

  // Handle preset selection
  const handlePresetChange = (templateId: string) => {
    if (templateId === '') {
      // Custom entry - clear fields
      setTaskName('');
      setDescription('');
      setComponent('');
      setIntervalValue('');
      setIntervalType('hours');
    } else {
      const preset = templates.find(t => t.id === templateId);
      if (preset) {
        setTaskName(preset.taskName);
        setDescription(preset.description ?? '');
        setComponent(preset.component ?? '');
        if (preset.intervalHours != null) {
          setIntervalType('hours');
          setIntervalValue(preset.intervalHours.toString());
        } else if (preset.intervalDays != null) {
          setIntervalType('days');
          setIntervalValue(preset.intervalDays.toString());
        }
      }
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();

    if (!taskName.trim()) {
      setError('Task name is required');
      return;
    }

    if (!intervalValue || parseInt(intervalValue, 10) <= 0) {
      setError('Please enter a valid interval');
      return;
    }

    setIsSubmitting(true);
    setError(null);

    try {
      const request: CreateMaintenanceScheduleRequest = {
        taskName: taskName.trim(),
        description: description.trim() || null,
        componentName: component.trim() || null,
        printerId: printerId || null,
        intervalHours: intervalType === 'hours' ? parseInt(intervalValue, 10) : null,
        intervalDays: intervalType === 'days' ? parseInt(intervalValue, 10) : null,
        isActive,
      };

      await onSubmit(request);
      
      // Reset form
      setTaskName('');
      setDescription('');
      setComponent('');
      setIntervalValue('');
      setIntervalType('hours');
      setPriority('2');
      setIsActive(true);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create schedule');
    } finally {
      setIsSubmitting(false);
    }
  };

  const resetForm = () => {
    setTaskName('');
    setDescription('');
    setComponent('');
    setIntervalValue('');
    setIntervalType('hours');
    setPriority('2');
    setIsActive(true);
    setError(null);
  };

  const handleClose = () => {
    resetForm();
    onClose();
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title="Schedule Maintenance"
      titleIcon={<CalendarDaysIcon className="h-6 w-6 text-pf-primary" />}
      size="lg"
      isDisabled={isSubmitting}
      footer={
        <>
          <Button
            type="button"
            variant="subtle"
            onClick={handleClose}
            disabled={isSubmitting}
          >
            Cancel
          </Button>
          <Button
            type="submit"
            form="create-schedule-form"
            variant="primary"
            disabled={isSubmitting || !taskName.trim() || !intervalValue}
          >
            {isSubmitting ? 'Creating...' : 'Create Schedule'}
          </Button>
        </>
      }
    >
      {printerName && (
        <div className="mb-4">
          <p className="text-sm text-pf-text-secondary">
            Scheduling for: <span className="font-medium text-pf-text-primary">{printerName}</span>
          </p>
        </div>
      )}

      <form id="create-schedule-form" onSubmit={handleSubmit} className="space-y-4">
        {error && (
          <div className="p-3 bg-red-500/10 border border-red-500/30 rounded-lg text-red-500 text-sm">
            {error}
          </div>
        )}

        {/* Quick Start from Preset */}
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1">
            Quick Start (Optional)
          </label>
          <Select
            onChange={(e) => handlePresetChange(e.target.value)}
            className="w-full"
          >
            <option value="">Select a preset or create custom...</option>
            {templates.map((preset) => (
              <option key={preset.id} value={preset.id}>
                {preset.taskName}
                {preset.component ? ` (${preset.component})` : ''}
                {getTemplateIntervalLabel(preset) ? ` - ${getTemplateIntervalLabel(preset)}` : ''}
              </option>
            ))}
          </Select>
          <p className="text-xs text-pf-text-tertiary mt-1">
            Choose a preset to auto-fill recommended values
          </p>
        </div>

        <hr className="border-pf-border" />

        {/* Task Name */}
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1">
            Task Name <span className="text-red-500">*</span>
          </label>
          <Input
            value={taskName}
            onChange={(e) => setTaskName(e.target.value)}
            placeholder="e.g., Belt Tensioning, Nozzle Check"
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
            placeholder="What should be done during this maintenance?"
            className="w-full h-20"
          />
        </div>

        {/* Component */}
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1">
            Component
          </label>
          <Select
            value={component}
            onChange={(e) => setComponent(e.target.value)}
            className="w-full"
          >
            <option value="">Select component...</option>
            {componentOptions.map(c => (
              <option key={c} value={c}>{c}</option>
            ))}
          </Select>
        </div>

        {/* Interval */}
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1">
            Maintenance Interval <span className="text-red-500">*</span>
          </label>
          <div className="flex gap-2">
            <div className="flex-1">
              <Input
                type="number"
                min="1"
                value={intervalValue}
                onChange={(e) => setIntervalValue(e.target.value)}
                placeholder="Enter value"
                className="w-full"
                required
              />
            </div>
            <Select
              value={intervalType}
              onChange={(e) => setIntervalType(e.target.value as 'hours' | 'days')}
              containerClassName="w-40"
            >
              <option value="hours">Print Hours</option>
              <option value="days">Calendar Days</option>
            </Select>
          </div>
          <p className="text-xs text-pf-text-tertiary mt-1">
            {intervalType === 'hours' 
              ? 'Alert will trigger after this many hours of printing'
              : 'Alert will trigger this many days after last maintenance'}
          </p>
        </div>

        {/* Priority */}
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1">
            Priority
          </label>
          <Select
            value={priority}
            onChange={(e) => setPriority(e.target.value)}
            className="w-full"
          >
            {priorityOptions.map(opt => (
              <option key={opt.value} value={opt.value}>{opt.label}</option>
            ))}
          </Select>
        </div>

        {/* Active Toggle */}
        <div className="flex items-center gap-3">
          <Checkbox
            id="isActive"
            checked={isActive}
            onChange={(e) => setIsActive(e.target.checked)}
          />
          <label htmlFor="isActive" className="text-sm text-pf-text-primary">
            Schedule is active (will generate alerts)
          </label>
        </div>
      </form>
    </Modal>
  );
}

export default CreateScheduleModal;
