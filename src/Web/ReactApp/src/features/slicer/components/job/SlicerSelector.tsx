import React from 'react';
import { Select } from '@/common/components/ui';
import type { SlicerEngineOption } from './types';

interface SlicerSelectorProps {
  /** Currently selected slicer ID (1=OrcaSlicer, 2=PrusaSlicer) */
  selectedSlicerId: number;
  /** Callback when slicer selection changes */
  onSlicerChange: (slicerId: number) => void;
  /** Available slicer engine options */
  engineOptions: SlicerEngineOption[];
  /** Optional CSS class name */
  className?: string;
}

/**
 * Slicer engine selection dropdown.
 * Displays available slicers with their versions.
 */
export const SlicerSelector: React.FC<SlicerSelectorProps> = ({
  selectedSlicerId,
  onSlicerChange,
  engineOptions,
  className
}) => {
  return (
    <div className={`bg-pf-panel border border-pf-border rounded-lg p-4 ${className ?? ''}`}>
      <label className="block text-sm font-semibold text-pf-text-primary mb-2">Slicer</label>
      <Select
        value={selectedSlicerId}
        onChange={e => onSlicerChange(Number(e.target.value))}
        className="w-full"
      >
        {engineOptions.map(opt => (
          <option key={opt.value} value={opt.value}>{opt.label}</option>
        ))}
      </Select>
    </div>
  );
};

export default SlicerSelector;
