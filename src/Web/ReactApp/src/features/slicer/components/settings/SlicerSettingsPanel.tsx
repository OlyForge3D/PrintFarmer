/**
 * OrcaSlicer-style Settings Panel — metadata-driven.
 *
 * Thin wrapper around MetadataProfileEditor that adapts the
 * OrcaProcessSettings API to the metadata renderer's API.
 * All fields are driven from orcaSettingsMetadata.json — zero hand-coded lists.
 */
import React, { useCallback } from 'react';
import { MetadataProfileEditor } from './MetadataProfileRenderer';
import type { OrcaProcessSettings, SettingsCategory } from './slicerSettingsTypes';

interface SlicerSettingsPanelProps {
  /** Current settings values */
  settings: OrcaProcessSettings;
  /** Called when any setting changes */
  onChange: (settings: Partial<OrcaProcessSettings>) => void;
  /** Disable all controls */
  disabled?: boolean;
  /** Custom class name */
  className?: string;
  /** Optional function to check if a category has modified settings */
  isCategoryDirty?: (category: SettingsCategory) => boolean;
  /** Raw Orca settings not explicitly modeled in typed controls */
  advancedSettings?: Record<string, unknown>;
  /** Called when dynamic advanced settings change */
  onAdvancedSettingsChange?: (settings: Record<string, unknown>) => void;
  /** Original settings snapshot for change tracking (orange labels + reset buttons) */
  originalSettings?: Record<string, unknown>;
}

/**
 * SlicerSettingsPanel — delegates entirely to MetadataProfileEditor.
 * All 344 process settings rendered from orcaSettingsMetadata.json.
 */
export const SlicerSettingsPanel: React.FC<SlicerSettingsPanelProps> = ({
  settings,
  onChange,
  disabled = false,
  className = '',
  originalSettings,
}) => {
  // Adapt MetadataProfileEditor's per-field onUpdate to the batch onChange API
  const handleUpdate = useCallback(
    (key: string, value: unknown) => {
      onChange({ [key]: value } as Partial<OrcaProcessSettings>);
    },
    [onChange],
  );

  return (
    <MetadataProfileEditor
      profileType="process"
      settings={settings as unknown as Record<string, unknown>}
      onUpdate={handleUpdate}
      disabled={disabled}
      className={className}
      originalSettings={originalSettings}
    />
  );
};

export default SlicerSettingsPanel;
