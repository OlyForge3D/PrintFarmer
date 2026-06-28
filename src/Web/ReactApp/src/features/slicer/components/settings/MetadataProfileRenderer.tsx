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
import { MetadataTabRenderer } from '@/features/slicer/components/settings/MetadataTabRenderer';
import metadata from '@/features/slicer/generated/orcaSettingsMetadata.json';
import type { ProfileType, ProfileTypeMetadata, TabLayout } from '@/features/slicer/components/settings/metadataTypes';

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
}

export const MetadataProfileEditor: React.FC<MetadataProfileEditorProps> = ({
  profileType,
  settings,
  originalSettings,
  onUpdate,
  disabled = false,
  className = '',
}) => {
  const profileMeta = (metadata as unknown as Record<string, ProfileTypeMetadata>)[profileType];
  const [viewMode, toggleViewMode] = useSlicerViewMode();
  const [activeTabIdx, setActiveTabIdx] = useState(0);

  // Filter tabs to only show those with visible fields in the current view mode,
  // and append a synthetic "Other Settings" tab for orphaned settings in advanced mode.
  const visibleTabs = useMemo(() => {
    const isFieldVisible = (key: string): boolean => {
      const m = profileMeta.settings[key];
      if (!m) return false;
      if (m.mode === 'developer') return false;
      if (viewMode === 'simple' && m.mode === 'advanced') return false;
      return true;
    };

    const filtered = profileMeta.tabs.filter((tab) =>
      tab.sections.some((section) => section.fields.some((f) => isFieldVisible(f.key)))
    );

    // Collect keys that appear in any tab
    const tabbedKeys = new Set(
      profileMeta.tabs.flatMap((t) => t.sections.flatMap((s) => s.fields.map((f) => f.key)))
    );

    // Find orphaned settings with an explicit user-visible mode.
    // Settings without a mode are internal/programmatic and should not appear.
    const orphanedFields = Object.keys(profileMeta.settings)
      .filter((k) => {
        if (tabbedKeys.has(k)) return false;
        const m = profileMeta.settings[k];
        if (!m || !m.mode || m.mode === 'developer') return false;
        if (viewMode === 'simple' && m.mode === 'advanced') return false;
        return true;
      })
      .map((k) => ({ key: k, compound: false }));

    if (orphanedFields.length > 0) {
      filtered.push({
        name: 'Other Settings',
        icon: 'cog',
        sections: [{ name: 'Other Settings', icon: 'cog', fields: orphanedFields }],
      });
    }

    return filtered;
  }, [profileMeta.tabs, profileMeta.settings, viewMode]);

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
      tabRefs.current[next]?.focus();
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
          const cur = settings[field.key];
          const orig = originalSettings[field.key];
          return orig !== undefined && cur !== undefined && JSON.stringify(cur) !== JSON.stringify(orig);
        }),
      );
    },
    [originalSettings, settings],
  );

  return (
    <div className={`bg-pf-bg-1 rounded-lg border border-pf-border flex flex-col ${className}`}>
      {/* Tab bar + Advanced toggle */}
      <div className="flex items-center justify-between px-4 py-2 border-b border-pf-border">
        <div className="flex gap-3 overflow-x-auto" role="tablist" aria-label="Profile settings sections">
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
                aria-controls={`profile-tabpanel-${slug(tab.name)}`}
                tabIndex={isActive ? 0 : -1}
                onKeyDown={(e) => handleTabKeyDown(e, idx)}
                onClick={() => setActiveTabIdx(idx)}
                disabled={disabled}
                aria-selected={isActive}
                className={clsx(
                  'px-1 pb-1 -mb-2 text-xs whitespace-nowrap rounded-none border-b-2 transition-colors',
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

        {/* Advanced toggle */}
        <Button
          variant="unstyled"
          type="button"
          onClick={toggleViewMode}
          disabled={disabled}
          className="shrink-0 ml-2 p-0.5 rounded transition-colors hover:bg-pf-bg-2 disabled:opacity-50"
          title={viewMode === 'simple' ? 'Show advanced parameters' : 'Hide advanced parameters'}
          aria-label={`Switch to ${viewMode === 'simple' ? 'Advanced' : 'Simple'} mode`}
        >
          <span className="inline-flex items-center gap-1.5">
            <img src="/icons/orcaslicer-advanced.svg" alt="" className="w-4 h-4" />
            <span
              className={`relative inline-block w-7 h-3.5 rounded-full transition-colors ${
                viewMode === 'advanced' ? 'bg-pf-accent-2' : 'bg-pf-border'
              }`}
            >
              <span
                className={`absolute top-0.5 w-2.5 h-2.5 rounded-full bg-white shadow-sm transition-all ${
                  viewMode === 'advanced' ? 'left-3.5' : 'left-0.5'
                }`}
              />
            </span>
          </span>
        </Button>
      </div>

      {/* Active tab content */}
      <div
        className="p-2 flex-1 min-h-0 overflow-y-auto"
        role="tabpanel"
        id={activeTab ? `profile-tabpanel-${slug(activeTab.name)}` : undefined}
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
        />
      </div>
    </div>
  );
};
