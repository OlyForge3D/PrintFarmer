/**
 * OrcaSlicer-style Filament Profile Editor
 * 7-tab layout matching SimplyPrint: Filament | Cooling | Setting Overrides |
 * Advanced | Multimaterial | Dependencies | Notes
 */
import React, { useState, useCallback } from 'react';
import { Button, Textarea } from '@/common/components/ui';
import { SettingRow, SectionHeader } from './SettingRow';
import {
  TemperatureIcon,
  CoolingIcon,
  RetractionIcon,
  SpeedIcon,
  PrecisionIcon,
} from './SlicerSettingIcons';
import type {
  FilamentSettingsViewMode,
  FilamentCategory,
  OrcaFilamentSettings,
} from './filamentSettingsTypes';
import {
  ORCA_FILAMENT_MODE_MAP,
} from './filamentSettingsTypes';

// -- Local icon components ---------------------------------------------------

const FlowIcon: React.FC<{ className?: string }> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <path d="M12 3v18" />
    <path d="M8 12l4 4 4-4" />
    <ellipse cx="12" cy="20" rx="6" ry="2" fill="currentColor" opacity="0.3" />
  </svg>
);

const FilamentIcon: React.FC<{ className?: string }> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <circle cx="12" cy="12" r="8" />
    <circle cx="12" cy="12" r="3" />
    <path d="M12 4v3M12 17v3M4 12h3M17 12h3" />
  </svg>
);

// GcodeIcon removed — G-code sections now use SectionHeader without icons

const LinkIcon: React.FC<{ className?: string }> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71" />
    <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71" />
  </svg>
);

const NotesIcon: React.FC<{ className?: string }> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
    <polyline points="14 2 14 8 20 8" />
    <line x1="16" y1="13" x2="8" y2="13" />
    <line x1="16" y1="17" x2="8" y2="17" />
    <polyline points="10 9 9 9 8 9" />
  </svg>
);

// -- Types -------------------------------------------------------------------

interface FilamentProfileEditorProps {
  settings: Partial<OrcaFilamentSettings>;
  onChange: (settings: Partial<OrcaFilamentSettings>) => void;
  viewMode: FilamentSettingsViewMode;
  onViewModeChange?: (mode: FilamentSettingsViewMode) => void;
  disabled?: boolean;
  className?: string;
  isCategoryDirty?: (category: FilamentCategory) => boolean;
}

// -- Constants ---------------------------------------------------------------

const CATEGORY_TABS: { id: FilamentCategory; label: string }[] = [
  { id: 'filament', label: 'Filament' },
  { id: 'cooling', label: 'Cooling' },
  { id: 'setting_overrides', label: 'Setting Overrides' },
  { id: 'advanced', label: 'Advanced' },
  { id: 'multimaterial', label: 'Multimaterial' },
  { id: 'dependencies', label: 'Dependencies' },
  { id: 'notes', label: 'Notes' },
];

// -- Helpers -----------------------------------------------------------------

function shouldShow(key: keyof OrcaFilamentSettings, viewMode: FilamentSettingsViewMode): boolean {
  return viewMode === 'advanced' || ORCA_FILAMENT_MODE_MAP[key as string] === 'simple';
}

// -- Main component ----------------------------------------------------------

export const FilamentProfileEditor: React.FC<FilamentProfileEditorProps> = ({
  settings,
  onChange,
  viewMode,
  onViewModeChange,
  disabled = false,
  className = '',
  isCategoryDirty,
}) => {
  const [activeCategory, setActiveCategory] = useState<FilamentCategory>('filament');

  const update = useCallback(<K extends keyof OrcaFilamentSettings>(
    key: K,
    value: OrcaFilamentSettings[K],
  ) => {
    onChange({ ...settings, [key]: value });
  }, [settings, onChange]);

  const show = (key: keyof OrcaFilamentSettings) => shouldShow(key, viewMode);

  return (
    <div className={`bg-pf-bg-1 rounded-lg border border-pf-border ${className}`}>
      {/* View Mode Toggle + Category Tabs */}
      <div className="flex items-center justify-between px-4 py-2 border-b border-pf-border">
        {/* Category tabs - filtered by view mode */}
        <div className="flex gap-1 overflow-x-auto">
          {(viewMode === 'advanced' ? CATEGORY_TABS : CATEGORY_TABS.filter(c => ['filament', 'cooling'].includes(c.id))).map(cat => {
            const isDirty = isCategoryDirty?.(cat.id) ?? false;
            return (
              <Button
                key={cat.id}
                variant="unstyled"
                type="button"
                size="sm"
                onClick={() => setActiveCategory(cat.id)}
                disabled={disabled}
                className={`px-2 py-0.5 text-[10px] font-medium rounded-full whitespace-nowrap relative
                           ${activeCategory === cat.id ? 'bg-pf-accent-2/15 text-pf-accent-2 ring-1 ring-pf-accent-2/40' : 'text-pf-text-secondary hover:text-pf-text-primary'}
                           ${isDirty ? 'ring-1 ring-pf-accent-orange ring-offset-1 ring-offset-pf-surface' : ''}`}
              >
                {cat.label}
                {isDirty && (
                  <span className="absolute -top-0.5 -right-0.5 w-2 h-2 rounded-full bg-pf-accent-orange" aria-label="Modified" />
                )}
              </Button>
            );
          })}
        </div>

        {/* OrcaSlicer-style Advanced toggle: atom icon + pill switch */}
        <div className="flex items-center gap-1 shrink-0 ml-2">
          <img src="/icons/orcaslicer-advanced.svg" alt="" className="w-4 h-4" />
          <Button
            variant="unstyled"
            type="button"
            onClick={() => {
              const newMode = viewMode === 'simple' ? 'advanced' : 'simple';
              onViewModeChange?.(newMode);
              if (newMode === 'simple' && !['filament', 'cooling'].includes(activeCategory)) {
                setActiveCategory('filament');
              }
            }}
            disabled={disabled}
            className={`relative w-8 h-4 rounded-full transition-colors ${
              viewMode === 'advanced' ? 'bg-pf-accent-2' : 'bg-pf-border'
            } disabled:opacity-50`}
            title={viewMode === 'simple' ? 'Show advanced parameters' : 'Hide advanced parameters'}
            aria-label={`Switch to ${viewMode === 'simple' ? 'Advanced' : 'Simple'} mode`}
          >
            <span className={`absolute top-0.5 w-3 h-3 rounded-full bg-white transition-transform ${
              viewMode === 'advanced' ? 'translate-x-4' : 'translate-x-0.5'
            }`} />
          </Button>
        </div>
      </div>

      {/* Tab Content - fixed height with scroll to prevent modal resizing */}
      <div className="p-4 h-96 overflow-y-auto">
        {activeCategory === 'filament' && (
          <FilamentTab settings={settings} update={update} disabled={disabled} show={show} />
        )}
        {activeCategory === 'cooling' && (
          <CoolingTab settings={settings} update={update} disabled={disabled} show={show} />
        )}
        {activeCategory === 'setting_overrides' && (
          <SettingOverridesTab settings={settings} update={update} disabled={disabled} show={show} />
        )}
        {activeCategory === 'advanced' && (
          <AdvancedTab settings={settings} update={update} disabled={disabled} show={show} />
        )}
        {activeCategory === 'multimaterial' && (
          <MultimaterialTab settings={settings} update={update} disabled={disabled} show={show} />
        )}
        {activeCategory === 'dependencies' && (
          <DependenciesTab settings={settings} update={update} disabled={disabled} show={show} />
        )}
        {activeCategory === 'notes' && (
          <NotesTab settings={settings} update={update} disabled={disabled} show={show} />
        )}
      </div>
    </div>
  );
};

