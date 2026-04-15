/**
 * OrcaSlicer-style Machine Profile Editor
 * Simple | Advanced view modes with 6-tab SimplyPrint-style layout.
 */
import React, { useState, useCallback } from 'react';
import { Button, FormField, Textarea } from '@/common/components/ui';
import { SettingRow, SectionHeader } from './SettingRow';
import {
  SpeedIcon,
  AccelerationIcon,
} from './SlicerSettingIcons';
import type {
  MachineSettingsViewMode,
  MachineCategory,
  OrcaMachineSettings,
} from './machineSettingsTypes';
import {
  GCODE_DIALECT_LABELS,
  MOTION_TYPE_LABELS,
  NOZZLE_TYPE_LABELS,
} from './machineSettingsTypes';

interface MachineProfileEditorProps {
  settings: Partial<OrcaMachineSettings>;
  onChange: (settings: Partial<OrcaMachineSettings>) => void;
  initialViewMode?: MachineSettingsViewMode;
  disabled?: boolean;
  className?: string;
  isCategoryDirty?: (category: MachineCategory) => boolean;
}

// -- Icons ----------------------------------------------------------------

const BuildVolumeIcon: React.FC<{ className?: string }> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" />
    <polyline points="3.27 6.96 12 12.01 20.73 6.96" />
    <line x1="12" y1="22.08" x2="12" y2="12" />
  </svg>
);

const NozzleIcon: React.FC<{ className?: string }> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <path d="M12 3v6" />
    <path d="M9 9h6v4l-3 8-3-8V9z" />
    <circle cx="12" cy="21" r="1" fill="currentColor" />
  </svg>
);

const GcodeIcon: React.FC<{ className?: string }> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <polyline points="4 17 10 11 4 5" />
    <line x1="12" y1="19" x2="20" y2="19" />
  </svg>
);

const MultimaterialIcon: React.FC<{ className?: string }> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <circle cx="12" cy="12" r="2" />
    <path d="M12 2v4M12 18v4M4.93 4.93l2.83 2.83M16.24 16.24l2.83 2.83M2 12h4M18 12h4M4.93 19.07l2.83-2.83M16.24 7.76l2.83-2.83" />
  </svg>
);

const MotionIcon: React.FC<{ className?: string }> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <path d="M5 12h14M12 5l7 7-7 7" />
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

// -- Main component -------------------------------------------------------

