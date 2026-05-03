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
  FieldRef,
  ViewMode,
} from '@/features/slicer/components/settings/metadataTypes';
import { CONDITIONAL_HIDDEN_KEYS } from '@/features/slicer/components/settings/metadataTypes';
import { OrcaIcon } from '@/features/slicer/components/settings/OrcaIcon';

// ── Compound Row (renders multiple fields on one line with a shared label) ──

interface CompoundRowProps {
  label: string;
  fields: FieldRef[];
  allSettings: Record<string, SettingMetadata>;
  values: Record<string, unknown>;
  originalValues?: Record<string, unknown>;
  onUpdate: (key: string, value: unknown) => void;
  disabled: boolean;
}

const CompoundRow: React.FC<CompoundRowProps> = ({
  label,
  fields,
  allSettings,
  values,
  originalValues,
  onUpdate,
  disabled,
}) => {
  const anyModified = fields.some((f) => {
    const orig = originalValues?.[f.key];
    const cur = values[f.key];
    return orig !== undefined && String(orig) !== String(cur ?? '');
  });

  return (
    <div className="flex items-center gap-1.5 py-0.5">
      <div className="w-2/5 shrink-0 truncate">
        <span
          className={`text-xs font-medium ${anyModified ? 'text-pf-warning' : 'text-pf-text'}`}
          title={label}
        >
          {label}
        </span>
      </div>
      <div className="flex-1 flex items-center gap-2">
        {fields.map((f) => {
          const meta = allSettings[f.key];
          if (!meta) return null;
          const unit = meta.unit || '';
          const val = values[f.key] ?? meta.default ?? '';
          return (
            <div key={f.key} className="flex items-center gap-1 flex-1">
              {meta.label && (
                <span className="text-[10px] text-pf-text-muted whitespace-nowrap">{meta.label}</span>
              )}
              <div className="flex items-center flex-1">
                <input
                  type="number"
                  title={meta.tooltip || `${label} ${meta.label || ''}`}
                  className="w-full py-1 px-2 bg-pf-panel border border-pf-border text-pf-text text-xs text-right rounded-l-lg rounded-r-none border-r-0 hover:border-pf-border-light focus:border-pf-accent-2 focus:outline-hidden"
                  value={val as number}
                  min={meta.min}
                  max={meta.max}
                  onChange={(e) => onUpdate(f.key, Number(e.target.value))}
                  disabled={disabled}
                />
                {unit && (
                  <span className="text-xs text-pf-text-muted px-1.5 bg-pf-border rounded-r-lg w-8 shrink-0 self-stretch flex items-center">
                    {unit}
                  </span>
                )}
                {!unit && (
                  <span className="rounded-r-lg border border-l-0 border-pf-border w-0" />
                )}
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
};

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
        {(() => {
          const elements: React.ReactNode[] = [];
          let i = 0;
          while (i < visibleFields.length) {
            const field = visibleFields[i];

            // Skip "other layers" keys rendered as part of a pair
            if (pairedOtherKeys.has(field.key)) {
              i++;
              continue;
            }

            // Compound group: consecutive compound: true fields rendered on one line
            // (skip fields already handled by temperature pairing)
            if (field.compound && !pairMap.has(field.key) && !pairedOtherKeys.has(field.key)) {
              const groupFields = [field];
              let j = i + 1;
              while (j < visibleFields.length && visibleFields[j].compound
                && !pairMap.has(visibleFields[j].key) && !pairedOtherKeys.has(visibleFields[j].key)) {
                groupFields.push(visibleFields[j]);
                j++;
              }
              // Only render as compound row if 2+ numeric fields (textareas / strings render normally)
              const allNumeric = groupFields.every(f => {
                const m = allSettings[f.key];
                return m && (m.type === 'int' || m.type === 'float' || m.type === 'percent');
              });
              if (groupFields.length >= 2 && allNumeric) {
              const label = field.compound_label || groupFields.map(f => allSettings[f.key]?.label || f.key).join(' / ');
              elements.push(
                <CompoundRow
                  key={`compound-${field.key}`}
                  label={label}
                  fields={groupFields}
                  allSettings={allSettings}
                  values={values}
                  originalValues={originalValues}
                  onUpdate={onUpdate}
                  disabled={disabled}
                />
              );
              i = j;
              continue;
              } // end allNumeric check — fall through to render individually
            }

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

            elements.push(
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
            i++;
          }
          return elements;
        })()}
      </div>
    </div>
  );
};