// -- Shared sub-component props ----------------------------------------------

interface TabProps {
  settings: Partial<OrcaFilamentSettings>;
  update: <K extends keyof OrcaFilamentSettings>(key: K, value: OrcaFilamentSettings[K]) => void;
  disabled: boolean;
  show: (key: keyof OrcaFilamentSettings) => boolean;
}

const EmptyMode: React.FC = () => (
  <p className="text-sm text-pf-text-secondary text-center py-6">
    No settings visible in Simple mode for this tab.{" "}
    Switch to <strong>Advanced</strong> to see all settings.
  </p>
);

// -- Filament Tab ------------------------------------------------------------

const FilamentTab: React.FC<TabProps & {
}> = ({ settings, update, disabled, show }) => {
  const anyVisible = [
    'filament_type', 'name', 'filament_diameter', 'enable_pressure_advance',
    'nozzle_temperature', 'hot_plate_temp', 'chamber_temperature',
  ].some(k => show(k as keyof OrcaFilamentSettings));

  if (!anyVisible) return <EmptyMode />;

  return (
    <div className="space-y-3">
      {/* Basic Information */}
      <div>
        <SectionHeader title="Basic Information" />
        <div className="">
          {show('name') && (
            <SettingRow type="text" icon={<FilamentIcon />} label="Profile Name" value={settings.name ?? ''} onChange={v => update('name', v)} disabled={disabled} />
          )}
          {show('filament_type') && (
            <SettingRow type="text" icon={<FilamentIcon />} label="Type" value={settings.filament_type ?? ''} onChange={v => update('filament_type', v)} disabled={disabled} />
          )}
          {show('filament_vendor') && (
            <SettingRow type="text" icon={<FilamentIcon />} label="Vendor" value={settings.filament_vendor ?? ''} onChange={v => update('filament_vendor', v)} disabled={disabled} />
          )}
          {show('default_filament_colour') && (
            <SettingRow type="color" icon={<FilamentIcon />} label="Default Color" value={settings.default_filament_colour ?? '#3B82F6'} onChange={v => update('default_filament_colour', v)} disabled={disabled} />
          )}
          {show('filament_diameter') && (
            <SettingRow type="number" icon={<PrecisionIcon />} label="Diameter" value={settings.filament_diameter ?? 1.75} onChange={v => update('filament_diameter', v)} min={1.0} max={3.0} step={0.05} unit="mm" disabled={disabled} />
          )}
          {show('filament_density') && (
            <SettingRow type="number" icon={<FilamentIcon />} label="Density" value={settings.filament_density ?? 1.24} onChange={v => update('filament_density', v)} min={0.5} max={3.0} step={0.01} unit="g/cm³" disabled={disabled} />
          )}
          {show('filament_adhesiveness_category') && (
            <SettingRow type="number" icon={<FilamentIcon />} label="Adhesiveness Category" value={settings.filament_adhesiveness_category ?? 0} onChange={v => update('filament_adhesiveness_category', v)} min={0} max={5} step={1} disabled={disabled} />
          )}
          {show('temperature_vitrification') && (
            <SettingRow type="number" icon={<TemperatureIcon />} label="Softening Temperature" value={settings.temperature_vitrification ?? 0} onChange={v => update('temperature_vitrification', v)} min={0} max={300} step={5} unit="°C" disabled={disabled} />
          )}
          {show('idle_temperature') && (
            <SettingRow type="number" icon={<TemperatureIcon />} label="Idle Temperature" value={settings.idle_temperature ?? 0} onChange={v => update('idle_temperature', v)} min={0} max={300} step={5} unit="°C" disabled={disabled} />
          )}
          {show('nozzle_temperature_range_low') && (
            <SettingRow type="number" icon={<TemperatureIcon />} label="Nozzle Temp Min" value={settings.nozzle_temperature_range_low ?? 180} onChange={v => update('nozzle_temperature_range_low', v)} min={100} max={400} step={5} unit="°C" disabled={disabled} />
          )}
          {show('nozzle_temperature_range_high') && (
            <SettingRow type="number" icon={<TemperatureIcon />} label="Nozzle Temp Max" value={settings.nozzle_temperature_range_high ?? 230} onChange={v => update('nozzle_temperature_range_high', v)} min={100} max={400} step={5} unit="°C" disabled={disabled} />
          )}
          {show('required_nozzle_HRC') && (
            <SettingRow type="number" icon={<FilamentIcon />} label="Required Nozzle HRC" value={settings.required_nozzle_HRC ?? 0} onChange={v => update('required_nozzle_HRC', v)} min={0} max={100} step={1} disabled={disabled} />
          )}
          {show('filament_soluble') && (
            <SettingRow type="checkbox" icon={<FilamentIcon />} label="Soluble Material" checked={settings.filament_soluble ?? false} onChange={v => update('filament_soluble', v)} disabled={disabled} />
          )}
          {show('filament_is_support') && (
            <SettingRow type="checkbox" icon={<FilamentIcon />} label="Support Material" checked={settings.filament_is_support ?? false} onChange={v => update('filament_is_support', v)} disabled={disabled} />
          )}
        </div>
      </div>

      {/* Shrinkage */}
      {(show('filament_shrink') || show('filament_shrinkage_compensation_z')) && (
        <div>
          <SectionHeader title="Shrinkage Compensation" />
          <div className="">
            {show('filament_shrink') && (
              <SettingRow type="number" icon={<PrecisionIcon />} label="Shrinkage (XY)" value={settings.filament_shrink ?? 0} onChange={v => update('filament_shrink', v)} min={0} max={10} step={0.1} unit="%" disabled={disabled} />
            )}
            {show('filament_shrinkage_compensation_z') && (
              <SettingRow type="number" icon={<PrecisionIcon />} label="Shrinkage (Z)" value={settings.filament_shrinkage_compensation_z ?? 0} onChange={v => update('filament_shrinkage_compensation_z', v)} min={0} max={10} step={0.1} unit="%" disabled={disabled} />
            )}
          </div>
        </div>
      )}

      {/* Temperature */}
      <div>
        <SectionHeader title="Print temperature" />
        <div className="">
          {show('nozzle_temperature_initial_layer') && (
            <SettingRow type="number" icon={<TemperatureIcon />} label="Nozzle Temp – First Layer" value={settings.nozzle_temperature_initial_layer ?? 215} onChange={v => update('nozzle_temperature_initial_layer', v)} min={150} max={400} step={5} unit="°C" disabled={disabled} />
          )}
          {show('nozzle_temperature') && (
            <SettingRow type="number" icon={<TemperatureIcon />} label="Nozzle Temp – Other Layers" value={settings.nozzle_temperature ?? 200} onChange={v => update('nozzle_temperature', v)} min={150} max={400} step={5} unit="°C" disabled={disabled} />
          )}
        </div>
      </div>
      <div>
        <SectionHeader title="Bed temperature" />
        <div className="">
          {show('hot_plate_temp_initial_layer') && (
            <SettingRow type="number" icon={<TemperatureIcon />} label="Bed Temp – First Layer" value={settings.hot_plate_temp_initial_layer ?? 65} onChange={v => update('hot_plate_temp_initial_layer', v)} min={0} max={150} step={5} unit="°C" disabled={disabled} />
          )}
          {show('hot_plate_temp') && (
            <SettingRow type="number" icon={<TemperatureIcon />} label="Bed Temp – Other Layers" value={settings.hot_plate_temp ?? 60} onChange={v => update('hot_plate_temp', v)} min={0} max={150} step={5} unit="°C" disabled={disabled} />
          )}
        </div>
      </div>
      <div>
        <SectionHeader title="Print chamber temperature" />
        <div className="">
          {show('activate_chamber_temp_control') && (
            <SettingRow type="checkbox" icon={<TemperatureIcon />} label="Chamber Temperature Control" checked={settings.activate_chamber_temp_control ?? false} onChange={v => update('activate_chamber_temp_control', v)} disabled={disabled} />
          )}
          {show('chamber_temperature') && (
            <SettingRow type="number" icon={<TemperatureIcon />} label="Chamber Temperature" value={settings.chamber_temperature ?? 0} onChange={v => update('chamber_temperature', v)} min={0} max={80} step={5} unit="°C" disabled={disabled} />
          )}
        </div>
      </div>

      {/* Flow */}
      <div>
        <SectionHeader title="Flow ratio and Pressure Advance" />
        <div className="">
          {show('filament_flow_ratio') && (
            <SettingRow type="number" icon={<FlowIcon />} label="Flow Ratio" value={settings.filament_flow_ratio ?? 1.0} onChange={v => update('filament_flow_ratio', v)} min={0.5} max={2.0} step={0.01} disabled={disabled} />
          )}
          {show('pellet_flow_coefficient') && (
            <SettingRow type="number" icon={<FlowIcon />} label="Pellet Flow Coefficient" value={settings.pellet_flow_coefficient ?? 0} onChange={v => update('pellet_flow_coefficient', v)} min={0} max={10} step={0.01} disabled={disabled} />
          )}
          {show('filament_change_length') && (
            <SettingRow type="number" icon={<RetractionIcon />} label="Filament Ramming Length" value={settings.filament_change_length ?? 0} onChange={v => update('filament_change_length', v)} min={0} max={100} step={1} unit="mm" disabled={disabled} />
          )}
        </div>
      </div>

      {/* Volumetric speed limitation */}
      <div>
        <SectionHeader title="Volumetric speed limitation" />
        <div className="">
          {show('filament_adaptive_volumetric_speed') && (
            <SettingRow type="checkbox" icon={<SpeedIcon />} label="Adaptive Volumetric Speed" checked={settings.filament_adaptive_volumetric_speed ?? false} onChange={v => update('filament_adaptive_volumetric_speed', v)} disabled={disabled} />
          )}
          {show('filament_max_volumetric_speed') && (
            <SettingRow type="number" icon={<SpeedIcon />} label="Max Volumetric Speed" value={settings.filament_max_volumetric_speed ?? 12} onChange={v => update('filament_max_volumetric_speed', v)} min={1} max={30} step={0.5} unit="mm³/s" disabled={disabled} />
          )}
        </div>
      </div>

      {/* Pressure Advance (part of Flow ratio and Pressure Advance) */}
      {show('enable_pressure_advance') && (
        <div>
          <div className="">
            <SettingRow type="checkbox" icon={<PrecisionIcon />} label="Enable Pressure Advance" checked={settings.enable_pressure_advance ?? false} onChange={v => update('enable_pressure_advance', v)} disabled={disabled} />
            {settings.enable_pressure_advance && (
              <>
                {show('pressure_advance') && (
                  <SettingRow type="number" icon={<PrecisionIcon />} label="Pressure Advance Value" value={settings.pressure_advance ?? 0.04} onChange={v => update('pressure_advance', v)} min={0} max={2} step={0.005} disabled={disabled} />
                )}
                {show('adaptive_pressure_advance') && (
                  <SettingRow type="checkbox" icon={<PrecisionIcon />} label="Adaptive PA (beta)" checked={settings.adaptive_pressure_advance ?? false} onChange={v => update('adaptive_pressure_advance', v)} disabled={disabled} />
                )}
                {show('adaptive_pressure_advance_overhangs') && (
                  <SettingRow type="checkbox" icon={<PrecisionIcon />} label="Adaptive PA for Overhangs" checked={settings.adaptive_pressure_advance_overhangs ?? false} onChange={v => update('adaptive_pressure_advance_overhangs', v)} disabled={disabled} />
                )}
                {show('adaptive_pressure_advance_bridges') && (
                  <SettingRow type="number" icon={<PrecisionIcon />} label="PA for Bridges" value={settings.adaptive_pressure_advance_bridges ?? 0} onChange={v => update('adaptive_pressure_advance_bridges', v)} min={0} max={2} step={0.005} disabled={disabled} />
                )}
              </>
            )}
          </div>
        </div>
      )}
    </div>
  );
};

