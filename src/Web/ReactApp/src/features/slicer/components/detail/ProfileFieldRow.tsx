import React from 'react';
import clsx from 'clsx';
import type { ProfileFieldMetadata } from '@/types/api';
import { InheritanceBadge } from './InheritanceBadge';

interface ProfileFieldRowProps {
  field: ProfileFieldMetadata;
  value: unknown;
  parentValue?: unknown;
  hasParent: boolean;
  parentName?: string;
}

/**
 * Individual readonly field display row with inheritance indicator
 */
export const ProfileFieldRow: React.FC<ProfileFieldRowProps> = ({
  field,
  value,
  parentValue,
  hasParent,
  parentName,
}) => {
  const formatValue = (val: unknown): string => {
    if (val === null || val === undefined) {
      return '—';
    }

    if (typeof val === 'boolean') {
      return val ? 'Yes' : 'No';
    }

    if (typeof val === 'number') {
      return field.unit ? `${val} ${field.unit}` : String(val);
    }

    if (field.fieldType === 'enum' && field.options) {
      const option = field.options.find((opt) => opt.value === String(val));
      return option?.label || String(val);
    }

    return String(val);
  };

  const isOverridden = hasParent && parentValue !== undefined && value !== parentValue;
  const status = hasParent
    ? isOverridden
      ? 'overridden'
      : 'inherited'
    : 'standalone';

  const formattedValue = formatValue(value);
  const formattedParentValue = parentValue !== undefined ? formatValue(parentValue) : null;

  return (
    <div className="flex items-start gap-3 py-2 border-b border-pf-border last:border-b-0">
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-2">
          <span
            className={clsx(
              'text-sm',
              isOverridden ? 'font-semibold text-pf-text-primary' : 'text-pf-text-primary'
            )}
          >
            {field.label}
          </span>
          {field.description && (
            <span className="text-xs text-pf-text-muted" title={field.description}>
              ⓘ
            </span>
          )}
        </div>
        <div className="mt-0.5">
          <div
            className={clsx(
              'text-sm',
              isOverridden ? 'font-bold text-pf-text-primary' : 'text-pf-text-primary'
            )}
          >
            {formattedValue}
          </div>
          {isOverridden && formattedParentValue && (
            <div className="text-xs text-pf-text-muted mt-0.5">
              Parent: {formattedParentValue}
            </div>
          )}
        </div>
      </div>
      <InheritanceBadge status={status} parentName={parentName} />
    </div>
  );
};
