import React, { useState } from 'react';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import { Input } from '@/common/components/ui/Input';
import { Select } from '@/common/components/ui/Select';
import { Textarea } from '@/common/components/ui/Textarea';
import type { MaintenanceSchedule, CreateMaintenanceLogRequest } from '@/types/maintenance';
import { WrenchIcon } from '@heroicons/react/24/outline';

interface LogMaintenanceModalProps {
  isOpen: boolean;
  printerId: string;
  printerName: string;
  schedule?: MaintenanceSchedule | null;
  schedules: MaintenanceSchedule[];
  onSubmit: (data: CreateMaintenanceLogRequest) => Promise<void>;
  onClose: () => void;
}

/**
 * Modal for logging maintenance activity on a printer.
 * Can be pre-populated with a schedule or allow custom entry.
 */
export function LogMaintenanceModal({
  isOpen,
  printerId,
  printerName,
  schedule,
  schedules,
  onSubmit,
  onClose,
}: LogMaintenanceModalProps) {
  const { user } = useAuth();
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Form state
  const [selectedScheduleId, setSelectedScheduleId] = useState<string>(schedule?.id || '');
  const [taskName, setTaskName] = useState(schedule?.taskName || '');
  const [component, setComponent] = useState(schedule?.component || '');
  const [notes, setNotes] = useState('');
  const [durationMinutes, setDurationMinutes] = useState<string>('');
  const [cost, setCost] = useState<string>('');
  const [partsReplaced, setPartsReplaced] = useState('');

  // Common preset maintenance tasks (not tied to schedules)
  const commonMaintenanceTasks = [
    { taskName: 'Nozzle Change', component: 'Nozzle' },
    { taskName: 'Nozzle Cleaning', component: 'Nozzle' },
    { taskName: 'Belt Tensioning', component: 'Belts' },
    { taskName: 'Lubrication', component: 'Bearings' },
    { taskName: 'Bed Leveling', component: 'Bed' },
    { taskName: 'Bed Cleaning', component: 'Bed' },
    { taskName: 'PEI Sheet Replacement', component: 'Bed' },
    { taskName: 'Hotend Cleaning', component: 'Hotend' },
    { taskName: 'Heatbreak Replacement', component: 'Hotend' },
    { taskName: 'Fan Replacement', component: 'Fans' },
    { taskName: 'Extruder Gear Cleaning', component: 'Extruder' },
    { taskName: 'Firmware Update', component: 'Electronics' },
    { taskName: 'Z-Axis Calibration', component: 'Z-Axis' },
    { taskName: 'General Cleaning', component: 'Frame' },
    { taskName: 'PTFE Tube Replacement', component: 'Extruder' },
    { taskName: 'Thermistor Replacement', component: 'Hotend' },
    { taskName: 'Heater Cartridge Replacement', component: 'Hotend' },
  ];

  // When schedule selection changes, update form fields
  const handleScheduleChange = (value: string) => {
    if (value === '') {
      // Custom entry - clear pre-filled values
      setSelectedScheduleId('');
      setTaskName('');
      setComponent('');
    } else if (value.startsWith('preset:')) {
      // Preset task selected
      const presetIndex = parseInt(value.replace('preset:', ''), 10);
      const preset = commonMaintenanceTasks[presetIndex];
      if (preset) {
        setSelectedScheduleId(''); // No schedule ID for presets
        setTaskName(preset.taskName);
        setComponent(preset.component);
      }
    } else {
      // Schedule selected
      setSelectedScheduleId(value);
      const selected = schedules.find(s => s.id === value);
      if (selected) {
        setTaskName(selected.taskName);
        setComponent(selected.component || '');
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
      const request: CreateMaintenanceLogRequest = {
        printerId,
        scheduleId: selectedScheduleId || null,
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

  // Common components for quick selection
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

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Log Maintenance"
      titleIcon={<WrenchIcon className="h-6 w-6 text-pf-primary" />}
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
          <div className="p-3 bg-red-500/10 border border-red-500/30 rounded-lg text-red-500 text-sm">
            {error}
          </div>
        )}

        {/* Maintenance Type Selection */}
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1">
            Maintenance Type
          </label>
          <Select
            value={selectedScheduleId}
            onChange={(e) => handleScheduleChange(e.target.value)}
            className="w-full"
          >
            <option value="">Custom / Ad-hoc Maintenance</option>
            
            {/* Scheduled Tasks (from database) */}
            {schedules.filter(s => s.isActive).length > 0 && (
              <optgroup label="📅 Scheduled Tasks">
                {schedules.filter(s => s.isActive).map(s => (
                  <option key={s.id} value={s.id}>
                    {s.taskName} {s.component ? `(${s.component})` : ''}
                  </option>
                ))}
              </optgroup>
            )}
            
            {/* Common Preset Tasks */}
            <optgroup label="🔧 Common Tasks">
              {commonMaintenanceTasks.map((task, index) => (
                <option key={`preset-${index}`} value={`preset:${index}`}>
                  {task.taskName} ({task.component})
                </option>
              ))}
            </optgroup>
          </Select>
          <p className="text-xs text-pf-text-tertiary mt-1">
            Select a scheduled task, common preset, or enter custom maintenance
          </p>
        </div>

        {/* Task Name (editable if custom) */}
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1">
            Task Name <span className="text-red-500">*</span>
          </label>
          <Input
            value={taskName}
            onChange={(e) => setTaskName(e.target.value)}
            placeholder="e.g., Nozzle Replacement, Belt Tensioning"
            disabled={!!selectedScheduleId}
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
              disabled={!!selectedScheduleId && !!schedule?.component}
            >
              <option value="">Select component...</option>
              {commonComponents.map(c => (
                <option key={c} value={c}>{c}</option>
              ))}
            </Select>
            {!selectedScheduleId && (
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
            className="w-full h-24"
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