// -- Cooling Tab -------------------------------------------------------------

const CoolingTab: React.FC<TabProps> = ({ settings, update, disabled, show }) => {
  const anyVisible = [
    'close_fan_the_first_x_layers', 'fan_min_speed', 'fan_max_speed',
    'slow_down_layer_time', 'enable_overhang_bridge_fan', 'additional_cooling_fan_speed',
  ].some(k => show(k as keyof OrcaFilamentSettings));

  if (!anyVisible) return <EmptyMode />;

  return (
    <div className="">
      <SectionHeader title="Cooling for specific layer" />
      {show('close_fan_the_first_x_layers') && (
        <SettingRow
          type="number"
          icon={<CoolingIcon />}
          label="No Cooling for the First N Layers"
          description="Disable fan for initial layers"
          value={settings.close_fan_the_first_x_layers ?? 1}
          onChange={v => update('close_fan_the_first_x_layers', v)}
          min={0}
          max={20}
          step={1}
          unit="layers"
          disabled={disabled}
        />
      )}
      {show('full_fan_speed_layer') && (
        <SettingRow
          type="number"
          icon={<CoolingIcon />}
          label="Full Fan Speed at Layer"
          value={settings.full_fan_speed_layer ?? 3}
          onChange={v => update('full_fan_speed_layer', v)}
          min={1}
          max={50}
          step={1}
          disabled={disabled}
        />
      )}
      <SectionHeader title="Part cooling fan" />
      {show('fan_min_speed') && (
        <SettingRow
          type="number"
          icon={<CoolingIcon />}
          label="Min Fan Speed"
          value={settings.fan_min_speed ?? 35}
          onChange={v => update('fan_min_speed', v)}
          min={0}
          max={100}
          step={5}
          unit="%"
          disabled={disabled}
        />
      )}
      {show('fan_cooling_layer_time') && (
        <SettingRow
          type="number"
          icon={<CoolingIcon />}
          label="Layer Time (for Min Fan)"
          description="Activate min fan speed when layer time exceeds this"
          value={settings.fan_cooling_layer_time ?? 10}
          onChange={v => update('fan_cooling_layer_time', v)}
          min={0}
          max={120}
          step={1}
          unit="s"
          disabled={disabled}
        />
      )}
      {show('fan_max_speed') && (
        <SettingRow
          type="number"
          icon={<CoolingIcon />}
          label="Max Fan Speed"
          value={settings.fan_max_speed ?? 100}
          onChange={v => update('fan_max_speed', v)}
          min={0}
          max={100}
          step={5}
          unit="%"
          disabled={disabled}
        />
      )}
      {show('slow_down_layer_time') && (
        <SettingRow
          type="number"
          icon={<CoolingIcon />}
          label="Layer Time (for Slow-Down)"
          description="Slow down and max fan when layer prints faster"
          value={settings.slow_down_layer_time ?? 5}
          onChange={v => update('slow_down_layer_time', v)}
          min={0}
          max={120}
          step={1}
          unit="s"
          disabled={disabled}
        />
      )}
      {show('reduce_fan_stop_start_freq') && (
        <SettingRow
          type="checkbox"
          icon={<CoolingIcon />}
          label="Keep Fan Always On"
          checked={settings.reduce_fan_stop_start_freq ?? false}
          onChange={v => update('reduce_fan_stop_start_freq', v)}
          disabled={disabled}
        />
      )}
      {show('slow_down_for_layer_cooling') && (
        <SettingRow
          type="checkbox"
          icon={<CoolingIcon />}
          label="Slow Down Printing for Better Layer Cooling"
          checked={settings.slow_down_for_layer_cooling ?? true}
          onChange={v => update('slow_down_for_layer_cooling', v)}
          disabled={disabled}
        />
      )}
      {show('dont_slow_down_outer_wall') && (
        <SettingRow
          type="checkbox"
          icon={<CoolingIcon />}
          label="Don't Slow Down Outer Walls"
          checked={settings.dont_slow_down_outer_wall ?? false}
          onChange={v => update('dont_slow_down_outer_wall', v)}
          disabled={disabled}
        />
      )}
      {show('slow_down_min_speed') && (
        <SettingRow
          type="number"
          icon={<SpeedIcon />}
          label="Min Print Speed (when slowing for cooling)"
          value={settings.slow_down_min_speed ?? 10}
          onChange={v => update('slow_down_min_speed', v)}
          min={1}
          max={100}
          step={1}
          unit="mm/s"
          disabled={disabled}
        />
      )}
      {show('enable_overhang_bridge_fan') && (
        <SettingRow
          type="checkbox"
          icon={<CoolingIcon />}
          label="Force Cooling for Overhangs and Bridges"
          checked={settings.enable_overhang_bridge_fan ?? true}
          onChange={v => update('enable_overhang_bridge_fan', v)}
          disabled={disabled}
        />
      )}
      {show('overhang_fan_speed') && (
        <SettingRow
          type="number"
          icon={<CoolingIcon />}
          label="Overhangs and External Bridges Fan Speed"
          value={settings.overhang_fan_speed ?? 100}
          onChange={v => update('overhang_fan_speed', v)}
          min={0}
          max={100}
          step={5}
          unit="%"
          disabled={disabled}
        />
      )}
      {show('internal_bridge_fan_speed') && (
        <SettingRow
          type="number"
          icon={<CoolingIcon />}
          label="Internal Bridges Fan Speed"
          value={settings.internal_bridge_fan_speed ?? 100}
          onChange={v => update('internal_bridge_fan_speed', v)}
          min={0}
          max={100}
          step={5}
          unit="%"
          disabled={disabled}
        />
      )}
      {show('support_material_interface_fan_speed') && (
        <SettingRow
          type="number"
          icon={<CoolingIcon />}
          label="Support Interface Fan Speed"
          value={settings.support_material_interface_fan_speed ?? -1}
          onChange={v => update('support_material_interface_fan_speed', v)}
          min={-1}
          max={100}
          step={5}
          unit="%"
          disabled={disabled}
        />
      )}
      {show('ironing_fan_speed') && (
        <SettingRow
          type="number"
          icon={<CoolingIcon />}
          label="Ironing Fan Speed"
          value={settings.ironing_fan_speed ?? -1}
          onChange={v => update('ironing_fan_speed', v)}
          min={-1}
          max={100}
          step={5}
          unit="%"
          disabled={disabled}
        />
      )}
      <SectionHeader title="Auxiliary part cooling fan" />
      {show('additional_cooling_fan_speed') && (
        <SettingRow
          type="number"
          icon={<CoolingIcon />}
          label="Additional Cooling Fan Speed"
          value={settings.additional_cooling_fan_speed ?? 0}
          onChange={v => update('additional_cooling_fan_speed', v)}
          min={0}
          max={100}
          step={5}
          unit="%"
          disabled={disabled}
        />
      )}
      <SectionHeader title="Exhaust fan" />
      {show('activate_air_filtration') && (
        <SettingRow
          type="checkbox"
          icon={<CoolingIcon />}
          label="Activate Air Filtration"
          checked={settings.activate_air_filtration ?? false}
          onChange={v => update('activate_air_filtration', v)}
          disabled={disabled}
        />
      )}
      {show('during_print_exhaust_fan_speed') && (
        <SettingRow
          type="number"
          icon={<CoolingIcon />}
          label="Exhaust Fan Speed (During Print)"
          value={settings.during_print_exhaust_fan_speed ?? 0}
          onChange={v => update('during_print_exhaust_fan_speed', v)}
          min={0}
          max={100}
          step={5}
          unit="%"
          disabled={disabled}
        />
      )}
      {show('complete_print_exhaust_fan_speed') && (
        <SettingRow
          type="number"
          icon={<CoolingIcon />}
          label="Exhaust Fan Speed (After Print)"
          value={settings.complete_print_exhaust_fan_speed ?? 70}
          onChange={v => update('complete_print_exhaust_fan_speed', v)}
          min={0}
          max={100}
          step={5}
          unit="%"
          disabled={disabled}
        />
      )}
    </div>
  );
};

