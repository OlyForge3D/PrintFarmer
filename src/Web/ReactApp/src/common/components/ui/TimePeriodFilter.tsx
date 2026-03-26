import React from 'react';
import { Button } from '@/common/components/ui/Button';
import { TIME_PERIOD_OPTIONS, type TimePeriodOption } from './timePeriodOptions';

export type { TimePeriodOption };

export interface TimePeriodFilterProps {
  value: number | undefined;
  onChange: (days: number | undefined) => void;
  options?: readonly TimePeriodOption[];
}

export const TimePeriodFilter: React.FC<TimePeriodFilterProps> = ({
  value,
  onChange,
  options = TIME_PERIOD_OPTIONS,
}) => (
  <div className="flex gap-1" role="group" aria-label="Time period filter">
    {options.map((opt) => (
      <Button
        variant="unstyled"
        key={opt.label}
        onClick={() => onChange(opt.value)}
        className={`rounded-md px-3 py-1.5 text-sm font-medium transition-colors ${
          value === opt.value
            ? 'bg-pf-primary text-white'
            : 'bg-pf-surface text-pf-text-secondary hover:bg-pf-hover'
        }`}
        aria-pressed={value === opt.value}
      >
        {opt.label}
      </Button>
    ))}
  </div>
);
