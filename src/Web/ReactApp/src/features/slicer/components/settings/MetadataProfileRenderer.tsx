/**
 * Metadata-driven profile setting renderer.
 *
 * Reads orcaSettingsMetadata.json at build time and renders every field
 * through the existing SettingRow component — zero hand-coded field lists.
 */
import React, { useState, useCallback, useMemo } from 'react';
import { Button, Textarea } from '@/common/components/ui';
import { SettingRow, SectionHeader } from './SettingRow';
import metadata from '../../generated/orcaSettingsMetadata.json';

// ── Metadata type definitions ───────────────────────────────────────────

export interface SettingMetadata {
  key: string;
  type: string;            // bool | float | int | percent | string | enum
  coType: string;           // coFloat | coFloats | coBool | coInt | coString | coEnum | …
  label: string;
  tooltip?: string;
  unit?: string;
  min?: number;
  max?: number;
  mode?: 'simple' | 'advanced';
  default?: string;
  gui_type?: 'color' | 'enum_open';
  enum_values?: string[];
  category?: string;
}

/** Known enum options for settings that use select dropdowns */
const KNOWN_ENUMS: Record<string, Array<{ value: string; label: string }>> = {
  printer_structure: [
    { value: 'undefine', label: 'Undefined' },
    { value: 'corexy', label: 'CoreXY' },
    { value: 'i3', label: 'I3' },
    { value: 'hbot', label: 'Hbot' },
    { value: 'delta', label: 'Delta' },
  ],
  gcode_flavor: [
    { value: 'marlin', label: 'Marlin (legacy)' },
    { value: 'klipper', label: 'Klipper' },
    { value: 'reprapfirmware', label: 'RepRapFirmware' },
    { value: 'marlin2', label: 'Marlin 2' },
  ],
  nozzle_type: [
    { value: 'undefine', label: 'Undefined' },
    { value: 'hardened_steel', label: 'Hardened Steel' },
    { value: 'stainless_steel', label: 'Stainless Steel' },
    { value: 'brass', label: 'Brass' },
  ],
  bed_type: [
    { value: 'Cool Plate', label: 'Cool Plate' },
    { value: 'Engineering Plate', label: 'Engineering Plate' },
    { value: 'High Temp Plate', label: 'High Temp Plate' },
    { value: 'Textured PEI Plate', label: 'Textured PEI Plate' },
  ],
};

/** Keys that should render as multi-line textareas */
const TEXTAREA_KEYS = new Set([
  'machine_start_gcode', 'machine_end_gcode',
  'machine_pause_gcode', 'template_custom_gcode',
  'change_filament_gcode', 'layer_change_gcode',
  'time_lapse_gcode', 'before_layer_change_gcode',
  'file_start_gcode', 'printing_by_object_gcode',
  'wrapping_detection_gcode', 'change_extrusion_role_gcode',
  'filament_start_gcode', 'filament_end_gcode',
  'adaptive_pressure_advance_model',
]);

export interface FieldRef {
  key: string;
  compound: boolean;
}

export interface SectionLayout {
  name: string;
  icon: string;
  fields: FieldRef[];
}

export interface TabLayout {
  name: string;
  icon: string;
  sections: SectionLayout[];
}

export interface ProfileTypeMetadata {
  tabs: TabLayout[];
  settings: Record<string, SettingMetadata>;
}

type ProfileType = 'filament' | 'machine' | 'process';
type ViewMode = 'simple' | 'advanced';

// ── Helpers ─────────────────────────────────────────────────────────────

/** Blue-tinted OrcaSlicer section icon */
const OrcaIcon: React.FC<{ icon: string }> = ({ icon }) => (
  <img
    src={`/icons/orca/${icon}.svg`}
    alt=""
    width={16}
    height={16}
    className="shrink-0 filter-[invert(35%)_sepia(90%)_saturate(500%)_hue-rotate(190deg)_brightness(95%)]"
  />
);

/** Map metadata type + gui_type to the SettingRow control type */
function resolveControlType(meta: SettingMetadata): 'checkbox' | 'number' | 'text' | 'color' | 'select' | 'textarea' | 'point' {
  if (meta.gui_type === 'color') return 'color';
  if (TEXTAREA_KEYS.has(meta.key)) return 'textarea';
  if (KNOWN_ENUMS[meta.key] || meta.type === 'enum' || meta.gui_type === 'enum_open') return 'select';
  if (meta.type === 'point') return 'point';
  switch (meta.type) {
    case 'bool':
      return 'checkbox';
    case 'float':
    case 'int':
    case 'percent':
      return 'number';
    default:
      return 'text';
  }
}