// -- Setting Overrides Tab ---------------------------------------------------

const SettingOverridesTab: React.FC<TabProps> = ({ settings, update, disabled, show }) => {
  const anyVisible = [
    'filament_retraction_length', 'filament_z_hop',
    'filament_long_retractions_when_cut', 'filament_retraction_distances_when_cut',
  ].some(k => show(k as keyof OrcaFilamentSettings));

  if (!anyVisible) return <EmptyMode />;

  return (
    <div className="">
      <SectionHeader title="Retraction" />
      {show('filament_retraction_length') && (
        <SettingRow
          type="number"
          icon={<RetractionIcon />}
          label="Retraction Length"
          description="Per-filament retraction override"
          value={settings.filament_retraction_length ?? 0.8}
          onChange={v => update('filament_retraction_length', v)}
          min={0}
          max={20}
          step={0.1}
          unit="mm"
          disabled={disabled}
        />
      )}
      {show('filament_z_hop') && (
        <SettingRow
          type="number"
          icon={<RetractionIcon />}
          label="Z-Hop Height"
          value={settings.filament_z_hop ?? 0}
          onChange={v => update('filament_z_hop', v)}
          min={0}
          max={5}
          step={0.1}
          unit="mm"
          disabled={disabled}
        />
      )}
      {show('filament_retract_lift_above') && (
        <SettingRow
          type="number"
          icon={<RetractionIcon />}
          label="Only Lift Z Above"
          value={settings.filament_retract_lift_above ?? 0}
          onChange={v => update('filament_retract_lift_above', v)}
          min={0}
          max={300}
          step={0.1}
          unit="mm"
          disabled={disabled}
        />
      )}
      {show('filament_retract_lift_below') && (
        <SettingRow
          type="number"
          icon={<RetractionIcon />}
          label="Only Lift Z Below"
          value={settings.filament_retract_lift_below ?? 0}
          onChange={v => update('filament_retract_lift_below', v)}
          min={0}
          max={300}
          step={0.1}
          unit="mm"
          disabled={disabled}
        />
      )}
      {show('filament_retraction_speed') && (
        <SettingRow
          type="number"
          icon={<SpeedIcon />}
          label="Retraction Speed"
          value={settings.filament_retraction_speed ?? 0}
          onChange={v => update('filament_retraction_speed', v)}
          min={0}
          max={200}
          step={5}
          unit="mm/s"
          disabled={disabled}
        />
      )}
      {show('filament_deretraction_speed') && (
        <SettingRow
          type="number"
          icon={<SpeedIcon />}
          label="Deretraction Speed"
          value={settings.filament_deretraction_speed ?? 0}
          onChange={v => update('filament_deretraction_speed', v)}
          min={0}
          max={200}
          step={5}
          unit="mm/s"
          disabled={disabled}
        />
      )}
      {show('filament_retract_restart_extra') && (
        <SettingRow
          type="number"
          icon={<RetractionIcon />}
          label="Extra Length on Restart"
          value={settings.filament_retract_restart_extra ?? 0}
          onChange={v => update('filament_retract_restart_extra', v)}
          min={-2}
          max={2}
          step={0.05}
          unit="mm"
          disabled={disabled}
        />
      )}
      {show('filament_retraction_minimum_travel') && (
        <SettingRow
          type="number"
          icon={<RetractionIcon />}
          label="Travel Distance Threshold"
          description="Minimum travel to trigger retraction"
          value={settings.filament_retraction_minimum_travel ?? 1}
          onChange={v => update('filament_retraction_minimum_travel', v)}
          min={0}
          max={20}
          step={0.5}
          unit="mm"
          disabled={disabled}
        />
      )}
      {show('filament_retract_when_changing_layer') && (
        <SettingRow
          type="checkbox"
          icon={<RetractionIcon />}
          label="Retract on Layer Change"
          checked={settings.filament_retract_when_changing_layer ?? false}
          onChange={v => update('filament_retract_when_changing_layer', v)}
          disabled={disabled}
        />
      )}
      {show('filament_wipe') && (
        <SettingRow
          type="checkbox"
          icon={<RetractionIcon />}
          label="Wipe While Retracting"
          checked={settings.filament_wipe ?? false}
          onChange={v => update('filament_wipe', v)}
          disabled={disabled}
        />
      )}
      {show('filament_wipe_distance') && (
        <SettingRow
          type="number"
          icon={<RetractionIcon />}
          label="Wipe Distance"
          value={settings.filament_wipe_distance ?? 1}
          onChange={v => update('filament_wipe_distance', v)}
          min={0}
          max={10}
          step={0.1}
          unit="mm"
          disabled={disabled}
        />
      )}
      {show('filament_retract_before_wipe') && (
        <SettingRow
          type="number"
          icon={<RetractionIcon />}
          label="Retract Amount Before Wipe"
          value={settings.filament_retract_before_wipe ?? 0}
          onChange={v => update('filament_retract_before_wipe', v)}
          min={0}
          max={100}
          step={5}
          unit="%"
          disabled={disabled}
        />
      )}
      {show('filament_long_retractions_when_cut') && (
        <SettingRow
          type="checkbox"
          icon={<RetractionIcon />}
          label="Long Retraction When Cut (beta)"
          checked={settings.filament_long_retractions_when_cut ?? false}
          onChange={v => update('filament_long_retractions_when_cut', v)}
          disabled={disabled}
        />
      )}
      {show('filament_retraction_distances_when_cut') && (
        <SettingRow
          type="number"
          icon={<RetractionIcon />}
          label="Retraction Distance When Cut"
          value={settings.filament_retraction_distances_when_cut ?? 0}
          onChange={v => update('filament_retraction_distances_when_cut', v)}
          min={0}
          max={100}
          step={1}
          unit="mm"
          disabled={disabled}
        />
      )}
      <SectionHeader title="Ironing" />
      {show('filament_ironing_flow') && (
        <SettingRow
          type="number"
          icon={<FlowIcon />}
          label="Ironing Flow"
          value={settings.filament_ironing_flow ?? 0.15}
          onChange={v => update('filament_ironing_flow', v)}
          min={0}
          max={2}
          step={0.01}
          disabled={disabled}
        />
      )}
      {show('filament_ironing_spacing') && (
        <SettingRow
          type="number"
          icon={<PrecisionIcon />}
          label="Ironing Line Spacing"
          value={settings.filament_ironing_spacing ?? 0.1}
          onChange={v => update('filament_ironing_spacing', v)}
          min={0}
          max={2}
          step={0.05}
          unit="mm"
          disabled={disabled}
        />
      )}
      {show('filament_ironing_inset') && (
        <SettingRow
          type="number"
          icon={<PrecisionIcon />}
          label="Ironing Inset"
          value={settings.filament_ironing_inset ?? 0.25}
          onChange={v => update('filament_ironing_inset', v)}
          min={0}
          max={5}
          step={0.1}
          unit="mm"
          disabled={disabled}
        />
      )}
      {show('filament_ironing_speed') && (
        <SettingRow
          type="number"
          icon={<SpeedIcon />}
          label="Ironing Speed"
          value={settings.filament_ironing_speed ?? 15}
          onChange={v => update('filament_ironing_speed', v)}
          min={1}
          max={100}
          step={5}
          unit="mm/s"
          disabled={disabled}
        />
      )}
    </div>
  );
};

