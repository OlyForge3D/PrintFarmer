/**
 * Metadata-driven profile setting renderer.
 *
 * Reads orcaSettingsMetadata.json at build time and renders every field
 * through the existing SettingRow component — zero hand-coded field lists.
 *
 * This file now delegates to the extracted standalone components:
 *  - metadataTypes.ts       — shared types, constants, and helpers
 *  - MetadataSettingRow.tsx  — single-field renderer
 *  - MetadataSection.tsx     — section group renderer
 *  - MetadataTabRenderer.tsx — tab-level renderer
 *
 * The MetadataProfileEditor top-level component remains here.
 * All type exports are re-exported for backward compatibility.
 */
import React, { useState, useMemo, useCallback, useRef } from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';
import { useSlicerViewMode } from '@/features/slicer/hooks/useSlicerViewMode';
import { SlicerModeToggle } from '@/features/slicer/components/settings/SlicerModeToggle';
import { MetadataTabRenderer } from '@/features/slicer/components/settings/MetadataTabRenderer';
import { getMetadataForVersion } from '@/features/slicer/components/settings/orcaSettingsMetadataResolver';
import {
  getSingleBedTemperatureKey,
  isBedTemperatureSettingVisible,
  type FilamentBedContext,
} from '@/features/slicer/utils/bedSurfaceTemperature';
import {
  isSettingModified,
  type MetadataSectionAddition,
  type ProfileType,
  type SettingMetadata,
  type TabLayout,
} from '@/features/slicer/components/settings/metadataTypes';

// Re-export all types for backward compatibility
export type {
  SettingMetadata,
  FieldRef,
  SectionLayout,
  TabLayout,
  ProfileTypeMetadata,
  ProfileType,
  ViewMode,
} from '@/features/slicer/components/settings/metadataTypes';

// Re-export standalone components so consumers can import from either location
export { MetadataSettingRow } from '@/features/slicer/components/settings/MetadataSettingRow';
export type { MetadataSettingRowProps } from '@/features/slicer/components/settings/MetadataSettingRow';
export { MetadataSection } from '@/features/slicer/components/settings/MetadataSection';
export type { MetadataSectionProps } from '@/features/slicer/components/settings/MetadataSection';
export { MetadataTabRenderer } from '@/features/slicer/components/settings/MetadataTabRenderer';
export type { MetadataTabRendererProps } from '@/features/slicer/components/settings/MetadataTabRenderer';

// ── MetadataProfileEditor (top-level) ───────────────────────────────────

export interface MetadataProfileEditorProps {
  profileType: ProfileType;
  settings: Record<string, unknown>;
  originalSettings?: Record<string, unknown>;
  onUpdate: (key: string, value: unknown) => void;
  disabled?: boolean;
  className?: string;
  /**
   * Optional OrcaSlicer engine version (e.g. "2.4.1", "2.3.1") to scope the
   * rendered settings/tabs to that version's real field set. When omitted or
   * unparsable, the full (union) metadata bundle is used — this is the safe
   * fallback for engine-agnostic profile management pages.
   */
  engineVersion?: string;
  /** Additional metadata for settings not yet represented by Orca's generated bundle */
  additionalSettings?: Readonly<Record<string, SettingMetadata>>;
  /** Additional sections inserted into matching generated tabs */
  additionalSections?: readonly MetadataSectionAddition[];
  /** Advanced metadata keys deliberately promoted into Simple mode */
  simpleModeSettingKeys?: ReadonlySet<string>;
  /** Resolved machine/active-plate context; omitted in engine-agnostic editors. */
  filamentBedContext?: FilamentBedContext;
  /** Replaces the default Basic/Advanced toggle with a caller-owned control; `null` hides it. */
  viewModeControl?: React.ReactNode;
}

