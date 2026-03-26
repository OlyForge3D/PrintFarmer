import React from 'react';
import { Button } from '@/common/components/ui/Button';
import { Input } from '@/common/components/ui/Input';
import { TIME_PERIOD_OPTIONS, type TimePeriodOption, type TimePeriodFilterValue } from './timePeriodOptions';

export type { TimePeriodOption, TimePeriodFilterValue };

export interface TimePeriodFilterProps {
  value: TimePeriodFilterValue;
  onChange: (value: TimePeriodFilterValue) => void;
  options?: readonly TimePeriodOption[];
}

export const TimePeriodFilter: React.FC<TimePeriodFilterProps> = ({
  value,
  onChange,
  options = TIME_PERIOD_OPTIONS,
}) => {
  const isCustom = value.type === 'custom';
  const presetDays = value.type === 'preset' ? value.days : undefined;
  const startDate = isCustom ? value.startDate : '';
  const endDate = isCustom ? value.endDate : '';

  const handleCustomToggle = () => {
    if (isCustom) {
      onChange({ type: 'preset', days: 30 });
    } else {
      const end = new Date().toISOString().split('T')[0];
      const start = new Date(Date.now() - 30 * 86_400_000).toISOString().split('T')[0];
      onChange({ type: 'custom', startDate: start, endDate: end });
    }
  };

  const handleStartChange = (newStart: string) => {
    if (newStart && endDate && newStart <= endDate) {
      onChange({ type: 'custom', startDate: newStart, endDate });
    }
  };

  const handleEndChange = (newEnd: string) => {
    if (startDate && newEnd && startDate <= newEnd) {
      onChange({ type: 'custom', startDate, endDate: newEnd });
    }
  };

  return (
    <div className="flex flex-wrap items-center gap-1" role="group" aria-label="Time period filter">
      {options.map((opt) => (
        <Button
          variant="unstyled"
          key={opt.label}
          onClick={() => onChange({ type: 'preset', days: opt.value })}
          className={`rounded-md px-3 py-1.5 text-sm font-medium transition-colors ${
            !isCustom && presetDays === opt.value
              ? 'bg-pf-primary text-white'
              : 'bg-pf-surface text-pf-text-secondary hover:bg-pf-hover'
          }`}
          aria-pressed={!isCustom && presetDays === opt.value}
        >
          {opt.label}
        </Button>
      ))}
      <Button
        variant="unstyled"
        onClick={handleCustomToggle}
        className={`rounded-md px-3 py-1.5 text-sm font-medium transition-colors ${
          isCustom
            ? 'bg-pf-primary text-white'
            : 'bg-pf-surface text-pf-text-secondary hover:bg-pf-hover'
        }`}
        aria-pressed={isCustom}
      >
        Custom
      </Button>
      {isCustom && (
        <div className="flex items-center gap-2 ml-2">
          <Input
            type="date"
            value={startDate}
            onChange={(e) => handleStartChange(e.target.value)}
            className="w-36 text-sm"
            aria-label="Start date"
            max={endDate || undefined}
          />
          <span className="text-pf-text-secondary text-sm">to</span>
          <Input
            type="date"
            value={endDate}
            onChange={(e) => handleEndChange(e.target.value)}
            className="w-36 text-sm"
            aria-label="End date"
            min={startDate || undefined}
          />
        </div>
      )}
    </div>
  );
};