// -- Advanced Tab ------------------------------------------------------------

const AdvancedTab: React.FC<TabProps> = ({ settings, update, disabled, show }) => {
  if (!show('filament_start_gcode')) return <EmptyMode />;

  const flowRatioFields: [keyof OrcaFilamentSettings, string][] = [
    ['outer_wall_flow_ratio', 'Outer Wall'],
    ['inner_wall_flow_ratio', 'Inner Wall'],
    ['top_solid_infill_flow_ratio', 'Top Solid Infill'],
    ['bottom_solid_infill_flow_ratio', 'Bottom Solid Infill'],
    ['internal_solid_infill_flow_ratio', 'Internal Solid Infill'],
    ['sparse_infill_flow_ratio', 'Sparse Infill'],
    ['gap_fill_flow_ratio', 'Gap Fill'],
    ['support_flow_ratio', 'Support'],
    ['support_interface_flow_ratio', 'Support Interface'],
    ['overhang_flow_ratio', 'Overhang'],
    ['first_layer_flow_ratio', 'First Layer'],
  ];

  return (
    <div className="space-y-4">
      <div>
        <SectionHeader title="Filament start G-code" />
        <Textarea
          value={settings.filament_start_gcode ?? ''}
          onChange={e => update('filament_start_gcode', e.target.value)}
          rows={6}
          disabled={disabled}
          className="font-mono text-xs"
          placeholder="; filament start g-code"
        />
      </div>

      <div>
        <SectionHeader title="Filament end G-code" />
        <Textarea
          value={settings.filament_end_gcode ?? ''}
          onChange={e => update('filament_end_gcode', e.target.value)}
          rows={6}
          disabled={disabled}
          className="font-mono text-xs"
          placeholder="; filament end g-code"
        />
      </div>

      <div className="border border-pf-border rounded-lg">
        <div className="px-4 py-2">
          <h4 className="text-sm font-medium text-pf-text-primary">Per-Feature Flow Ratios</h4>
        </div>
        <div className="">
          <SettingRow
            type="checkbox"
            icon={<FlowIcon />}
            label="Set Other Flow Ratios"
            description="All feature flow ratios follow the main Flow Ratio when enabled"
            checked={settings.set_other_flow_ratios ?? false}
            onChange={v => update('set_other_flow_ratios', v)}
            disabled={disabled}
          />
          {!settings.set_other_flow_ratios && flowRatioFields.map(([key, label]) => (
            <SettingRow
              key={key as string}
              type="number"
              icon={<FlowIcon />}
              label={`${label} Flow Ratio`}
              value={(settings[key] as number | undefined) ?? 1.0}
              onChange={v => update(key, v as OrcaFilamentSettings[typeof key])}
              min={0.5}
              max={1.5}
              step={0.01}
              disabled={disabled}
            />
          ))}
        </div>
      </div>

      <div className="border border-pf-border rounded-lg">
        <div className="px-4 py-2">
          <h4 className="text-sm font-medium text-pf-text-primary">Miscellaneous</h4>
        </div>
        <div className="">
          <SettingRow
            type="number"
            icon={<FilamentIcon />}
            label="Cost per kg"
            value={settings.cost ?? 20}
            onChange={v => update('cost', v)}
            min={0}
            max={500}
            step={1}
            unit="$"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<PrecisionIcon />}
            label="PA Smooth Time"
            value={settings.pressure_advance_smooth_time ?? 0.04}
            onChange={v => update('pressure_advance_smooth_time', v)}
            min={0}
            max={0.2}
            step={0.005}
            unit="s"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<RetractionIcon />}
            label="Filament Load Time"
            value={settings.filament_load_time ?? 0}
            onChange={v => update('filament_load_time', v)}
            min={0}
            max={60}
            step={1}
            unit="s"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<RetractionIcon />}
            label="Filament Unload Time"
            value={settings.filament_unload_time ?? 0}
            onChange={v => update('filament_unload_time', v)}
            min={0}
            max={60}
            step={1}
            unit="s"
            disabled={disabled}
          />
          <SettingRow
            type="checkbox"
            icon={<CoolingIcon />}
            label="Enable Volumetric Extrusion"
            checked={settings.enable_volumetric_extrusion ?? false}
            onChange={v => update('enable_volumetric_extrusion', v)}
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<CoolingIcon />}
            label="Close-Loop Fan Power"
            value={settings.close_loop_fan_power ?? 0}
            onChange={v => update('close_loop_fan_power', v)}
            min={0}
            max={100}
            step={5}
            unit="%"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<SpeedIcon />}
            label="Wipe Tower Interface Speed"
            value={settings.wipe_tower_interface_speed ?? 0}
            onChange={v => update('wipe_tower_interface_speed', v)}
            min={0}
            max={200}
            step={5}
            unit="mm/s"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<FlowIcon />}
            label="Wipe Tower Interface Flow Ratio"
            value={settings.wipe_tower_interface_flow_ratio ?? 1.0}
            onChange={v => update('wipe_tower_interface_flow_ratio', v)}
            min={0}
            max={2}
            step={0.05}
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<TemperatureIcon />}
            label="Flush Temperature"
            value={settings.filament_flush_temp ?? 0}
            onChange={v => update('filament_flush_temp', v)}
            min={0}
            max={400}
            step={5}
            unit="°C"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<SpeedIcon />}
            label="Flush Volumetric Speed"
            value={settings.filament_flush_volumetric_speed ?? 0}
            onChange={v => update('filament_flush_volumetric_speed', v)}
            min={0}
            max={30}
            step={0.5}
            unit="mm³/s"
            disabled={disabled}
          />
          <SettingRow
            type="text"
            icon={<TemperatureIcon />}
            label="Bed Temperature Formula"
            value={settings.bed_temperature_formula ?? ''}
            onChange={v => update('bed_temperature_formula', v)}
            disabled={disabled}
          />
        </div>
      </div>
    </div>
  );
};

