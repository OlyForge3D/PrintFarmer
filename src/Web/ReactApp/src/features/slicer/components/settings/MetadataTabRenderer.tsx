/**
 * MetadataTabRenderer — renders all sections within a single tab from
 * orcaSettingsMetadata.json.
 *
 * Can be used directly by ProcessProfileEditor, MachineProfileEditor, and
 * FilamentProfileEditor without going through the full MetadataProfileEditor.
 */
import React from 'react';
import { MetadataSection } from '@/features/slicer/components/settings/MetadataSection';
import type {
  TabLayout,
  SettingMetadata,
  ViewMode,
} from '@/features/slicer/components/settings/metadataTypes';

// ── Props ───────────────────────────────────────────────────────────────

export interface MetadataTabRendererProps {
  /** Tab layout from the metadata definition */
  tab: TabLayout;
  /** Full settings dictionary for this profile type */
  allSettings: Record<string, SettingMetadata>;
  /** Current settings values */
  values: Record<string, unknown>;
  /** Original (saved) values for change tracking */
  originalValues?: Record<string, unknown>;
  /** Fires when the user changes a setting's value */
  onUpdate: (key: string, value: unknown) => void;
  /** Current view mode controlling which fields are visible */
  viewMode: ViewMode;
  /** Whether all controls are disabled */
  disabled: boolean;
}

// ── Component ───────────────────────────────────────────────────────────

export const MetadataTabRenderer: React.FC<MetadataTabRendererProps> = ({
  tab,
  allSettings,
  values,
  originalValues,
  onUpdate,
  viewMode,
  disabled,
}) => (
  <div className="space-y-1">
    {tab.sections.map((section) => (
      <MetadataSection
        key={section.name}
        section={section}
        allSettings={allSettings}
        values={values}
        originalValues={originalValues}
        onUpdate={onUpdate}
        viewMode={viewMode}
        disabled={disabled}
      />
    ))}
  </div>
);
