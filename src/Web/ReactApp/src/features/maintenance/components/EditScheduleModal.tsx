/**
 * EditScheduleModal Component
 *
 * Modal for editing an existing maintenance schedule.
 * Pre-populates the form with current schedule values and calls updateSchedule on submit.
 */

import React, { useEffect, useState } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import { Input } from '@/common/components/ui/Input';
import { Select } from '@/common/components/ui/Select';
import { Textarea } from '@/common/components/ui/Textarea';
import { Checkbox } from '@/common/components/ui/Checkbox';
import type { MaintenanceSchedule, UpdateMaintenanceScheduleRequest } from '@/types/maintenance';
import { EditIcon } from '@/common/components/icons/MdiIcons';

interface EditScheduleModalProps {
  isOpen: boolean;
  schedule: MaintenanceSchedule | null;
  onSubmit: (id: string, data: UpdateMaintenanceScheduleRequest) => Promise<void>;
  onClose: () => void;
}

const priorityOptions = [
  { value: '1', label: 'Low - Nice to have' },
  { value: '2', label: 'Medium - Should do' },
  { value: '3', label: 'High - Important' },
  { value: '4', label: 'Critical - Must do' },
];

const componentOptions = [
  'Motion System',
  'Hotend',
  'Extruder',
  'Bed',
  'Belts',
  'Fans',
  'Electronics',
  'Air Filtration',
  'Frame',
  'Other',
];

export function EditScheduleModal({
  isOpen,
  schedule,
  onSubmit,
  onClose,
}: EditScheduleModalProps) {
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [taskName, setTaskName] = useState('');
  const [description, setDescription] = useState('');
  const [component, setComponent] = useState('');
  const [intervalType, setIntervalType] = useState<'hours' | 'days'>('hours');
  const [intervalValue, setIntervalValue] = useState('');
  const [priority, setPriority] = useState('2');
  const [isActive, setIsActive] = useState(true);

  // Populate form when schedule changes
  useEffect(() => {
    if (schedule) {
      setTaskName(schedule.taskName);
      setDescription(schedule.description ?? '');
      setComponent(schedule.component ?? '');
      setPriority(String(schedule.priority));
      setIsActive(schedule.isActive);

      if (schedule.intervalHours != null) {
        setIntervalType('hours');
        setIntervalValue(String(schedule.intervalHours));
      } else if (schedule.intervalDays != null) {
        setIntervalType('days');
        setIntervalValue(String(schedule.intervalDays));
      } else {
        setIntervalType('hours');
        setIntervalValue('');
      }
      setError(null);
    }
  }, [schedule]);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!schedule) return;

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
      const request: UpdateMaintenanceScheduleRequest = {
        taskName: taskName.trim(),
        description: description.trim() || null,
        componentName: component.trim() || null,
        intervalHours: intervalType === 'hours' ? parseInt(intervalValue, 10) : null,
        intervalDays: intervalType === 'days' ? parseInt(intervalValue, 10) : null,
        isActive,
      };

      await onSubmit(schedule.id, request);
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to update schedule');
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Edit Maintenance Schedule"
      titleIcon={<EditIcon className="h-6 w-6 text-pf-primary" />}
      size="lg"
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
            form="edit-schedule-form"
            variant="primary"
            disabled={isSubmitting || !taskName.trim() || !intervalValue}
          >
            {isSubmitting ? 'Saving...' : 'Save Changes'}
          </Button>
        </>
      }
    >
      <form id="edit-schedule-form" onSubmit={handleSubmit} className="space-y-4">
        {error && (
          <div className="p-3 bg-red-500/10 border border-red-500/30 rounded-lg text-red-500 text-sm">
            {error}
          </div>
        )}

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
            {componentOptions.map((c) => (
              <option key={c} value={c}>
                {c}
              </option>
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
            {priorityOptions.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </Select>
        </div>

        {/* Active Toggle */}
        <div className="flex items-center gap-3">
          <Checkbox
            id="editIsActive"
            checked={isActive}
            onChange={(e) => setIsActive(e.target.checked)}
          />
          <label htmlFor="editIsActive" className="text-sm text-pf-text-primary">
            Schedule is active (will generate alerts)
          </label>
        </div>
      </form>
    </Modal>
  );
}

export default EditScheduleModal;
