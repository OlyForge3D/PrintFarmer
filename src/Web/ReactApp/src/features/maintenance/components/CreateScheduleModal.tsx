import React, { useState } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import { Input } from '@/common/components/ui/Input';
import { Select } from '@/common/components/ui/Select';
import type { CreateMaintenanceScheduleRequest } from '@/types/maintenance';
import { CalendarDaysIcon } from '@heroicons/react/24/outline';

interface CreateScheduleModalProps {
  isOpen: boolean;
  printerId?: string;
  printerName?: string;
  onSubmit: (data: CreateMaintenanceScheduleRequest) => Promise<void>;
  onClose: () => void;
}

// Common preset schedules with recommended intervals
const presetSchedules = [
  { taskName: 'Nozzle Inspection', component: 'Nozzle', intervalHours: 100, intervalDays: null, description: 'Check for wear and clogs' },
  { taskName: 'Belt Tensioning', component: 'Belts', intervalHours: 500, intervalDays: null, description: 'Check and adjust belt tension' },
  { taskName: 'Lubrication', component: 'Bearings', intervalHours: 200, intervalDays: null, description: 'Lubricate linear rails and bearings' },
  { taskName: 'Bed Leveling Check', component: 'Bed', intervalHours: null, intervalDays: 30, description: 'Verify bed mesh is accurate' },
  { taskName: 'Fan Cleaning', component: 'Fans', intervalHours: null, intervalDays: 30, description: 'Clean dust from cooling fans' },
  { taskName: 'Extruder Cleaning', component: 'Extruder', intervalHours: 200, intervalDays: null, description: 'Clean extruder gears and tension spring' },
  { taskName: 'Firmware Update Check', component: 'Electronics', intervalHours: null, intervalDays: 90, description: 'Check for firmware updates' },
  { taskName: 'Z-Axis Calibration', component: 'Z-Axis', intervalHours: null, intervalDays: 60, description: 'Verify Z-axis alignment' },
  { taskName: 'General Cleaning', component: 'Frame', intervalHours: null, intervalDays: 7, description: 'Clean printer exterior and build plate' },
  { taskName: 'PTFE Tube Check', component: 'Extruder', intervalHours: 500, intervalDays: null, description: 'Inspect PTFE tube for wear' },
  { taskName: 'Thermistor Check', component: 'Hotend', intervalHours: 1000, intervalDays: null, description: 'Verify temperature readings are accurate' },
];

// Common components for selection
const commonComponents = [
  'Hotend',
  'Nozzle',
  'Bed',
  'Belts',
  'Bearings',
  'Fans',
  'Extruder',
  'Z-Axis',
  'Electronics',
  'Frame',
  'Other',
];

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

  // Handle preset selection
  const handlePresetChange = (presetIndex: string) => {
    if (presetIndex === '') {
      // Custom entry - clear fields
      setTaskName('');
      setDescription('');
      setComponent('');
      setIntervalValue('');
      setIntervalType('hours');
    } else {
      const preset = presetSchedules[parseInt(presetIndex, 10)];
      if (preset) {
        setTaskName(preset.taskName);
        setDescription(preset.description);
        setComponent(preset.component);
        if (preset.intervalHours) {
          setIntervalType('hours');
          setIntervalValue(preset.intervalHours.toString());
        } else if (preset.intervalDays) {
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
            {presetSchedules.map((preset, index) => (
              <option key={index} value={index}>
                {preset.taskName} ({preset.component}) - {preset.intervalHours ? `${preset.intervalHours}h` : `${preset.intervalDays}d`}
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
          <textarea
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="What should be done during this maintenance?"
            className="w-full h-20 px-3 py-2 bg-pf-bg-dark border border-pf-border rounded-lg text-pf-text-primary placeholder:text-pf-text-tertiary focus:outline-none focus:ring-2 focus:ring-pf-primary"
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
            {commonComponents.map(c => (
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
            <Input
              type="number"
              min="1"
              value={intervalValue}
              onChange={(e) => setIntervalValue(e.target.value)}
              placeholder="Enter value"
              className="flex-1"
              required
            />
            <Select
              value={intervalType}
              onChange={(e) => setIntervalType(e.target.value as 'hours' | 'days')}
              className="w-40"
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
          <input
            type="checkbox"
            id="isActive"
            checked={isActive}
            onChange={(e) => setIsActive(e.target.checked)}
            className="w-4 h-4 rounded border-pf-border text-pf-primary focus:ring-pf-primary"
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