export const MetadataProfileEditor: React.FC<MetadataProfileEditorProps> = ({
  profileType,
  settings,
  originalSettings,
  onUpdate,
  disabled = false,
  className = '',
  engineVersion,
  additionalSettings,
  additionalSections,
  simpleModeSettingKeys,
  filamentBedContext,
  viewModeControl,
}) => {
  const scopedBundle = useMemo(
    () => getMetadataForVersion(engineVersion),
    [engineVersion],
  );
  const baseProfileMeta = scopedBundle.profileTypes[profileType];
  const profileMeta = useMemo(() => {
    const settings = Object.fromEntries(
      Object.entries(baseProfileMeta.settings).filter(([key]) =>
        profileType !== 'filament' || isBedTemperatureSettingVisible(key, filamentBedContext)),
    );
    if (!additionalSettings && !additionalSections?.length) {
      return { ...baseProfileMeta, settings };
    }

    const existingFieldKeys = new Set(
      baseProfileMeta.tabs.flatMap((tab) =>
        tab.sections.flatMap((section) => section.fields.map((field) => field.key))),
    );
    const tabs = baseProfileMeta.tabs.map((tab) => {
      const sections = (additionalSections ?? [])
        .filter((addition) => addition.tabName === tab.name)
        .map((addition) => ({
          ...addition.section,
          fields: addition.section.fields.filter((field) => !existingFieldKeys.has(field.key)),
        }))
        .filter((section) => section.fields.length > 0);

      return sections.length > 0
        ? { ...tab, sections: [...sections, ...tab.sections] }
        : tab;
    });

    return {
      tabs,
      settings: {
        // Generated Orca metadata is authoritative when an additional entry graduates
        // into the bundle; this ordering prevents a stale local definition from winning.
        ...(additionalSettings ?? {}),
        ...settings,
      },
    };
  }, [additionalSections, additionalSettings, baseProfileMeta, profileType, filamentBedContext]);
  const [viewMode, toggleViewMode] = useSlicerViewMode();
  const [activeTabIdx, setActiveTabIdx] = useState(0);

  // Filter tabs to only show those with visible fields in the current view mode.
  const visibleTabs = useMemo(() => {
    const isFieldVisible = (key: string): boolean => {
      const m = profileMeta.settings[key];
      if (!m) return false;
      if (m.mode === 'developer') return false;
      if (
        viewMode === 'simple'
        && m.mode === 'advanced'
        && !simpleModeSettingKeys?.has(key)
      ) return false;
      return true;
    };

    return profileMeta.tabs.filter((tab) =>
      tab.sections.some((section) => section.fields.some((f) => isFieldVisible(f.key)))
    );
  }, [profileMeta.tabs, profileMeta.settings, simpleModeSettingKeys, viewMode]);

  // Clamp activeTabIdx when visibleTabs changes
  const clampedActiveTabIdx = Math.min(activeTabIdx, Math.max(0, visibleTabs.length - 1));
  const activeTab = visibleTabs[clampedActiveTabIdx] ?? visibleTabs[0];

  // Roving-focus tablist: arrow keys move between tabs, Home/End jump to ends.
  const tabRefs = useRef<(HTMLButtonElement | null)[]>([]);
  const slug = (name: string) => name.toLowerCase().replace(/[^a-z0-9]+/g, '-');
  const handleTabKeyDown = useCallback(
    (e: React.KeyboardEvent, idx: number) => {
      const count = visibleTabs.length;
      if (count === 0) return;
      let next: number | null = null;
      if (e.key === 'ArrowRight') next = (idx + 1) % count;
      else if (e.key === 'ArrowLeft') next = (idx - 1 + count) % count;
      else if (e.key === 'Home') next = 0;
      else if (e.key === 'End') next = count - 1;
      if (next === null) return;
      e.preventDefault();
      setActiveTabIdx(next);
      const tab = tabRefs.current[next];
      tab?.focus();
      tab?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
    },
    [visibleTabs.length],
  );

  // A tab is "dirty" when any of its fields differs from the original snapshot.
  // Uses the same comparison as the per-field change tracking so the tab label
  // turns the modified color (orange) whenever a contained setting is changed.
  const isTabDirty = useCallback(
    (tab: TabLayout): boolean => {
      if (!originalSettings) return false;
      return tab.sections.some((section) =>
        section.fields.some((field) => {
          return isSettingModified(settings, originalSettings, field.key);
        }),
      );
    },
    [originalSettings, settings],
  );

  return (
    <div className={`bg-pf-bg-1 rounded-lg border border-pf-border flex flex-col ${className}`}>
      {profileType === 'filament' && (
        <p className="px-4 pt-2 text-sm text-pf-text-secondary">
          {!filamentBedContext
            ? 'No selected machine context. Showing all bed-surface settings.'
            : filamentBedContext.supportMultiBedTypes === undefined
              ? 'Machine multi-bed support is unknown. Showing all bed-surface settings.'
              : !filamentBedContext.bedType || filamentBedContext.bedType === 'Default Plate'
                ? 'Active bed surface is unknown. Showing all bed-surface settings.'
                : getSingleBedTemperatureKey(filamentBedContext)
                  ? `Bed surface: ${filamentBedContext.bedType}. Hidden surface values are preserved.`
                  : `Bed surface: ${filamentBedContext.bedType}. Showing all bed-surface settings.`}
        </p>
      )}
      {/* Tab bar + settings view toggle */}
      <div className="flex items-center justify-between gap-3 px-4 py-2 border-b border-pf-border">
        <div className="flex min-w-0 flex-1 flex-wrap p-0.5" role="tablist" aria-label="Profile settings sections">
          {visibleTabs.map((tab, idx) => {
            const isActive = idx === clampedActiveTabIdx;
            const dirty = isTabDirty(tab);
            return (
              <Button
                key={tab.name}
                ref={(el: HTMLButtonElement | null) => { tabRefs.current[idx] = el; }}
                variant="unstyled"
                type="button"
                size="sm"
                role="tab"
                id={`profile-tab-${slug(tab.name)}`}
                aria-controls="profile-settings-tabpanel"
                tabIndex={isActive ? 0 : -1}
                onKeyDown={(e) => handleTabKeyDown(e, idx)}
                onClick={() => setActiveTabIdx(idx)}
                disabled={disabled}
                aria-selected={isActive}
                className={clsx(
                  'min-h-11 min-w-11 shrink-0 px-1 pb-1 text-xs whitespace-nowrap rounded-none border-b transition-colors sm:min-h-0 sm:min-w-0',
                  isActive ? 'font-bold border-pf-accent-2' : 'font-normal border-transparent',
                  dirty
                    ? 'text-pf-warning'
                    : isActive
                      ? 'text-pf-text-primary'
                      : 'text-pf-text-secondary hover:text-pf-text-primary',
                )}
              >
                {tab.name}
              </Button>
            );
          })}
        </div>
        {viewModeControl !== undefined ? viewModeControl : (
          <SlicerModeToggle
            levels={['Basic', 'Advanced']}
            value={viewMode === 'advanced' ? 'Advanced' : 'Basic'}
            onChange={toggleViewMode}
            disabled={disabled}
          />
        )}
      </div>

      {/* Active tab content */}
      <div
        className="p-2 flex-1 min-h-0 overflow-y-auto"
        role="tabpanel"
        id="profile-settings-tabpanel"
        aria-labelledby={activeTab ? `profile-tab-${slug(activeTab.name)}` : undefined}
        tabIndex={0}
      >
        <MetadataTabRenderer
          tab={activeTab}
          allSettings={profileMeta.settings}
          values={settings}
          originalValues={originalSettings}
          onUpdate={onUpdate}
          viewMode={viewMode}
          disabled={disabled}
          simpleModeSettingKeys={simpleModeSettingKeys}
        />
      </div>
    </div>
  );
};
