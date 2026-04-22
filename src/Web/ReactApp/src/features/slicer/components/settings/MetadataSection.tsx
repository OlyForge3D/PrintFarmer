/**
 * MetadataSection — renders a collapsible group of settings from a single
 * section in orcaSettingsMetadata.json.
 *
 * Filters fields by view-mode (simple/advanced), detects paired temperature
 * rows, and delegates each field to MetadataSettingRow.
 */
import React, { useMemo } from 'react';
import { SectionHeader } from '@/features/slicer/components/settings/SettingRow';
import { MetadataSettingRow } from '@/features/slicer/components/settings/MetadataSettingRow';
import type {
  SectionLayout,
  SettingMetadata,
  ViewMode,
} from '@/features/slicer/components/settings/metadataTypes';
import { CONDITIONAL_HIDDEN_KEYS } from '@/features/slicer/components/settings/metadataTypes';
import { OrcaIcon } from '@/features/slicer/components/settings/OrcaIcon';

// ── Props ───────────────────────────────────────────────────────────────

export interface MetadataSectionProps {
  /** Section layout from the metadata tab definition */
  section: SectionLayout;
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

export const MetadataSection: React.FC<MetadataSectionProps> = ({
  section,
  allSettings,
  values,
  originalValues,
  onUpdate,
  viewMode,
  disabled,
}) => {
  // Resolve visible fields: filter by mode and existence in settings dict
  const visibleFields = useMemo(() => {
    return section.fields.filter((f) => {
      const meta = allSettings[f.key];
      if (!meta) return false;
      if (meta.mode === 'developer') return false;
      if (viewMode === 'simple' && meta.mode === 'advanced') return false;
      if (CONDITIONAL_HIDDEN_KEYS.has(f.key)) return false;
      return true;
    });
  }, [section.fields, allSettings, viewMode]);

  if (visibleFields.length === 0) return null;

  // Detect paired temperature fields: *_temp_initial_layer + *_temp
  const pairedOtherKeys = new Set<string>();
  const pairMap = new Map<string, string>();
  for (let i = 0; i < visibleFields.length - 1; i++) {
    const k = visibleFields[i].key;
    const next = visibleFields[i + 1].key;
    if (k.endsWith('_temp_initial_layer') && next === k.replace('_initial_layer', '')) {
      pairMap.set(k, next);
      pairedOtherKeys.add(next);
    }
  }

  return (
    <div>
      <SectionHeader
        icon={<OrcaIcon icon={section.icon} />}
        title={section.name}
      />
      <div>
        {visibleFields.map((field) => {
          // Skip "other layers" keys rendered as part of a pair
          if (pairedOtherKeys.has(field.key)) return null;

          const meta = allSettings[field.key];
          const otherKey = pairMap.get(field.key);
          const isTextarea = meta.key && (
            meta.gui_type !== 'color' &&
            ['machine_start_gcode', 'machine_end_gcode', 'machine_pause_gcode',
             'template_custom_gcode', 'change_filament_gcode', 'layer_change_gcode',
             'time_lapse_gcode', 'before_layer_change_gcode', 'file_start_gcode',
             'printing_by_object_gcode', 'wrapping_detection_gcode',
             'change_extrusion_role_gcode', 'filament_start_gcode', 'filament_end_gcode',
             'adaptive_pressure_advance_model', 'filament_notes', 'printer_notes',
             'compatible_printers_condition', 'compatible_prints_condition',
            ].includes(meta.key)
          );
          const showLabel = isTextarea ? visibleFields.length > 1 : true;

          return (
            <MetadataSettingRow
              key={field.key}
              field={field}
              meta={meta}
              values={values}
              originalValues={originalValues}
              onUpdate={onUpdate}
              disabled={disabled}
              pairedOtherKey={otherKey}
              pairedOtherMeta={otherKey ? allSettings[otherKey] : undefined}
              showLabel={showLabel}
            />
          );
        })}
      </div>
    </div>
  );
};
