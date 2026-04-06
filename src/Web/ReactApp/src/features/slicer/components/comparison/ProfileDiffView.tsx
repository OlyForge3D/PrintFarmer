import React, { useMemo, useState } from 'react';
import clsx from 'clsx';
import { Toggle, EmptyState, Badge, Spinner } from '@/common/components/ui';
import type { ProfileTypeSchemaDto, ProfileFieldMetadata } from '@/types/api';

export interface ProfileDiffViewProps {
  profileType: 'process' | 'machine' | 'filament';
  leftProfile: Record<string, unknown>;
  rightProfile: Record<string, unknown>;
  leftLabel?: string;
  rightLabel?: string;
  schema?: ProfileTypeSchemaDto;
  showOnlyDifferences?: boolean;
  className?: string;
}

interface DiffField {
  key: string;
  label: string;
  leftValue: unknown;
  rightValue: unknown;
  isDifferent: boolean;
  metadata?: ProfileFieldMetadata;
  category: string;
}

function formatValue(
  value: unknown,
  metadata?: ProfileFieldMetadata
): string {
  if (value === null || value === undefined) {
    return '—';
  }

  if (typeof value === 'boolean') {
    return value ? '✓' : '✗';
  }

  if (typeof value === 'number') {
    const unit = metadata?.unit ? ` ${metadata.unit}` : '';
    return `${value}${unit}`;
  }

  if (metadata?.fieldType === 'enum' && typeof value === 'string') {
    const option = metadata.options?.find((opt) => opt.value === value);
    return option?.label || value;
  }

  return String(value);
}

function areValuesEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (a === null || a === undefined) return b === null || b === undefined;
  if (b === null || b === undefined) return false;
  
  if (typeof a === 'number' && typeof b === 'number') {
    return Math.abs(a - b) < 0.0001;
  }
  
  return String(a) === String(b);
}

export function ProfileDiffView({
  leftProfile,
  rightProfile,
  leftLabel = 'Profile A',
  rightLabel = 'Profile B',
  schema,
  showOnlyDifferences: initialShowOnlyDifferences = false,
  className,
}: ProfileDiffViewProps) {
  const [showOnlyDifferences, setShowOnlyDifferences] = useState(initialShowOnlyDifferences);

  const diffFields = useMemo(() => {
    const allKeys = new Set([
      ...Object.keys(leftProfile),
      ...Object.keys(rightProfile),
    ]);

    const fields: DiffField[] = [];

    for (const key of allKeys) {
      if (key === 'settings') continue; // Skip nested settings object
      
      const leftValue = leftProfile[key];
      const rightValue = rightProfile[key];
      const isDifferent = !areValuesEqual(leftValue, rightValue);
      
      const metadata = schema?.fields.find((f) => f.key === key);
      const label = metadata?.label || key;
      const category = metadata?.category || 'Other';

      fields.push({
        key,
        label,
        leftValue,
        rightValue,
        isDifferent,
        metadata,
        category,
      });
    }

    return fields;
  }, [leftProfile, rightProfile, schema]);

  const categorizedFields = useMemo(() => {
    const categories = schema?.categories || ['Other'];
    const grouped = new Map<string, DiffField[]>();

    for (const category of categories) {
      grouped.set(category, []);
    }

    for (const field of diffFields) {
      const categoryFields = grouped.get(field.category) || [];
      categoryFields.push(field);
      if (!grouped.has(field.category)) {
        grouped.set(field.category, categoryFields);
      }
    }

    return Array.from(grouped.entries()).filter(([, fields]) => fields.length > 0);
  }, [diffFields, schema]);

  const displayedFields = useMemo(() => {
    return categorizedFields.map(([category, fields]) => [
      category,
      showOnlyDifferences ? fields.filter((f) => f.isDifferent) : fields,
    ] as const);
  }, [categorizedFields, showOnlyDifferences]);

  const differenceCount = diffFields.filter((f) => f.isDifferent).length;
  const hasNoDifferences = differenceCount === 0;

  if (!schema) {
    return (
      <div className={clsx('flex items-center justify-center p-8', className)}>
        <Spinner size="lg" />
      </div>
    );
  }

  return (
    <div className={clsx('flex flex-col gap-4', className)}>
      {/* Header with toggle */}
      <div className="flex items-center justify-between border-b border-pf-border pb-3">
        <div className="flex items-center gap-3">
          <h3 className="text-lg font-medium text-pf-text-primary">Profile Comparison</h3>
          <Badge variant={hasNoDifferences ? 'success' : 'default'}>
            {hasNoDifferences ? 'Identical' : `${differenceCount} difference${differenceCount !== 1 ? 's' : ''}`}
          </Badge>
        </div>
        <div className="flex items-center gap-2">
          <label htmlFor="show-differences-only" className="text-sm text-pf-text-secondary">
            Show only differences
          </label>
          <Toggle
            id="show-differences-only"
            checked={showOnlyDifferences}
            onChange={setShowOnlyDifferences}
          />
        </div>
      </div>

      {hasNoDifferences && (
        <EmptyState
          icon="✓"
          title="Profiles are identical"
          description="No differences found between the selected profiles"
        />
      )}

      {!hasNoDifferences && (
        <div className="space-y-6">
          {displayedFields.map(([category, fields]) => {
            if (fields.length === 0) return null;

            return (
              <div key={category}>
                <h4 className="mb-3 text-sm font-semibold uppercase tracking-wide text-pf-text-secondary">
                  {category}
                </h4>
                <div className="overflow-x-auto rounded-lg border border-pf-border">
                  <table className="w-full">
                    <thead>
                      <tr className="border-b border-pf-border bg-pf-bg-1">
                        <th className="px-4 py-2 text-left text-sm font-medium text-pf-text-secondary">
                          {leftLabel}
                        </th>
                        <th className="px-4 py-2 text-center text-sm font-medium text-pf-text-secondary">
                          Field
                        </th>
                        <th className="px-4 py-2 text-right text-sm font-medium text-pf-text-secondary">
                          {rightLabel}
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {fields.map((field) => (
                        <tr
                          key={field.key}
                          className={clsx(
                            'border-b border-pf-border last:border-b-0',
                            field.isDifferent && 'bg-pf-accent-bg/20'
                          )}
                        >
                          <td
                            className={clsx(
                              'px-4 py-2 text-left text-sm',
                              field.isDifferent
                                ? 'font-medium text-pf-text-primary'
                                : 'text-pf-text-secondary'
                            )}
                          >
                            {formatValue(field.leftValue, field.metadata)}
                          </td>
                          <td className="px-4 py-2 text-center text-sm font-medium text-pf-text-primary">
                            {field.label}
                          </td>
                          <td
                            className={clsx(
                              'px-4 py-2 text-right text-sm',
                              field.isDifferent
                                ? 'font-medium text-pf-text-primary'
                                : 'text-pf-text-secondary'
                            )}
                          >
                            {formatValue(field.rightValue, field.metadata)}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
