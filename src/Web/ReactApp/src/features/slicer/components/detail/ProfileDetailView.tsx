import React, { useState, useMemo } from 'react';
import clsx from 'clsx';
import { Spinner, Button } from '@/common/components/ui';
import { useProfileSchema } from '../settings/schema/useProfileSchema';
import { ProfileFieldRow } from './ProfileFieldRow';
import type { ProfileFieldMetadata } from '@/types/api';

interface ProfileDetailViewProps {
  profileType: 'process' | 'machine' | 'filament';
  profile: Record<string, unknown>;
  parentProfile?: Record<string, unknown>;
  profileName?: string;
  parentName?: string;
  className?: string;
}

interface CategorySection {
  category: string;
  fields: Array<{
    field: ProfileFieldMetadata;
    value: unknown;
    parentValue?: unknown;
    isOverridden: boolean;
  }>;
  totalFields: number;
  overriddenCount: number;
}

/**
 * Rich readonly display of all profile settings organized by category
 * Shows inheritance indicators and parent value comparisons
 */
export const ProfileDetailView: React.FC<ProfileDetailViewProps> = ({
  profileType,
  profile,
  parentProfile,
  parentName,
  className,
}) => {
  const [showInherited, setShowInherited] = useState(true);
  const [expandedCategories, setExpandedCategories] = useState<Set<string>>(new Set());

  const { data: schema, isLoading, error } = useProfileSchema(profileType);

  // Group fields by category and calculate override stats
  const categorySections = useMemo((): CategorySection[] => {
    if (!schema?.fields) return [];

    const categoryMap = new Map<string, CategorySection>();

    schema.fields.forEach((field) => {
      const value = profile[field.key];
      const parentValue = parentProfile?.[field.key];
      const isOverridden =
        parentProfile !== undefined && parentValue !== undefined && value !== parentValue;

      if (!categoryMap.has(field.category)) {
        categoryMap.set(field.category, {
          category: field.category,
          fields: [],
          totalFields: 0,
          overriddenCount: 0,
        });
      }

      const section = categoryMap.get(field.category)!;
      section.fields.push({ field, value, parentValue, isOverridden });
      section.totalFields++;
      if (isOverridden) {
        section.overriddenCount++;
      }
    });

    return Array.from(categoryMap.values());
  }, [schema, profile, parentProfile]);

  const toggleCategory = (category: string) => {
    const newExpanded = new Set(expandedCategories);
    if (newExpanded.has(category)) {
      newExpanded.delete(category);
    } else {
      newExpanded.add(category);
    }
    setExpandedCategories(newExpanded);
  };

  const toggleAllCategories = () => {
    if (expandedCategories.size === categorySections.length) {
      setExpandedCategories(new Set());
    } else {
      setExpandedCategories(new Set(categorySections.map((s) => s.category)));
    }
  };

  if (isLoading) {
    return (
      <div className={clsx('flex items-center justify-center py-12', className)}>
        <Spinner size="lg" />
      </div>
    );
  }

  if (error) {
    return (
      <div className={clsx('p-4 rounded-lg bg-pf-bg-0 border border-pf-border', className)}>
        <div className="text-sm text-pf-error">Failed to load profile schema</div>
      </div>
    );
  }

  if (categorySections.length === 0) {
    return (
      <div className={clsx('p-8 text-center rounded-lg bg-pf-bg-0 border border-pf-border', className)}>
        <div className="text-sm text-pf-text-muted">No settings available for this profile</div>
      </div>
    );
  }

  const hasParent = parentProfile !== undefined;
  const totalOverriddenCount = categorySections.reduce((sum, s) => sum + s.overriddenCount, 0);

  return (
    <div className={clsx('space-y-4', className)}>
      {/* Header controls */}
      <div className="flex items-center justify-between gap-4 p-3 rounded-lg bg-pf-bg-0 border border-pf-border">
        <div className="flex items-center gap-4">
          <Button
            variant="ghost"
            size="sm"
            onClick={toggleAllCategories}
            className="text-xs"
          >
            {expandedCategories.size === categorySections.length ? 'Collapse All' : 'Expand All'}
          </Button>

          {hasParent && (
            <label className="flex items-center gap-2 text-xs text-pf-text-primary cursor-pointer select-none">
              {/* eslint-disable-next-line local/pf-no-raw-html-controls */}
              <input
                type="checkbox"
                checked={showInherited}
                onChange={(e) => setShowInherited(e.target.checked)}
                className="w-4 h-4 rounded border-pf-border text-pf-accent focus:ring-pf-accent"
              />
              Show inherited fields
            </label>
          )}
        </div>

        {hasParent && totalOverriddenCount > 0 && (
          <div className="text-xs text-pf-text-muted">
            {totalOverriddenCount} field{totalOverriddenCount !== 1 ? 's' : ''} overridden
          </div>
        )}
      </div>

      {/* Category sections */}
      <div className="space-y-3">
        {categorySections.map((section) => {
          const isExpanded = expandedCategories.has(section.category);
          const visibleFields = showInherited
            ? section.fields
            : section.fields.filter((f) => f.isOverridden);

          if (!showInherited && visibleFields.length === 0) {
            return null;
          }

          return (
            <div
              key={section.category}
              className="rounded-lg bg-pf-bg-0 border border-pf-border overflow-hidden"
            >
              {/* Category header */}
              {/* eslint-disable-next-line local/pf-no-raw-html-controls */}
              <button
                type="button"
                onClick={() => toggleCategory(section.category)}
                className="w-full px-4 py-3 flex items-center justify-between bg-pf-bg-0 hover:bg-pf-accent-bg transition-colors"
              >
                <div className="flex items-center gap-3">
                  <span
                    className={clsx(
                      'text-lg transition-transform',
                      isExpanded ? 'rotate-90' : 'rotate-0'
                    )}
                  >
                    ▶
                  </span>
                  <div className="text-left">
                    <div className="text-sm font-semibold text-pf-text-primary">
                      {section.category}
                    </div>
                    <div className="text-xs text-pf-text-muted mt-0.5">
                      {section.totalFields} field{section.totalFields !== 1 ? 's' : ''}
                      {hasParent && section.overriddenCount > 0 && (
                        <span className="ml-1 text-orange-500 font-medium">
                          ({section.overriddenCount} overridden)
                        </span>
                      )}
                    </div>
                  </div>
                </div>
                <div className="text-xs text-pf-text-muted">
                  {isExpanded ? 'Click to collapse' : 'Click to expand'}
                </div>
              </button>

              {/* Category fields */}
              {isExpanded && (
                <div className="px-4 py-2 border-t border-pf-border">
                  {visibleFields.length === 0 ? (
                    <div className="py-4 text-center text-xs text-pf-text-muted">
                      {showInherited ? 'No fields in this category' : 'No overridden fields'}
                    </div>
                  ) : (
                    <div className="space-y-0">
                      {visibleFields.map(({ field, value, parentValue }) => (
                        <ProfileFieldRow
                          key={field.key}
                          field={field}
                          value={value}
                          parentValue={parentValue}
                          hasParent={hasParent}
                          parentName={parentName}
                        />
                      ))}
                    </div>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
};
