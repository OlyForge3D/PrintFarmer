import { useCallback, useRef, type KeyboardEvent } from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';
import type { SettingsMode } from './useSettingsMode';

interface SettingsModeToggleProps {
  mode: SettingsMode;
  onModeChange: (mode: SettingsMode) => void;
  /** Optional descriptive count shown as small helper text (e.g. "22 of 80 settings"). */
  helperText?: string;
}

const MODES: readonly { value: SettingsMode; label: string; description: string }[] = [
  {
    value: 'essential',
    label: 'Essential',
    description: 'The settings people actually change day to day',
  },
  {
    value: 'everything',
    label: 'Everything',
    description: 'Show every setting, including tuning knobs',
  },
];

/**
 * Segmented control for switching between Essential and Everything modes.
 *
 * Rendered as a WAI-ARIA `radiogroup` with roving `tabindex` — the currently
 * selected option is the group's single tab stop, and Left/Right arrows move
 * selection between options. Screen readers announce it as a grouped set of
 * radio buttons rather than two disconnected toggle buttons.
 */
export function SettingsModeToggle({ mode, onModeChange, helperText }: SettingsModeToggleProps) {
  const buttonRefs = useRef<Record<SettingsMode, HTMLButtonElement | null>>({
    essential: null,
    everything: null,
  });

  const handleKeyDown = useCallback(
    (event: KeyboardEvent<HTMLDivElement>) => {
      if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') {
        return;
      }
      event.preventDefault();
      const currentIndex = MODES.findIndex((m) => m.value === mode);
      const delta = event.key === 'ArrowRight' ? 1 : -1;
      const nextIndex = (currentIndex + delta + MODES.length) % MODES.length;
      const nextMode = MODES[nextIndex].value;
      onModeChange(nextMode);
      buttonRefs.current[nextMode]?.focus();
    },
    [mode, onModeChange],
  );

  return (
    <div className="flex items-center gap-3">
      <div
        role="radiogroup"
        aria-label="Settings visibility"
        onKeyDown={handleKeyDown}
        className="inline-flex items-center rounded-md border border-pf-border bg-pf-bg-0 p-0.5"
      >
        {MODES.map((option) => {
          const selected = option.value === mode;
          return (
            <Button
              key={option.value}
              ref={(el) => {
                buttonRefs.current[option.value] = el;
              }}
              type="button"
              variant="unstyled"
              role="radio"
              aria-checked={selected}
              aria-label={`${option.label}: ${option.description}`}
              tabIndex={selected ? 0 : -1}
              onClick={() => onModeChange(option.value)}
              className={clsx(
                'rounded px-3 py-1 text-sm font-medium transition-colors inline-flex items-center justify-center',
                'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-offset-1 focus-visible:ring-offset-pf-bg-0',
                selected
                  ? 'bg-pf-accent text-pf-bg-0 shadow-sm'
                  : 'text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-1',
              )}
            >
              {option.label}
            </Button>
          );
        })}
      </div>
      {helperText && (
        <span className="text-xs text-pf-text-secondary" aria-live="polite">
          {helperText}
        </span>
      )}
    </div>
  );
}