export const MachineProfileEditor: React.FC<MachineProfileEditorProps> = ({
  settings,
  onChange,
  initialViewMode = 'simple',
  disabled = false,
  className = '',
  isCategoryDirty,
}) => {
  const [viewMode, setViewMode] = useState<MachineSettingsViewMode>(initialViewMode);
  const [activeCategory, setActiveCategory] = useState<MachineCategory>('basic_information');

  const updateSetting = useCallback(
    <K extends keyof OrcaMachineSettings>(key: K, value: OrcaMachineSettings[K]) => {
      onChange({ ...settings, [key]: value });
    },
    [settings, onChange],
  );

  const categories: { id: MachineCategory; label: string; icon: React.ReactNode }[] = [
    { id: 'basic_information', label: 'Basic Information', icon: <BuildVolumeIcon className="w-3 h-3 shrink-0" /> },
    { id: 'machine_gcode', label: 'Machine G-code', icon: <GcodeIcon className="w-3 h-3 shrink-0" /> },
    { id: 'multimaterial', label: 'Multimaterial', icon: <MultimaterialIcon className="w-3 h-3 shrink-0" /> },
    { id: 'extruder', label: 'Extruder', icon: <NozzleIcon className="w-3 h-3 shrink-0" /> },
    { id: 'motion_ability', label: 'Motion Ability', icon: <MotionIcon className="w-3 h-3 shrink-0" /> },
    { id: 'notes', label: 'Notes', icon: <NotesIcon className="w-3 h-3 shrink-0" /> },
  ];

  return (
    <div className={`bg-pf-bg-1 rounded-lg border border-pf-border ${className}`}>
      {/* View Mode Toggle + Category Tabs */}
      <div className="flex items-center justify-between px-4 py-2 border-b border-pf-border">
        {/* Category tabs - only shown in Advanced mode */}
        <div className="flex gap-1 overflow-x-auto">
          {viewMode === 'advanced' ? categories.map((cat) => {
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
                           ${isDirty ? 'ring-1 ring-pf-accent-orange' : ''}`}
              >
                <span className="inline-flex items-center gap-1">{cat.icon}{cat.label}</span>
                {isDirty && (
                  <span className="absolute -top-0.5 -right-0.5 w-2 h-2 rounded-full bg-pf-accent-orange" aria-label="Modified" />
                )}
              </Button>
            );
          }) : <span className="text-[10px] text-pf-text-muted">Machine</span>}
        </div>

        {/* Simple/Advanced pill toggle */}
        <div className="flex items-center gap-0 shrink-0 ml-2">
          <Button variant="unstyled" size="sm" onClick={() => setViewMode('simple')} disabled={disabled}
            className={`px-2 py-0.5 text-[10px] font-medium rounded-l-md border transition-colors ${
              viewMode === 'simple' ? 'bg-pf-accent text-white border-pf-accent' : 'bg-pf-bg-2 text-pf-text-secondary border-pf-border hover:text-pf-text-primary'
            }`}>Simple</Button>
          <Button variant="unstyled" size="sm" onClick={() => setViewMode('advanced')} disabled={disabled}
            className={`px-2 py-0.5 text-[10px] font-medium rounded-r-md -ml-px border transition-colors ${
              viewMode === 'advanced' ? 'bg-pf-accent text-white border-pf-accent' : 'bg-pf-bg-2 text-pf-text-secondary border-pf-border hover:text-pf-text-primary'
            }`}>Advanced</Button>
        </div>
      </div>

      <div className="p-2 h-96 overflow-y-auto">
        {viewMode === 'simple' && (
          <SimpleMachineSettingsPanel
            settings={settings}
            onUpdate={updateSetting}
            disabled={disabled}
          />
        )}

        {viewMode === 'advanced' && (
          <>
            <div className="">
              {activeCategory === 'basic_information' && (
                <BasicInformationTab settings={settings} onUpdate={updateSetting} disabled={disabled} />
              )}
              {activeCategory === 'machine_gcode' && (
                <MachineGcodeTab settings={settings} onUpdate={updateSetting} disabled={disabled} />
              )}
              {activeCategory === 'multimaterial' && (
                <MultimaterialTab settings={settings} onUpdate={updateSetting} disabled={disabled} />
              )}
              {activeCategory === 'extruder' && (
                <ExtruderTab settings={settings} onUpdate={updateSetting} disabled={disabled} />
              )}
              {activeCategory === 'motion_ability' && (
                <MotionAbilityTab settings={settings} onUpdate={updateSetting} disabled={disabled} />
              )}
              {activeCategory === 'notes' && (
                <NotesTab settings={settings} onUpdate={updateSetting} disabled={disabled} />
              )}
            </div>
          </>
        )}
      </div>
    </div>
  );
};

// -- Shared prop type -----------------------------------------------------

interface TabPanelProps {
  settings: Partial<OrcaMachineSettings>;
  onUpdate: <K extends keyof OrcaMachineSettings>(key: K, value: OrcaMachineSettings[K]) => void;
  disabled: boolean;
}

// -- Simple mode ----------------------------------------------------------

const SimpleMachineSettingsPanel: React.FC<
  TabPanelProps
> = ({ settings, onUpdate, disabled }) => (
  <div className="">
    <div className="py-1">
      <SectionHeader icon={<BuildVolumeIcon className="w-5 h-5" />} title="Build Volume" />
      <div className="py-1.5">
        <div className="flex items-center gap-3">
          <div className="w-2/5 shrink-0"><span className="text-xs font-medium text-pf-text">Dimensions</span></div>
          <div className="flex-1 grid grid-cols-3 gap-2">
            <SettingRow type="number" label="" prefix="X" value={settings.bed_size_x ?? 220} min={50} max={1000} step={1} unit="mm" onChange={(v) => onUpdate('bed_size_x', v)} disabled={disabled} />
            <SettingRow type="number" label="" prefix="Y" value={settings.bed_size_y ?? 220} min={50} max={1000} step={1} unit="mm" onChange={(v) => onUpdate('bed_size_y', v)} disabled={disabled} />
            <SettingRow type="number" label="" prefix="Z" value={settings.printable_height ?? 250} min={50} max={1000} step={1} unit="mm" onChange={(v) => onUpdate('printable_height', v)} disabled={disabled} />
          </div>
        </div>
      </div>
    </div>
    <div className="py-1">
      <SectionHeader icon={<NozzleIcon className="w-5 h-5" />} title="Nozzle" />
      <div className="space-y-3">
        <SettingRow type="select" label="Nozzle Size" value={String(settings.nozzle_size ?? 0.4)} options={[{ value: '0.2', label: '0.2 mm' }, { value: '0.25', label: '0.25 mm' }, { value: '0.4', label: '0.4 mm' }, { value: '0.5', label: '0.5 mm' }, { value: '0.6', label: '0.6 mm' }, { value: '0.8', label: '0.8 mm' }, { value: '1.0', label: '1.0 mm' }]} onChange={(v) => onUpdate('nozzle_size', parseFloat(v))} disabled={disabled} />
        <SettingRow type="select" label="Nozzle Volume Type" value={settings.nozzle_volume_type ?? 'standard'} options={[{ value: 'standard', label: 'Standard' }, { value: 'high_flow', label: 'High Flow' }]} onChange={(v) => onUpdate('nozzle_volume_type', v as OrcaMachineSettings['nozzle_volume_type'])} disabled={disabled} />
        <SettingRow type="number" label="Nozzle HRC" value={settings.nozzle_hrc ?? 0} min={0} max={100} step={1} onChange={(v) => onUpdate('nozzle_hrc', v)} disabled={disabled} description="Rockwell C; 0 = brass" />
      </div>
    </div>
    <div className="py-1">
      <SectionHeader title="Capabilities" />
      <div className="space-y-3">
        <SettingRow type="checkbox" label="Support Multi Bed Types" checked={settings.support_multi_bed_types ?? false} onChange={(v) => onUpdate('support_multi_bed_types', v)} disabled={disabled} />
        <SettingRow type="checkbox" label="Pellet-Modded Printer" checked={settings.pellet_modded_printer ?? false} onChange={(v) => onUpdate('pellet_modded_printer', v)} disabled={disabled} />
        <SettingRow type="checkbox" label="Chamber Temp Control" checked={settings.support_chamber_temp_control ?? false} onChange={(v) => onUpdate('support_chamber_temp_control', v)} disabled={disabled} />
        <SettingRow type="checkbox" label="Air Filtration" checked={settings.support_air_filtration ?? false} onChange={(v) => onUpdate('support_air_filtration', v)} disabled={disabled} />
      </div>
    </div>
    <div className="py-1">
      <SectionHeader title="Retraction" />
      <div className="space-y-3">
        <SettingRow type="slider" label="Retraction Length" value={settings.retraction_length ?? 0.8} min={0} max={10} step={0.1} unit="mm" onChange={(v) => onUpdate('retraction_length', v)} disabled={disabled} />
        <SettingRow type="slider" label="Z Hop" value={settings.z_hop ?? 0.2} min={0} max={2} step={0.05} unit="mm" onChange={(v) => onUpdate('z_hop', v)} disabled={disabled} />
        <SettingRow type="number" label="Long Retraction (Cutter)" value={settings.long_retractions_when_cut ?? 0} min={0} max={50} step={0.5} unit="mm" onChange={(v) => onUpdate('long_retractions_when_cut', v)} disabled={disabled} />
        <SettingRow type="text" label="Retraction Distances (Cut)" value={settings.retraction_distances_when_cut ?? ''} onChange={(v) => onUpdate('retraction_distances_when_cut', v)} disabled={disabled} description="Comma-separated" />
      </div>
    </div>
  </div>
);

// -- Tab 1: Basic Information ---------------------------------------------

const BasicInformationTab: React.FC<
  TabPanelProps
> = ({ settings, onUpdate, disabled }) => {
  const nozzleTypeOptions = Object.entries(NOZZLE_TYPE_LABELS).map(([value, label]) => ({ value, label }));
  const gcodeDialectOptions = Object.entries(GCODE_DIALECT_LABELS).map(([value, label]) => ({ value, label }));
  return (
    <div className="space-y-3">
      {/* ── Printable Space ─────────────────────────────────────────── */}
      <div>
        <SectionHeader title="Printable Space" />
        <div className="">
          <SettingRow type="text" label="Excluded Bed Area" value={settings.bed_exclude_area ?? ''} onChange={(v) => onUpdate('bed_exclude_area', v)} disabled={disabled} />
          <SettingRow type="number" label="Printable Height" value={settings.printable_height ?? 250} min={50} max={1000} step={1} unit="mm" onChange={(v) => onUpdate('printable_height', v)} disabled={disabled} />
          <SettingRow type="checkbox" label="Support Multi Bed Types" checked={settings.support_multi_bed_types ?? false} onChange={(v) => onUpdate('support_multi_bed_types', v)} disabled={disabled} />
          <div className="py-1.5">
            <div className="flex items-center gap-3">
              <div className="w-2/5 shrink-0"><span className="text-xs font-medium text-pf-text">Best Object Position</span></div>
              <div className="flex-1 grid grid-cols-2 gap-2">
                <SettingRow type="number" label="" prefix="X" value={settings.best_object_pos_x ?? 0.5} min={0} max={1} step={0.05} onChange={(v) => onUpdate('best_object_pos_x', v)} disabled={disabled} />
                <SettingRow type="number" label="" prefix="Y" value={settings.best_object_pos_y ?? 0.5} min={0} max={1} step={0.05} onChange={(v) => onUpdate('best_object_pos_y', v)} disabled={disabled} />
              </div>
            </div>
          </div>
          <SettingRow type="number" label="Z Offset" value={settings.z_offset ?? 0} min={-5} max={5} step={0.01} unit="mm" onChange={(v) => onUpdate('z_offset', v)} disabled={disabled} />
          <SettingRow type="number" label="Preferred Orientation" value={settings.preferred_orientation ?? 0} min={0} max={360} step={1} unit="°" onChange={(v) => onUpdate('preferred_orientation', v)} disabled={disabled} />
        </div>
      </div>

      {/* ── Advanced ────────────────────────────────────────────────── */}
      <div>
        <SectionHeader title="Advanced" />
        <div className="">
          <SettingRow type="text" label="Printer Structure" value={settings.printer_structure ?? ''} onChange={(v) => onUpdate('printer_structure', v)} disabled={disabled} />
          <SettingRow type="select" label="G-code Flavor" value={settings.gcode_flavor ?? 'marlin2'} options={gcodeDialectOptions} onChange={(v) => onUpdate('gcode_flavor', v as OrcaMachineSettings['gcode_flavor'])} disabled={disabled} />
          <SettingRow type="checkbox" label="Pellet Modded Printer" checked={settings.pellet_modded_printer ?? false} onChange={(v) => onUpdate('pellet_modded_printer', v)} disabled={disabled} />
          <SettingRow type="checkbox" label="Disable Set Remaining Print Time" checked={settings.disable_m73 ?? false} onChange={(v) => onUpdate('disable_m73', v)} disabled={disabled} />
          <SettingRow type="text" label="G-code Thumbnails" value={settings.thumbnails ?? ''} onChange={(v) => onUpdate('thumbnails', v)} disabled={disabled} />
          <SettingRow type="checkbox" label="Use Relative E Distances" checked={settings.use_relative_e_distances ?? false} onChange={(v) => onUpdate('use_relative_e_distances', v)} disabled={disabled} />
          <SettingRow type="checkbox" label="Use Firmware Retraction" checked={settings.use_firmware_retraction ?? false} onChange={(v) => onUpdate('use_firmware_retraction', v)} disabled={disabled} />
          <SettingRow type="number" label="Time Cost" value={settings.time_cost ?? 0} min={0} max={100} step={1} unit="money/h" onChange={(v) => onUpdate('time_cost', v)} disabled={disabled} />
        </div>
      </div>

      {/* ── Cooling Fan ─────────────────────────────────────────────── */}
      <div>
        <SectionHeader title="Cooling Fan" />
        <div className="">
          <SettingRow type="number" label="Fan Speed-up Time" value={settings.fan_speedup_time ?? 0} min={0} max={10} step={0.1} unit="s" onChange={(v) => onUpdate('fan_speedup_time', v)} disabled={disabled} />
          <SettingRow type="number" label="Fan Kick-start Time" value={settings.fan_kickstart ?? 0} min={0} max={5} step={0.1} unit="s" onChange={(v) => onUpdate('fan_kickstart', v)} disabled={disabled} />
          <SettingRow type="checkbox" label="Only Overhangs" checked={settings.fan_speedup_overhangs ?? false} onChange={(v) => onUpdate('fan_speedup_overhangs', v)} disabled={disabled} />
        </div>
      </div>

      {/* ── Extruder Clearance ──────────────────────────────────────── */}
      <div>
        <SectionHeader title="Extruder Clearance" />
        <div className="">
          <SettingRow type="number" label="Radius" value={settings.extruder_clearance_radius ?? 45} min={0} max={150} step={1} unit="mm" onChange={(v) => onUpdate('extruder_clearance_radius', v)} disabled={disabled} />
          <SettingRow type="number" label="Height to Rod" value={settings.extruder_clearance_height_to_rod ?? 36} min={0} max={200} step={1} unit="mm" onChange={(v) => onUpdate('extruder_clearance_height_to_rod', v)} disabled={disabled} />
          <SettingRow type="number" label="Height to Lid" value={settings.extruder_clearance_height_to_lid ?? 40} min={0} max={200} step={1} unit="mm" onChange={(v) => onUpdate('extruder_clearance_height_to_lid', v)} disabled={disabled} />
        </div>
      </div>

      {/* ── Adaptive Bed Mesh ───────────────────────────────────────── */}
      <div>
        <SectionHeader title="Adaptive Bed Mesh" />
        <div className="">
          <div className="py-1.5">
            <div className="flex items-center gap-3">
              <div className="w-2/5 shrink-0"><span className="text-xs font-medium text-pf-text">Bed Mesh Min</span></div>
              <div className="flex-1 grid grid-cols-2 gap-2">
                <SettingRow type="number" label="" prefix="X" value={settings.bed_mesh_min_x ?? -99999} min={-99999} max={99999} step={1} unit="mm" onChange={(v) => onUpdate('bed_mesh_min_x', v)} disabled={disabled} />
                <SettingRow type="number" label="" prefix="Y" value={settings.bed_mesh_min_y ?? -99999} min={-99999} max={99999} step={1} unit="mm" onChange={(v) => onUpdate('bed_mesh_min_y', v)} disabled={disabled} />
              </div>
            </div>
          </div>
          <div className="py-1.5">
            <div className="flex items-center gap-3">
              <div className="w-2/5 shrink-0"><span className="text-xs font-medium text-pf-text">Bed Mesh Max</span></div>
              <div className="flex-1 grid grid-cols-2 gap-2">
                <SettingRow type="number" label="" prefix="X" value={settings.bed_mesh_max_x ?? 99999} min={-99999} max={99999} step={1} unit="mm" onChange={(v) => onUpdate('bed_mesh_max_x', v)} disabled={disabled} />
                <SettingRow type="number" label="" prefix="Y" value={settings.bed_mesh_max_y ?? 99999} min={-99999} max={99999} step={1} unit="mm" onChange={(v) => onUpdate('bed_mesh_max_y', v)} disabled={disabled} />
              </div>
            </div>
          </div>
          <div className="py-1.5">
            <div className="flex items-center gap-3">
              <div className="w-2/5 shrink-0"><span className="text-xs font-medium text-pf-text">Probe Point Distance</span></div>
              <div className="flex-1 grid grid-cols-2 gap-2">
                <SettingRow type="number" label="" prefix="X" value={settings.probe_point_dist_x ?? 50} min={1} max={500} step={1} unit="mm" onChange={(v) => onUpdate('probe_point_dist_x', v)} disabled={disabled} />
                <SettingRow type="number" label="" prefix="Y" value={settings.probe_point_dist_y ?? 50} min={1} max={500} step={1} unit="mm" onChange={(v) => onUpdate('probe_point_dist_y', v)} disabled={disabled} />
              </div>
            </div>
          </div>
          <SettingRow type="number" label="Mesh Margin" value={settings.adaptive_bed_mesh_margin ?? 0} min={0} max={50} step={1} unit="mm" onChange={(v) => onUpdate('adaptive_bed_mesh_margin', v)} disabled={disabled} />
        </div>
      </div>

      {/* ── Accessory ───────────────────────────────────────────────── */}
      <div>
        <SectionHeader title="Accessory" />
        <div className="">
          <SettingRow type="select" label="Nozzle Type" value={settings.nozzle_type ?? 'brass'} options={nozzleTypeOptions} onChange={(v) => onUpdate('nozzle_type', v as OrcaMachineSettings['nozzle_type'])} disabled={disabled} />
          <SettingRow type="number" label="Nozzle HRC" value={settings.nozzle_hrc ?? 0} min={0} max={100} step={1} unit="HRC" onChange={(v) => onUpdate('nozzle_hrc', v)} disabled={disabled} />
          <SettingRow type="checkbox" label="Auxiliary Part Cooling Fan" checked={settings.auxiliary_fan ?? false} onChange={(v) => onUpdate('auxiliary_fan', v)} disabled={disabled} />
          <SettingRow type="checkbox" label="Support Controlling Chamber Temperature" checked={settings.support_chamber_temp_control ?? false} onChange={(v) => onUpdate('support_chamber_temp_control', v)} disabled={disabled} />
          <SettingRow type="checkbox" label="Support Air Filtration" checked={settings.support_air_filtration ?? false} onChange={(v) => onUpdate('support_air_filtration', v)} disabled={disabled} />
        </div>
      </div>
    </div>
  );
};

// -- Tab 2: Machine G-code ------------------------------------------------

const MachineGcodeTab: React.FC<TabPanelProps> = ({ settings, onUpdate, disabled }) => {
  const gcodeDialectOptions = Object.entries(GCODE_DIALECT_LABELS).map(([value, label]) => ({ value, label }));
  const gcodeFields: Array<{ key: keyof OrcaMachineSettings; label: string; rows: number }> = [
    { key: 'file_start_gcode', label: 'File Start G-code', rows: 4 },
    { key: 'wrapping_detection_gcode', label: 'Wrapping Detection G-code', rows: 4 },
    { key: 'machine_start_gcode', label: 'Machine Start G-code', rows: 10 },
    { key: 'machine_end_gcode', label: 'Machine End G-code', rows: 10 },
    { key: 'before_layer_change_gcode', label: 'Before Layer Change G-code', rows: 6 },
    { key: 'layer_change_gcode', label: 'Layer Change G-code', rows: 6 },
    { key: 'toolchange_gcode', label: 'Change Filament G-code', rows: 6 },
    { key: 'change_extrusion_role_gcode', label: 'Change Extrusion Role G-code', rows: 4 },
    { key: 'pause_print_gcode', label: 'Pause Print G-code', rows: 4 },
    { key: 'template_custom_gcode', label: 'Template Custom G-code', rows: 4 },
    { key: 'printing_by_object_gcode', label: 'Printing By Object G-code', rows: 4 },
    { key: 'timelapse_gcode', label: 'Timelapse G-code', rows: 4 },
  ];
  return (
    <>
      <SettingRow type="select" label="G-code Flavor" icon={<GcodeIcon className="w-5 h-5" />} value={settings.gcode_flavor ?? 'marlin2'} options={gcodeDialectOptions} onChange={(v) => onUpdate('gcode_flavor', v as OrcaMachineSettings['gcode_flavor'])} disabled={disabled} />
      <div className="py-3 space-y-3">
        <SettingRow type="checkbox" label="Use Relative E Distances" checked={settings.use_relative_e_distances ?? false} onChange={(v) => onUpdate('use_relative_e_distances', v)} disabled={disabled} />
        <SettingRow type="checkbox" label="Use Firmware Retraction" checked={settings.use_firmware_retraction ?? false} onChange={(v) => onUpdate('use_firmware_retraction', v)} disabled={disabled} />
      </div>
      {gcodeFields.map(({ key, label, rows }) => (
        <div key={key as string} className="py-1">
          <FormField label={label} htmlFor={key as string}>
            <Textarea
              id={key as string}
              value={(settings[key] as string | undefined) ?? ''}
              onChange={(e) => onUpdate(key, e.target.value as OrcaMachineSettings[typeof key])}
              rows={rows}
              disabled={disabled}
              className="font-mono text-sm"
            />
          </FormField>
        </div>
      ))}
    </>
  );
};

// -- Tab 3: Multimaterial -------------------------------------------------

const MultimaterialTab: React.FC<TabPanelProps> = ({ settings, onUpdate, disabled }) => (
  <>
    <div className="py-1">
      <SectionHeader title="Single extruder multi-material setup" />
      <div className="space-y-3">
        <SettingRow type="checkbox" label="Single Extruder Multi-Material" checked={settings.single_extruder_multi_material ?? false} onChange={(v) => onUpdate('single_extruder_multi_material', v)} disabled={disabled} />
        <SettingRow type="checkbox" label="Single Extruder MM Priming" checked={settings.single_extruder_multi_material_priming ?? false} onChange={(v) => onUpdate('single_extruder_multi_material_priming', v)} disabled={disabled} />
        <SettingRow type="checkbox" label="Multi-Material Support" checked={settings.support_multi_material ?? false} onChange={(v) => onUpdate('support_multi_material', v)} disabled={disabled} />
        <SettingRow type="checkbox" label="Manual Filament Change" checked={settings.manual_filament_change ?? false} onChange={(v) => onUpdate('manual_filament_change', v)} disabled={disabled} />
        <SettingRow type="text" label="Bed Temperature Formula" value={settings.bed_temperature_formula ?? ''} onChange={(v) => onUpdate('bed_temperature_formula', v)} disabled={disabled} />
        <SettingRow type="checkbox" label="Purge in Prime Tower" checked={settings.purge_in_prime_tower ?? false} onChange={(v) => onUpdate('purge_in_prime_tower', v)} disabled={disabled} />
        <SettingRow type="checkbox" label="Enable Filament Ramming" checked={settings.enable_filament_ramming ?? false} onChange={(v) => onUpdate('enable_filament_ramming', v)} disabled={disabled} />
        <SettingRow type="checkbox" label="High Current on Filament Swap" checked={settings.high_current_on_filament_swap ?? false} onChange={(v) => onUpdate('high_current_on_filament_swap', v)} disabled={disabled} />
      </div>
    </div>
    <div className="py-1">
      <SectionHeader title="Single extruder multi-material parameters" />
      <div className="grid grid-cols-2 gap-4">
        <SettingRow type="number" label="Cooling Tube Retraction" value={settings.cooling_tube_retraction ?? 0} min={0} max={50} step={0.5} unit="mm" onChange={(v) => onUpdate('cooling_tube_retraction', v)} disabled={disabled} />
        <SettingRow type="number" label="Cooling Tube Length" value={settings.cooling_tube_length ?? 0} min={0} max={50} step={0.5} unit="mm" onChange={(v) => onUpdate('cooling_tube_length', v)} disabled={disabled} />
        <SettingRow type="number" label="Parking Pos Retraction" value={settings.parking_pos_retraction ?? 0} min={0} max={100} step={1} unit="mm" onChange={(v) => onUpdate('parking_pos_retraction', v)} disabled={disabled} />
        <SettingRow type="number" label="Extra Loading Move" value={settings.extra_loading_move ?? 0} min={0} max={50} step={0.5} unit="mm" onChange={(v) => onUpdate('extra_loading_move', v)} disabled={disabled} />
      </div>
    </div>
    <div className="py-1">
      <SectionHeader title="Advanced" />
      <div className="grid grid-cols-3 gap-4">
        <SettingRow type="number" label="Load Time" value={settings.machine_load_filament_time ?? 0} min={0} max={120} step={1} unit="s" onChange={(v) => onUpdate('machine_load_filament_time', v)} disabled={disabled} />
        <SettingRow type="number" label="Unload Time" value={settings.machine_unload_filament_time ?? 0} min={0} max={120} step={1} unit="s" onChange={(v) => onUpdate('machine_unload_filament_time', v)} disabled={disabled} />
        <SettingRow type="number" label="Tool Change Time" value={settings.machine_tool_change_time ?? 0} min={0} max={120} step={1} unit="s" onChange={(v) => onUpdate('machine_tool_change_time', v)} disabled={disabled} />
      </div>
    </div>
    <div className="py-1">
      <SectionHeader title="Wipe Tower" />
      <div className="space-y-3">
        <div className="grid grid-cols-2 gap-4">
          <SettingRow type="select" label="Tower Type" value={settings.wipe_tower_type ?? 'sparse'} options={[{ value: 'sparse', label: 'Sparse' }, { value: 'dense', label: 'Dense' }]} onChange={(v) => onUpdate('wipe_tower_type', v as OrcaMachineSettings['wipe_tower_type'])} disabled={disabled} />
          <SettingRow type="select" label="Wall Type" value={settings.wipe_tower_wall_type ?? 'single'} options={[{ value: 'single', label: 'Single' }, { value: 'double', label: 'Double' }]} onChange={(v) => onUpdate('wipe_tower_wall_type', v as OrcaMachineSettings['wipe_tower_wall_type'])} disabled={disabled} />
        </div>
        <div className="grid grid-cols-2 gap-4">
          <SettingRow type="number" label="Bridging" value={settings.wipe_tower_bridging ?? 10} min={1} max={20} step={0.5} unit="mm" onChange={(v) => onUpdate('wipe_tower_bridging', v)} disabled={disabled} />
          <SettingRow type="number" label="Max Purge Speed" value={settings.wipe_tower_max_purge_speed ?? 60} min={10} max={200} step={5} unit="mm/s" onChange={(v) => onUpdate('wipe_tower_max_purge_speed', v)} disabled={disabled} />
        </div>
        <div className="grid grid-cols-2 gap-4">
          <SettingRow type="number" label="Rib Width" value={settings.wipe_tower_rib_width ?? 0} min={0} max={5} step={0.1} unit="mm" onChange={(v) => onUpdate('wipe_tower_rib_width', v)} disabled={disabled} />
          <SettingRow type="number" label="Extra Rib Length" value={settings.wipe_tower_extra_rib_length ?? 0} min={0} max={10} step={0.5} unit="mm" onChange={(v) => onUpdate('wipe_tower_extra_rib_length', v)} disabled={disabled} />
        </div>
        <SettingRow type="checkbox" label="No Sparse Layers" checked={settings.wipe_tower_no_sparse_layers ?? false} onChange={(v) => onUpdate('wipe_tower_no_sparse_layers', v)} disabled={disabled} />
        <SettingRow type="checkbox" label="Fillet Wall" checked={settings.wipe_tower_fillet_wall ?? false} onChange={(v) => onUpdate('wipe_tower_fillet_wall', v)} disabled={disabled} />
      </div>
    </div>
  </>
);

// -- Tab 4: Extruder ------------------------------------------------------

const ExtruderTab: React.FC<TabPanelProps> = ({ settings, onUpdate, disabled }) => {
  const nozzleTypeOptions = Object.entries(NOZZLE_TYPE_LABELS).map(([value, label]) => ({ value, label }));
  return (
    <>
      <div className="py-1">
        <SectionHeader icon={<NozzleIcon className="w-5 h-5" />} title="Extruder" />
        <div className="space-y-3">
          <SettingRow type="number" label="Extruder Count" value={settings.extruder_count ?? 1} min={1} max={8} step={1} onChange={(v) => onUpdate('extruder_count', v)} disabled={disabled} />
          <SettingRow type="select" label="Extruder Type" value={settings.extruder_type ?? 'direct_drive'} options={[{ value: 'direct_drive', label: 'Direct Drive' }, { value: 'bowden', label: 'Bowden' }]} onChange={(v) => onUpdate('extruder_type', v as OrcaMachineSettings['extruder_type'])} disabled={disabled} />
          <SettingRow type="number" label="Extrusion Multiplier" value={settings.extrusion_multiplier ?? 1.0} min={0.5} max={2.0} step={0.01} onChange={(v) => onUpdate('extrusion_multiplier', v)} disabled={disabled} />
          <SettingRow type="text" label="Extruder Offset" value={settings.extruder_offset ?? '0x0'} onChange={(v) => onUpdate('extruder_offset', v)} disabled={disabled} />
          <SettingRow type="text" label="Extruder Colour" value={settings.extruder_colour ?? '#FF8000'} onChange={(v) => onUpdate('extruder_colour', v)} disabled={disabled} />
        </div>
      </div>
      <div className="py-1">
        <SectionHeader title="Basic information" />
        <div className="space-y-3">
          <SettingRow type="select" label="Nozzle Diameter" value={String(settings.nozzle_diameter ?? 0.4)} options={[{ value: '0.2', label: '0.2 mm' }, { value: '0.4', label: '0.4 mm' }, { value: '0.6', label: '0.6 mm' }, { value: '0.8', label: '0.8 mm' }]} onChange={(v) => onUpdate('nozzle_diameter', parseFloat(v))} disabled={disabled} />
          <SettingRow type="select" label="Nozzle Type" value={settings.nozzle_type ?? 'brass'} options={nozzleTypeOptions} onChange={(v) => onUpdate('nozzle_type', v as OrcaMachineSettings['nozzle_type'])} disabled={disabled} />
          <SettingRow type="number" label="Nozzle Volume" value={settings.nozzle_volume ?? 0} min={0} max={50} step={0.5} unit="mm3" onChange={(v) => onUpdate('nozzle_volume', v)} disabled={disabled} />
        </div>
      </div>
      <div className="py-1">
        <SectionHeader title="Layer Height Limits" />
        <div className="grid grid-cols-2 gap-4">
          <SettingRow type="number" label="Min Layer Height" value={settings.min_layer_height ?? 0.08} min={0.01} max={0.5} step={0.01} unit="mm" onChange={(v) => onUpdate('min_layer_height', v)} disabled={disabled} />
          <SettingRow type="number" label="Max Layer Height" value={settings.max_layer_height ?? 0.28} min={0.05} max={2.0} step={0.01} unit="mm" onChange={(v) => onUpdate('max_layer_height', v)} disabled={disabled} />
        </div>
        <SettingRow type="number" label="Extruder Printable Height" value={settings.extruder_printable_height ?? 0} min={0} max={1000} step={1} unit="mm" onChange={(v) => onUpdate('extruder_printable_height', v)} disabled={disabled} description="Per-extruder height limit; 0 = use machine height" />
      </div>
      <div className="py-1">
        <SectionHeader title="Retraction" />
        <div className="space-y-3">
          <SettingRow type="slider" label="Retraction Length" value={settings.retraction_length ?? 0.8} min={0} max={10} step={0.1} unit="mm" onChange={(v) => onUpdate('retraction_length', v)} disabled={disabled} />
          <SettingRow type="slider" label="Retraction Speed" value={settings.retraction_speed ?? 35} min={1} max={150} step={1} unit="mm/s" onChange={(v) => onUpdate('retraction_speed', v)} disabled={disabled} />
          <SettingRow type="slider" label="Deretraction Speed" value={settings.deretraction_speed ?? 25} min={1} max={150} step={1} unit="mm/s" onChange={(v) => onUpdate('deretraction_speed', v)} disabled={disabled} />
          <SettingRow type="number" label="Retract Restart Extra" value={settings.retract_restart_extra ?? 0} min={-2} max={2} step={0.01} unit="mm" onChange={(v) => onUpdate('retract_restart_extra', v)} disabled={disabled} />
          <SettingRow type="number" label="Retraction Min Travel" value={settings.retraction_minimum_travel ?? 2} min={0} max={20} step={0.5} unit="mm" onChange={(v) => onUpdate('retraction_minimum_travel', v)} disabled={disabled} />
          <SettingRow type="checkbox" label="Retract When Changing Layer" checked={settings.retract_when_changing_layer ?? false} onChange={(v) => onUpdate('retract_when_changing_layer', v)} disabled={disabled} />
          <div className="grid grid-cols-2 gap-4">
            <SettingRow type="number" label="Length (Toolchange)" value={settings.retract_length_toolchange ?? 10} min={0} max={30} step={0.5} unit="mm" onChange={(v) => onUpdate('retract_length_toolchange', v)} disabled={disabled} />
            <SettingRow type="number" label="Restart Extra (Toolchange)" value={settings.retract_restart_extra_toolchange ?? 0} min={0} max={5} step={0.1} unit="mm" onChange={(v) => onUpdate('retract_restart_extra_toolchange', v)} disabled={disabled} />
          </div>
        </div>
      </div>
      <div className="py-1">
        <SectionHeader title="Z-Hop" />
        <div className="space-y-3">
          <SettingRow type="slider" label="Z Hop Height" value={settings.z_hop ?? 0.2} min={0} max={5} step={0.05} unit="mm" onChange={(v) => onUpdate('z_hop', v)} disabled={disabled} />
          <SettingRow type="text" label="Z Hop Types" value={settings.z_hop_types ?? ''} onChange={(v) => onUpdate('z_hop_types', v)} disabled={disabled} />
          <SettingRow type="text" label="Retract Lift Enforce" value={settings.retract_lift_enforce ?? ''} onChange={(v) => onUpdate('retract_lift_enforce', v)} disabled={disabled} description="Enforce Z-lift on specified features" />
          <div className="grid grid-cols-2 gap-4">
            <SettingRow type="number" label="Lift Above Z" value={settings.retract_lift_above ?? 0} min={0} max={50} step={0.1} unit="mm" onChange={(v) => onUpdate('retract_lift_above', v)} disabled={disabled} />
            <SettingRow type="number" label="Lift Below Z" value={settings.retract_lift_below ?? 0} min={0} max={50} step={0.1} unit="mm" onChange={(v) => onUpdate('retract_lift_below', v)} disabled={disabled} />
          </div>
        </div>
      </div>
      <div className="py-1">
        <SectionHeader title="Wipe" />
        <div className="space-y-3">
          <SettingRow type="checkbox" label="Wipe on Retract" checked={settings.wipe ?? false} onChange={(v) => onUpdate('wipe', v)} disabled={disabled} />
          <div className="grid grid-cols-2 gap-4">
            <SettingRow type="number" label="Wipe Distance" value={settings.wipe_distance ?? 0} min={0} max={10} step={0.5} unit="mm" onChange={(v) => onUpdate('wipe_distance', v)} disabled={disabled} />
            <SettingRow type="number" label="Wipe Speed" value={settings.wipe_speed ?? 80} min={5} max={300} step={5} unit="mm/s" onChange={(v) => onUpdate('wipe_speed', v)} disabled={disabled} />
          </div>
          <SettingRow type="slider" label="Retract Before Wipe" value={settings.retract_before_wipe ?? 0} min={0} max={100} step={1} unit="%" onChange={(v) => onUpdate('retract_before_wipe', v)} disabled={disabled} />
          <SettingRow type="checkbox" label="Wipe Before External Loop" checked={settings.wipe_before_external_loop ?? false} onChange={(v) => onUpdate('wipe_before_external_loop', v)} disabled={disabled} />
          <SettingRow type="checkbox" label="Wipe On Loops" checked={settings.wipe_on_loops ?? false} onChange={(v) => onUpdate('wipe_on_loops', v)} disabled={disabled} />
          <SettingRow type="checkbox" label="Travel Slope" checked={settings.travel_slope ?? false} onChange={(v) => onUpdate('travel_slope', v)} disabled={disabled} />
        </div>
      </div>
      <div className="py-1">
        <SectionHeader title="Retraction when switching material" />
        <div className="space-y-3">
          <SettingRow type="number" label="Retraction Length" value={settings.retract_length_toolchange ?? 10} min={0} max={100} step={0.5} unit="mm" onChange={(v) => onUpdate('retract_length_toolchange', v)} disabled={disabled} />
          <SettingRow type="number" label="Extra Length on Restart" value={settings.retract_restart_extra_toolchange ?? 0} min={-5} max={10} step={0.1} unit="mm" onChange={(v) => onUpdate('retract_restart_extra_toolchange', v)} disabled={disabled} />
          <SettingRow type="number" label="Long Retraction When Cut" value={settings.long_retractions_when_cut ?? 0} min={0} max={50} step={0.5} unit="mm" onChange={(v) => onUpdate('long_retractions_when_cut', v)} disabled={disabled} />
          <SettingRow type="text" label="Retraction Distances When Cut" value={settings.retraction_distances_when_cut ?? ''} onChange={(v) => onUpdate('retraction_distances_when_cut', v)} disabled={disabled} />
        </div>
      </div>
    </>
  );
};

// -- Tab 5: Motion Ability ------------------------------------------------

const MotionAbilityTab: React.FC<TabPanelProps> = ({ settings, onUpdate, disabled }) => {
  const motionTypeOptions = Object.entries(MOTION_TYPE_LABELS).map(([value, label]) => ({ value, label }));
  return (
    <>
      <div className="py-1">
        <SectionHeader title="Advanced" />
        <div className="space-y-3">
          <SettingRow type="checkbox" label="Emit Machine Limits to G-code" checked={settings.emit_machine_limits_to_gcode ?? true} onChange={(v) => onUpdate('emit_machine_limits_to_gcode', v)} disabled={disabled} />
          <SettingRow type="select" label="Motion Type" value={settings.motion_type ?? 'cartesian'} options={motionTypeOptions} onChange={(v) => onUpdate('motion_type', v as OrcaMachineSettings['motion_type'])} disabled={disabled} />
        </div>
      </div>
      <div className="py-1">
        <SectionHeader icon={<SpeedIcon className="w-5 h-5" />} title="Speed limitation" />
        <div className="space-y-3">
          <SettingRow type="slider" label="Max Print Speed" value={settings.max_print_speed ?? 250} min={10} max={1000} step={10} unit="mm/s" onChange={(v) => onUpdate('max_print_speed', v)} disabled={disabled} />
          <div className="grid grid-cols-2 gap-4">
            <SettingRow type="number" label="Max Speed X" value={settings.machine_max_speed_x ?? 300} min={10} max={1000} step={10} unit="mm/s" onChange={(v) => onUpdate('machine_max_speed_x', v)} disabled={disabled} />
            <SettingRow type="number" label="Max Speed Y" value={settings.machine_max_speed_y ?? 300} min={10} max={1000} step={10} unit="mm/s" onChange={(v) => onUpdate('machine_max_speed_y', v)} disabled={disabled} />
            <SettingRow type="number" label="Max Speed Z" value={settings.machine_max_speed_z ?? 10} min={1} max={100} step={1} unit="mm/s" onChange={(v) => onUpdate('machine_max_speed_z', v)} disabled={disabled} />
            <SettingRow type="number" label="Max Speed E" value={settings.machine_max_speed_e ?? 120} min={1} max={500} step={5} unit="mm/s" onChange={(v) => onUpdate('machine_max_speed_e', v)} disabled={disabled} />
          </div>
        </div>
      </div>
      <div className="py-1">
        <SectionHeader icon={<AccelerationIcon className="w-5 h-5" />} title="Acceleration limitation" />
        <div className="grid grid-cols-2 gap-4">
          <SettingRow type="number" label="Accel X" value={settings.machine_max_acceleration_x ?? 3000} min={100} max={50000} step={100} unit="mm/s2" onChange={(v) => onUpdate('machine_max_acceleration_x', v)} disabled={disabled} />
          <SettingRow type="number" label="Accel Y" value={settings.machine_max_acceleration_y ?? 3000} min={100} max={50000} step={100} unit="mm/s2" onChange={(v) => onUpdate('machine_max_acceleration_y', v)} disabled={disabled} />
          <SettingRow type="number" label="Accel Z" value={settings.machine_max_acceleration_z ?? 500} min={10} max={5000} step={50} unit="mm/s2" onChange={(v) => onUpdate('machine_max_acceleration_z', v)} disabled={disabled} />
          <SettingRow type="number" label="Accel E" value={settings.machine_max_acceleration_e ?? 5000} min={100} max={50000} step={100} unit="mm/s2" onChange={(v) => onUpdate('machine_max_acceleration_e', v)} disabled={disabled} />
        </div>
        <div className="mt-3">
          <SettingRow type="number" label="Accel Travel" value={settings.machine_max_acceleration_travel ?? 5000} min={100} max={50000} step={100} unit="mm/s²" onChange={(v) => onUpdate('machine_max_acceleration_travel', v)} disabled={disabled} />
          <SettingRow type="number" label="Accel Extruding" value={settings.machine_max_acceleration_extruding ?? 5000} min={100} max={50000} step={100} unit="mm/s²" onChange={(v) => onUpdate('machine_max_acceleration_extruding', v)} disabled={disabled} />
          <SettingRow type="number" label="Accel Retracting" value={settings.machine_max_acceleration_retracting ?? 5000} min={100} max={50000} step={100} unit="mm/s²" onChange={(v) => onUpdate('machine_max_acceleration_retracting', v)} disabled={disabled} />
        </div>
      </div>
      <div className="py-1">
        <SectionHeader title="Jerk limitation" />
        <div className="grid grid-cols-2 gap-4">
          <SettingRow type="number" label="Jerk X" value={settings.machine_max_jerk_x ?? 8} min={0} max={50} step={0.5} unit="mm/s" onChange={(v) => onUpdate('machine_max_jerk_x', v)} disabled={disabled} />
          <SettingRow type="number" label="Jerk Y" value={settings.machine_max_jerk_y ?? 8} min={0} max={50} step={0.5} unit="mm/s" onChange={(v) => onUpdate('machine_max_jerk_y', v)} disabled={disabled} />
          <SettingRow type="number" label="Jerk Z" value={settings.machine_max_jerk_z ?? 0.4} min={0} max={5} step={0.1} unit="mm/s" onChange={(v) => onUpdate('machine_max_jerk_z', v)} disabled={disabled} />
          <SettingRow type="number" label="Jerk E" value={settings.machine_max_jerk_e ?? 2.5} min={0} max={20} step={0.5} unit="mm/s" onChange={(v) => onUpdate('machine_max_jerk_e', v)} disabled={disabled} />
        </div>
        <div className="mt-3">
          <SettingRow type="number" label="Max Junction Deviation" value={settings.max_junction_deviation ?? 0.013} min={0} max={1} step={0.001} unit="mm" onChange={(v) => onUpdate('max_junction_deviation', v)} disabled={disabled} description="Alternative to jerk" />
        </div>
      </div>
      <div className="py-1">
        <SectionHeader title="Travel" />
        <div className="space-y-3">
          <div className="grid grid-cols-2 gap-4">
            <SettingRow type="number" label="Travel Speed" value={settings.travel_speed ?? 200} min={50} max={1000} step={10} unit="mm/s" onChange={(v) => onUpdate('travel_speed', v)} disabled={disabled} />
            <SettingRow type="number" label="Travel Acceleration" value={settings.travel_acceleration ?? 5000} min={100} max={50000} step={100} unit="mm/s2" onChange={(v) => onUpdate('travel_acceleration', v)} disabled={disabled} />
          </div>
          <SettingRow type="number" label="Travel Jerk" value={settings.travel_jerk ?? 8} min={0} max={30} step={0.5} unit="mm/s" onChange={(v) => onUpdate('travel_jerk', v)} disabled={disabled} />
        </div>
      </div>
      <div className="py-1">
        <SectionHeader title="Resonance Avoidance" />
        <div className="space-y-3">
        <SettingRow type="checkbox" label="Resonance Avoidance" checked={settings.resonance_avoidance ?? false} onChange={(v) => onUpdate('resonance_avoidance', v)} disabled={disabled} />
        {settings.resonance_avoidance && (
          <div className="grid grid-cols-2 gap-4">
            <SettingRow type="number" label="Min Speed" value={settings.min_resonance_avoidance_speed ?? 0} min={0} max={500} step={5} unit="mm/s" onChange={(v) => onUpdate('min_resonance_avoidance_speed', v)} disabled={disabled} />
            <SettingRow type="number" label="Max Speed" value={settings.max_resonance_avoidance_speed ?? 300} min={0} max={1000} step={10} unit="mm/s" onChange={(v) => onUpdate('max_resonance_avoidance_speed', v)} disabled={disabled} />
          </div>
        )}
        <SettingRow type="checkbox" label="Arc Movement (G2/G3)" checked={settings.support_arc_movement ?? false} onChange={(v) => onUpdate('support_arc_movement', v)} disabled={disabled} />
        {settings.support_arc_movement && <SettingRow type="number" label="Arc Resolution" value={settings.arc_resolution ?? 0.1} min={0.01} max={1} step={0.01} unit="mm" onChange={(v) => onUpdate('arc_resolution', v)} disabled={disabled} />}
        </div>
      </div>
    </>
  );
};

// -- Tab 6: Notes ---------------------------------------------------------

const NotesTab: React.FC<TabPanelProps> = ({ settings, onUpdate, disabled }) => (
  <div className="py-1">
    <FormField label="Printer Notes" htmlFor="printer_notes">
      <Textarea
        id="printer_notes"
        value={settings.printer_notes ?? ''}
        onChange={(e) => onUpdate('printer_notes', e.target.value)}
        rows={16}
        disabled={disabled}
        placeholder="Free-form notes about this printer profile..."
        className="text-sm"
      />
    </FormField>
  </div>
);

export default MachineProfileEditor;
