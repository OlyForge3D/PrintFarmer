import { forwardRef } from 'react';
import clsx from 'clsx';
import { Button, Tooltip } from '@/common/components/ui';

export type SlicerSettingsLevel = 'Simple' | 'Basic' | 'Advanced';

interface SlicerModeToggleProps {
  levels: readonly SlicerSettingsLevel[];
  value: SlicerSettingsLevel;
  onChange: (value: SlicerSettingsLevel) => void;
  disabled?: boolean;
}

export const SlicerModeToggle = forwardRef<HTMLButtonElement, SlicerModeToggleProps>(
  function SlicerModeToggle({ levels, value, onChange, disabled }, ref) {
    if (levels.length < 2) return null;

    const index = levels.indexOf(value);
    const next = levels[(index + 1) % levels.length];

    return (
      <Tooltip content={`${value} settings · Switch to ${next}`} position="bottom" wrapperClassName="shrink-0">
        <Button
          ref={ref}
          variant="unstyled"
          type="button"
          onClick={() => onChange(next)}
          disabled={disabled}
          aria-label={`Settings view: ${value}`}
          className="inline-flex min-h-8 max-sm:min-h-11 pointer-coarse:min-h-11 items-center rounded px-1 transition-colors hover:bg-pf-bg-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent disabled:opacity-50"
        >
          <span className="flex items-center gap-1.5">
            <img src="/icons/orcaslicer-advanced.svg" alt="" className="h-4 w-4" />
            <span
              aria-hidden="true"
              data-pf-radius="full"
              className={clsx(
                'relative inline-flex h-3.5 items-center rounded-full px-0.5 transition-colors',
                index === 0 ? 'bg-pf-border' : index === levels.length - 1 ? 'bg-pf-accent-2' : 'bg-pf-accent-2/50',
              )}
            >
              {levels.map((level) => (
                <span key={level} className="flex h-full w-3 items-center justify-center">
                  <span className={clsx('h-1 w-1 rounded-full', index === 0 ? 'bg-pf-text-muted' : 'bg-white/70')} />
                </span>
              ))}
              <span
                className="absolute left-[0.1875rem] top-0.5 h-2.5 w-2.5 rounded-full bg-white shadow-sm transition-transform motion-reduce:transition-none"
                style={{ transform: `translateX(${index * 0.75}rem)` }}
              />
            </span>
          </span>
        </Button>
      </Tooltip>
    );
  },
);