// -- Multimaterial Tab -------------------------------------------------------

const MultimaterialTab: React.FC<TabProps> = ({ settings, update, disabled, show }) => {
  if (!show('filament_minimal_purge_on_wipe_tower')) return <EmptyMode />;

  return (
    <div className="">
      <SectionHeader title="Wipe tower parameters" />
      <SettingRow
        type="number"
        icon={<FlowIcon />}
        label="Minimal Purge on Wipe Tower"
        value={settings.filament_minimal_purge_on_wipe_tower ?? 15}
        onChange={v => update('filament_minimal_purge_on_wipe_tower', v)}
        min={0}
        max={500}
        step={1}
        unit="mm³"
        disabled={disabled}
      />
      <SettingRow
        type="number"
        icon={<PrecisionIcon />}
        label="Interface Layer Pre-extrusion Distance"
        value={settings.filament_tower_interface_pre_extrusion_dist ?? 0}
        onChange={v => update('filament_tower_interface_pre_extrusion_dist', v)}
        min={0}
        max={50}
        step={0.5}
        unit="mm"
        disabled={disabled}
      />
      <SettingRow
        type="number"
        icon={<PrecisionIcon />}
        label="Interface Layer Pre-extrusion Length"
        value={settings.filament_tower_interface_pre_extrusion_length ?? 0}
        onChange={v => update('filament_tower_interface_pre_extrusion_length', v)}
        min={0}
        max={50}
        step={0.5}
        unit="mm"
        disabled={disabled}
      />
      <SettingRow
        type="number"
        icon={<FlowIcon />}
        label="Tower Ironing Area"
        value={settings.filament_tower_ironing_area ?? 0}
        onChange={v => update('filament_tower_ironing_area', v)}
        min={0}
        max={100}
        step={1}
        unit="%"
        disabled={disabled}
      />
      <SettingRow
        type="number"
        icon={<FlowIcon />}
        label="Interface Layer Purge Length"
        value={settings.filament_tower_interface_purge_volume ?? 0}
        onChange={v => update('filament_tower_interface_purge_volume', v)}
        min={0}
        max={500}
        step={1}
        unit="mm"
        disabled={disabled}
      />
      <SettingRow
        type="number"
        icon={<TemperatureIcon />}
        label="Interface Layer Print Temperature"
        value={settings.filament_tower_interface_print_temp ?? 0}
        onChange={v => update('filament_tower_interface_print_temp', v)}
        min={0}
        max={400}
        step={5}
        unit="°C"
        disabled={disabled}
      />
      <SectionHeader title="Multi Filament" />
      <SettingRow
        type="checkbox"
        icon={<RetractionIcon />}
        label="Long Retraction When Extruder Change"
        checked={settings.long_retractions_when_ec ?? false}
        onChange={v => update('long_retractions_when_ec', v)}
        disabled={disabled}
      />
      <SettingRow
        type="number"
        icon={<RetractionIcon />}
        label="Retraction Distance When Extruder Change"
        value={settings.retraction_distances_when_ec ?? 0}
        onChange={v => update('retraction_distances_when_ec', v)}
        min={0}
        max={100}
        step={1}
        unit="mm"
        disabled={disabled}
      />
      <SectionHeader title="Tool change parameters with single extruder MM printers" />
      <SettingRow
        type="number"
        icon={<SpeedIcon />}
        label="Loading Speed at Start"
        value={settings.filament_loading_speed_start ?? 0}
        onChange={v => update('filament_loading_speed_start', v)}
        min={0}
        max={100}
        step={1}
        unit="mm/s"
        disabled={disabled}
      />
      <SettingRow
        type="number"
        icon={<SpeedIcon />}
        label="Loading Speed"
        value={settings.filament_loading_speed ?? 0}
        onChange={v => update('filament_loading_speed', v)}
        min={0}
        max={100}
        step={1}
        unit="mm/s"
        disabled={disabled}
      />
      <SettingRow
        type="number"
        icon={<SpeedIcon />}
        label="Unloading Speed at Start"
        value={settings.filament_unloading_speed_start ?? 0}
        onChange={v => update('filament_unloading_speed_start', v)}
        min={0}
        max={100}
        step={1}
        unit="mm/s"
        disabled={disabled}
      />
      <SettingRow
        type="number"
        icon={<SpeedIcon />}
        label="Unloading Speed"
        value={settings.filament_unloading_speed ?? 0}
        onChange={v => update('filament_unloading_speed', v)}
        min={0}
        max={100}
        step={1}
        unit="mm/s"
        disabled={disabled}
      />
      <SettingRow
        type="number"
        icon={<SpeedIcon />}
        label="Delay After Unloading"
        value={settings.filament_toolchange_delay ?? 0}
        onChange={v => update('filament_toolchange_delay', v)}
        min={0}
        max={30}
        step={0.5}
        unit="s"
        disabled={disabled}
      />
      <SettingRow
        type="number"
        icon={<CoolingIcon />}
        label="Number of Cooling Moves"
        value={settings.filament_cooling_moves ?? 4}
        onChange={v => update('filament_cooling_moves', v)}
        min={0}
        max={20}
        step={1}
        disabled={disabled}
      />
      <SettingRow
        type="number"
        icon={<SpeedIcon />}
        label="Speed of First Cooling Move"
        value={settings.filament_cooling_initial_speed ?? 2.2}
        onChange={v => update('filament_cooling_initial_speed', v)}
        min={0}
        max={50}
        step={0.1}
        unit="mm/s"
        disabled={disabled}
      />
      <SettingRow
        type="number"
        icon={<SpeedIcon />}
        label="Speed of Last Cooling Move"
        value={settings.filament_cooling_final_speed ?? 3.4}
        onChange={v => update('filament_cooling_final_speed', v)}
        min={0}
        max={50}
        step={0.1}
        unit="mm/s"
        disabled={disabled}
      />
      <SettingRow
        type="number"
        icon={<SpeedIcon />}
        label="Stamping Loading Speed"
        value={settings.filament_stamping_loading_speed ?? 0}
        onChange={v => update('filament_stamping_loading_speed', v)}
        min={0}
        max={100}
        step={1}
        unit="mm/s"
        disabled={disabled}
      />
      <SettingRow
        type="number"
        icon={<PrecisionIcon />}
        label="Stamping Distance"
        description="Measured from the center of the cooling tube"
        value={settings.filament_stamping_distance ?? 0}
        onChange={v => update('filament_stamping_distance', v)}
        min={0}
        max={100}
        step={0.5}
        unit="mm"
        disabled={disabled}
      />
      <SettingRow
        type="text"
        icon={<PrecisionIcon />}
        label="Ramming Parameters"
        value={settings.filament_ramming_parameters ?? ''}
        onChange={v => update('filament_ramming_parameters', v)}
        disabled={disabled}
      />
      <SettingRow
        type="checkbox"
        icon={<PrecisionIcon />}
        label="Enable Ramming for Multi-Tool Setups"
        checked={settings.filament_multitool_ramming ?? false}
        onChange={v => update('filament_multitool_ramming', v)}
        disabled={disabled}
      />
      <SettingRow
        type="number"
        icon={<FlowIcon />}
        label="Multi-Tool Ramming Volume"
        value={settings.filament_multitool_ramming_volume ?? 0}
        onChange={v => update('filament_multitool_ramming_volume', v)}
        min={0}
        max={500}
        step={10}
        unit="mm³"
        disabled={disabled}
      />
      <SettingRow
        type="number"
        icon={<FlowIcon />}
        label="Multi-Tool Ramming Flow"
        value={settings.filament_multitool_ramming_flow ?? 0}
        onChange={v => update('filament_multitool_ramming_flow', v)}
        min={0}
        max={5}
        step={0.1}
        unit="mm³/s"
        disabled={disabled}
      />
    </div>
  );
};

