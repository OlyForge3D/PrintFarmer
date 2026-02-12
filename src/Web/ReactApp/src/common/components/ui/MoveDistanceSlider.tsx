import React from 'react';

const STEP_VALUES = [1, 5, 10, 25, 50, 100];

interface MoveDistanceSliderProps {
  /** Current step value in mm */
  value: number;
  /** Callback when step changes */
  onChange: (value: number) => void;
  /** Whether the control is disabled */
  disabled?: boolean;
}

/**
 * Discrete step slider for selecting move distance (mm).
 * Snaps to predefined values: 1, 5, 10, 25, 50, 100mm.
 * Used in printer movement controls (sidebar and detailed card).
 */
export function MoveDistanceSlider({ value, onChange, disabled = false }: MoveDistanceSliderProps) {
  const currentIndex = STEP_VALUES.indexOf(value);
  const sliderIndex = currentIndex >= 0 ? currentIndex : STEP_VALUES.findIndex(v => v >= value);

  const handleSliderChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const idx = Number(e.target.value);
    onChange(STEP_VALUES[idx]);
  };

  return (
    <div className="flex items-center gap-2 w-full">
      <span className="text-[10px] text-pf-text-tertiary shrink-0">1</span>
      <input
        type="range"
        min={0}
        max={STEP_VALUES.length - 1}
        step={1}
        value={sliderIndex >= 0 ? sliderIndex : 0}
        onChange={handleSliderChange}
        disabled={disabled}
        className="flex-1 h-1.5 accent-pf-accent cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed"
        aria-label="Move distance in millimeters"
      />
      <span className="text-[10px] text-pf-text-tertiary shrink-0">100</span>
      <span className="text-xs font-bold text-pf-text-primary tabular-nums min-w-[3.5ch] text-right">{value}mm</span>
    </div>
  );
}
