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
import React, { useState, useMemo } from 'react';
import { Button } from '@/common/components/ui';
import { useSlicerViewMode } from '@/features/slicer/hooks/useSlicerViewMode';
import { MetadataTabRenderer } from '@/features/slicer/components/settings/MetadataTabRenderer';
import metadata from '@/features/slicer/generated/orcaSettingsMetadata.json';
import type { ProfileType, ProfileTypeMetadata } from '@/features/slicer/components/settings/metadataTypes';

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

    // Find orphaned settings that are visible in the current view mode
    const orphanedFields = Object.keys(profileMeta.settings)
      .filter((k) => !tabbedKeys.has(k) && isFieldVisible(k))
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

  return (
    <div className={`bg-pf-bg-1 rounded-lg border border-pf-border flex flex-col ${className}`}>
      {/* Tab bar + Advanced toggle */}
      <div className="flex items-center justify-between px-4 py-2 border-b border-pf-border">
        <div className="flex gap-1 overflow-x-auto">
          {visibleTabs.map((tab, idx) => (
            <Button
              key={tab.name}
              variant="unstyled"
              type="button"
              size="sm"
              onClick={() => setActiveTabIdx(idx)}
              disabled={disabled}
              className={`px-2 py-0.5 text-[10px] font-medium rounded-full whitespace-nowrap
                ${idx === clampedActiveTabIdx
                  ? 'bg-pf-accent-2/15 text-pf-accent-2 ring-1 ring-pf-accent-2/40'
                  : 'text-pf-text-secondary hover:text-pf-text-primary'}`}
            >
              {tab.name}
            </Button>
          ))}
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
      <div className="p-2 flex-1 min-h-0 overflow-y-auto">
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