// -- Dependencies Tab --------------------------------------------------------

const DependenciesTab: React.FC<TabProps> = ({ settings, update, disabled, show }) => {
  if (!show('compatible_printers')) return <EmptyMode />;

  const printersValue = Array.isArray(settings.compatible_printers)
    ? settings.compatible_printers.join(', ')
    : '';

  return (
    <div className="space-y-4">
      <div>
        <label className="flex items-center gap-2 text-sm font-medium text-pf-text-primary mb-1">
          <LinkIcon className="w-4 h-4" />
          Compatible Printers
        </label>
        <p className="text-xs text-pf-text-secondary mb-2">
          Comma-separated list of compatible printer profile names
        </p>
        <Textarea
          value={printersValue}
          onChange={e => {
            const raw = e.target.value;
            update('compatible_printers', raw.split(',').map(s => s.trim()).filter(Boolean));
          }}
          rows={4}
          disabled={disabled}
          placeholder="Bambu Lab X1C 0.4 nozzle, Bambu Lab P1S 0.4 nozzle"
        />
      </div>

      <div>
        <label className="flex items-center gap-2 text-sm font-medium text-pf-text-primary mb-1">
          <LinkIcon className="w-4 h-4" />
          Condition
        </label>
        <p className="text-xs text-pf-text-secondary mb-2">
          Boolean expression to filter printer compatibility
        </p>
        <Textarea
          value={settings.compatible_printers_condition ?? ''}
          onChange={e => update('compatible_printers_condition', e.target.value)}
          rows={3}
          disabled={disabled}
          placeholder="printer_notes=~/.*PRINTER_VENDOR_BAMBULAB.*/i"
        />
      </div>
    </div>
  );
};

// -- Notes Tab ---------------------------------------------------------------

const NotesTab: React.FC<TabProps> = ({ settings, update, disabled, show }) => {
  if (!show('filament_notes')) return <EmptyMode />;

  return (
    <div>
      <label className="flex items-center gap-2 text-sm font-medium text-pf-text-primary mb-1">
        <NotesIcon className="w-4 h-4" />
        Filament Notes
      </label>
      <p className="text-xs text-pf-text-secondary mb-2">
        Free-form notes about this filament profile
      </p>
      <Textarea
        value={settings.filament_notes ?? ''}
        onChange={e => update('filament_notes', e.target.value)}
        rows={10}
        disabled={disabled}
        placeholder="Print notes, recommended settings, tips…"
      />
    </div>
  );
};

export default FilamentProfileEditor;
