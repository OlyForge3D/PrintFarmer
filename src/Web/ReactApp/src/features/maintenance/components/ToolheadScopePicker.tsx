import React, { useId, useMemo } from 'react';
import clsx from 'clsx';
import { RadioGroup, type RadioOption } from '@/common/components/ui/RadioGroup';
import {
  selectMaintenanceEligibleToolheads,
  type MaintenanceEligibleToolhead,
} from '@/features/printers/utils/isEligibleMaintenanceToolhead';
import type { ToolheadDto } from '@/types/api';
import { PRINTER_WIDE_SCOPE, type ToolheadScopeValue } from './toolheadScope';

export interface ToolheadScopePickerProps {
  /** Physical + gate toolheads for the printer. Only eligible ones are shown. */
  toolheads?: readonly (MaintenanceEligibleToolhead | ToolheadDto)[] | null;
  /** Currently selected scope. */
  value: ToolheadScopeValue;
  /** Called with the new scope. Pass `null` back through `toolheadIdFromScope`. */
  onChange: (value: ToolheadScopeValue) => void;
  /** Optional group label (visible). Defaults to "Maintenance scope". */
  label?: string;
  /** Optional helper text shown under the group. */
  helperText?: string;
  /** Optional testid override. */
  'data-testid'?: string;
  /** Optional container class. */
  className?: string;
  /** Disable all radios (e.g. while a mutation is pending). */
  disabled?: boolean;
  /**
   * When true (default), collapses to a static "Printer-wide" label if the
   * printer has fewer than 2 eligible toolheads. Set to false to always render
   * the radiogroup even for single-toolhead printers.
   */
  hideWhenSingle?: boolean;
}

/**
 * Accessible per-toolhead scope picker used across the maintenance surface
 * (deployment, logging, filtering). Renders "Printer-wide" plus one radio per
 * eligible physical toolhead; MMU/AMS gates are filtered out unless the API
 * explicitly opts them in via `supportsMaintenanceScope`.
 */
export function ToolheadScopePicker({
  toolheads,
  value,
  onChange,
  label = 'Maintenance scope',
  helperText,
  'data-testid': testId,
  className,
  disabled,
  hideWhenSingle = true,
}: ToolheadScopePickerProps) {
  const groupId = useId();
  const labelId = `${groupId}-label`;
  const helperId = helperText ? `${groupId}-helper` : undefined;

  const eligible = useMemo(() => selectMaintenanceEligibleToolheads(toolheads), [toolheads]);

  const options: RadioOption[] = useMemo(() => {
    const opts: RadioOption[] = [{ value: PRINTER_WIDE_SCOPE, label: 'Printer-wide' }];
    for (const th of eligible) {
      opts.push({ value: th.id, label: describeToolhead(th) });
    }
    return opts;
  }, [eligible]);

  // Collapse for single-toolhead printers to avoid a pointless one-radio group.
  if (hideWhenSingle && eligible.length <= 1) {
    return (
      <div className={clsx('text-sm text-pf-text-secondary', className)} data-testid={testId}>
        <span className="sr-only">{label}: </span>
        Printer-wide (only one maintenance target)
      </div>
    );
  }

  return (
    <div className={className} data-testid={testId}>
      <div id={labelId} className="text-sm font-medium text-pf-text-primary mb-2">
        {label}
      </div>
      <div role="group" aria-labelledby={labelId} aria-describedby={helperId}>
        <RadioGroup
          name={groupId}
          options={options}
          value={value}
          onChange={onChange}
          disabled={disabled}
          direction="vertical"
        />
      </div>
      {helperText && (
        <p id={helperId} className="text-xs text-pf-text-tertiary mt-1">
          {helperText}
        </p>
      )}
    </div>
  );
}

function describeToolhead(th: MaintenanceEligibleToolhead): string {
  const parts: string[] = [];
  if (typeof th.index === 'number') {
    parts.push(`T${th.index}`);
  }
  if (th.name && th.name.trim()) {
    parts.push(th.name.trim());
  } else if (th.toolheadModelDefName && th.toolheadModelDefName.trim()) {
    parts.push(th.toolheadModelDefName.trim());
  }
  if (parts.length === 0) {
    parts.push(th.id);
  }
  return parts.join(' · ');
}

export default ToolheadScopePicker;