/** Coerce raw settings value to a number, falling back to metadata default */
function toNumber(raw: unknown, meta: SettingMetadata): number {
  if (typeof raw === 'number') return raw;
  if (typeof raw === 'string') {
    const n = parseFloat(raw);
    if (!isNaN(n)) return n;
  }
  const d = parseFloat(meta.default ?? '0');
  return isNaN(d) ? 0 : d;
}

function toBool(raw: unknown, meta: SettingMetadata): boolean {
  if (typeof raw === 'boolean') return raw;
  if (typeof raw === 'string') return raw === 'true' || raw === '1';
  return meta.default === 'true';
}

/** Parse a point value "x,y" or "0x0" into [x, y] */
function parsePoint(raw: unknown, meta: SettingMetadata): [number, number] {
  const str = raw != null ? String(raw) : (meta.default ?? '0, 0');
  // Handle "0x0", "0,0", "0, 0" formats
  const parts = str.split(/[x,]\s*/);
  const x = parseFloat(parts[0] ?? '0');
  const y = parseFloat(parts[1] ?? '0');
  return [isNaN(x) ? 0 : x, isNaN(y) ? 0 : y];
}

function toString(raw: unknown, meta: SettingMetadata): string {
  if (raw === undefined || raw === null) return meta.default ?? '';
  return String(raw);
}

// ── MetadataSection ─────────────────────────────────────────────────────

interface MetadataSectionProps {
  section: SectionLayout;
  allSettings: Record<string, SettingMetadata>;
  values: Record<string, unknown>;
  onUpdate: (key: string, value: unknown) => void;
  viewMode: ViewMode;
  disabled: boolean;
}

const MetadataSection: React.FC<MetadataSectionProps> = ({
  section,
  allSettings,
  values,
  onUpdate,
  viewMode,
  disabled,
}) => {
  // Resolve visible fields: filter by mode and existence in settings dict
  const visibleFields = useMemo(() => {
    return section.fields.filter((f) => {
      const meta = allSettings[f.key];
      if (!meta) return false;
      if (viewMode === 'simple' && meta.mode === 'advanced') return false;
      return true;
    });
  }, [section.fields, allSettings, viewMode]);

  if (visibleFields.length === 0) return null;

  return (
    <div>
      <SectionHeader
        icon={<OrcaIcon icon={section.icon} />}
        title={section.name}
      />
      <div>
        {visibleFields.map((field) => {
          const meta = allSettings[field.key];
          const controlType = resolveControlType(meta);

          switch (controlType) {
            case 'checkbox':
              return (
                <SettingRow
                  key={field.key}
                  type="checkbox"
                  label={meta.label}
                  tooltip={meta.tooltip}
                  checked={toBool(values[field.key], meta)}
                  onChange={(v) => onUpdate(field.key, v)}
                  disabled={disabled}
                />
              );
            case 'number':
              return (
                <SettingRow
                  key={field.key}
                  type="number"
                  label={meta.label}
                  tooltip={meta.tooltip}
                  value={toNumber(values[field.key], meta)}
                  min={meta.min}
                  max={meta.max}
                  step={meta.type === 'int' ? 1 : 0.1}
                  unit={meta.unit}
                  onChange={(v) => onUpdate(field.key, v)}
                  disabled={disabled}
                />
              );
            case 'color':
              return (
                <SettingRow
                  key={field.key}
                  type="color"
                  label={meta.label}
                  tooltip={meta.tooltip}
                  value={toString(values[field.key], meta)}
                  onChange={(v) => onUpdate(field.key, v)}
                  disabled={disabled}
                />
              );
            case 'select': {
              const options = KNOWN_ENUMS[field.key]
                ?? meta.enum_values?.map((v: string) => ({ value: v, label: v }))
                ?? [];
              return (
                <SettingRow
                  key={field.key}
                  type="select"
                  label={meta.label}
                  tooltip={meta.tooltip}
                  value={toString(values[field.key], meta)}
                  options={options}
                  onChange={(v) => onUpdate(field.key, v)}
                  disabled={disabled}
                />
              );
            }
            case 'textarea': {
              // Skip label when section has only one textarea (section header IS the label)
              const showLabel = visibleFields.length > 1;
              return (
                <div key={field.key} className="py-0.5">
                  {showLabel && (
                    <div className="flex items-center gap-1.5 mb-1">
                      <span className="text-xs text-pf-text-secondary" title={meta.tooltip}>{meta.label}</span>
                    </div>
                  )}
                  <Textarea
                    rows={8}
                    value={toString(values[field.key], meta)}
                    onChange={(e) => onUpdate(field.key, e.target.value)}
                    disabled={disabled}
                    className="font-mono text-sm"
                  />
                </div>
              );
            }
            case 'point': {
              const [px, py] = parsePoint(values[field.key], meta);
              return (
                <div key={field.key} className="flex items-center gap-3 py-0.5">
                  <div className="flex items-center gap-1.5 w-2/5 shrink-0">
                    <span className="text-xs text-pf-text-secondary" title={meta.tooltip}>{meta.label}</span>
                  </div>
                  <div className="flex items-center gap-2 flex-1">
                    <span className="text-xs text-pf-text-muted">X</span>
                    <input
                      type="number"
                      title={`${meta.label} X`}
                      className="flex-1 px-2 py-1 text-sm text-right bg-pf-panel border border-pf-border rounded"
                      value={px}
                      onChange={(e) => onUpdate(field.key, `${e.target.value},${py}`)}
                      disabled={disabled}
                    />
                    <span className="text-xs text-pf-text-muted">Y</span>
                    <input
                      type="number"
                      title={`${meta.label} Y`}
                      className="flex-1 px-2 py-1 text-sm text-right bg-pf-panel border border-pf-border rounded"
                      value={py}
                      onChange={(e) => onUpdate(field.key, `${px},${e.target.value}`)}
                      disabled={disabled}
                    />
                  </div>
                </div>
              );
            }
            default:
              return (
                <SettingRow
                  key={field.key}
                  type="text"
                  label={meta.label}
                  tooltip={meta.tooltip}
                  value={toString(values[field.key], meta)}
                  onChange={(v) => onUpdate(field.key, v)}
                  disabled={disabled}
                />
              );
          }
        })}
      </div>
    </div>
  );
};

// ── MetadataTab ─────────────────────────────────────────────────────────

interface MetadataTabProps {
  tab: TabLayout;
  allSettings: Record<string, SettingMetadata>;
  values: Record<string, unknown>;
  onUpdate: (key: string, value: unknown) => void;
  viewMode: ViewMode;
  disabled: boolean;
}

const MetadataTab: React.FC<MetadataTabProps> = ({
  tab,
  allSettings,
  values,
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
        onUpdate={onUpdate}
        viewMode={viewMode}
        disabled={disabled}
      />
    ))}
  </div>
);

// ── MetadataProfileEditor (top-level) ───────────────────────────────────

export interface MetadataProfileEditorProps {
  profileType: ProfileType;
  settings: Record<string, unknown>;
  onUpdate: (key: string, value: unknown) => void;
  initialViewMode?: ViewMode;
  disabled?: boolean;
  className?: string;
}

export const MetadataProfileEditor: React.FC<MetadataProfileEditorProps> = ({
  profileType,
  settings,
  onUpdate,
  initialViewMode = 'simple',
  disabled = false,
  className = '',
}) => {
  const profileMeta = (metadata as unknown as Record<string, ProfileTypeMetadata>)[profileType];
  const [viewMode, setViewMode] = useState<ViewMode>(initialViewMode);
  const [activeTabIdx, setActiveTabIdx] = useState(0);

  const handleToggleMode = useCallback(() => {
    setViewMode((m) => (m === 'simple' ? 'advanced' : 'simple'));
  }, []);

  const tabs = profileMeta.tabs;
  const activeTab = tabs[activeTabIdx] ?? tabs[0];

  return (
    <div className={`bg-pf-bg-1 rounded-lg border border-pf-border ${className}`}>
      {/* Tab bar + Advanced toggle */}
      <div className="flex items-center justify-between px-4 py-2 border-b border-pf-border">
        <div className="flex gap-1 overflow-x-auto">
          {tabs.map((tab, idx) => (
            <Button
              key={tab.name}
              variant="unstyled"
              type="button"
              size="sm"
              onClick={() => setActiveTabIdx(idx)}
              disabled={disabled}
              className={`px-2 py-0.5 text-[10px] font-medium rounded-full whitespace-nowrap
                ${idx === activeTabIdx
                  ? 'bg-pf-accent-2/15 text-pf-accent-2 ring-1 ring-pf-accent-2/40'
                  : 'text-pf-text-secondary hover:text-pf-text-primary'}`}
            >
              <span className="inline-flex items-center gap-1">
                <OrcaIcon icon={tab.icon} />
                {tab.name}
              </span>
            </Button>
          ))}
        </div>

        {/* Advanced toggle — matches MachineProfileEditor pattern */}
        <Button
          variant="unstyled"
          type="button"
          onClick={handleToggleMode}
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
      <div className="p-2 h-96 overflow-y-auto">
        <MetadataTab
          tab={activeTab}
          allSettings={profileMeta.settings}
          values={settings}
          onUpdate={onUpdate}
          viewMode={viewMode}
          disabled={disabled}
        />
      </div>
    </div>
  );
};
