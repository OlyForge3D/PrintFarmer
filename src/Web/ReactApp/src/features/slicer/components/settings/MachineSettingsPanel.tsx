/**
 * OrcaSlicer-style Machine Settings Panel
 * Displays machine/printer profile configuration with Basic | Advanced view modes
 */
import React, { useState, useCallback } from 'react';
import { Button } from '@/common/components/ui';
import { CompactSettingRow, SettingSection } from './SettingRow';
import {
  WallCountIcon,
  SpeedIcon,
  TemperatureIcon,
  PrecisionIcon,
} from './SlicerSettingIcons';
import type {
  MachineSettingsViewMode,
  MachineCategory,
  OrcaMachineSettings,
} from './machineSettingsTypes';

interface MachineSettingsPanelProps {
  /** Current machine settings values */
  settings: Partial<OrcaMachineSettings>;
  /** Called when any setting changes */
  onChange: (settings: Partial<OrcaMachineSettings>) => void;
  /** Initial view mode */
  initialViewMode?: MachineSettingsViewMode;
  /** Disable all controls */
  disabled?: boolean;
  /** Custom class name */
  className?: string;
  /** Optional function to check if a category has modified settings */
  isCategoryDirty?: (category: MachineCategory) => boolean;
}

export function MachineSettingsPanel({
  settings,
  onChange,
  initialViewMode = 'simple',
  disabled = false,
  className = '',
  isCategoryDirty,
}: MachineSettingsPanelProps) {
  const [viewMode, setViewMode] = useState<MachineSettingsViewMode>(initialViewMode);
  const [activeCategory, setActiveCategory] = useState<MachineCategory>('basic_information');

  const advancedSettings = settings;

  const onUpdate = useCallback(
    (key: string, value: unknown) => {
      const updated = { ...settings, [key]: value } as Partial<OrcaMachineSettings>;
      onChange(updated);
    },
    [settings, onChange]
  );

  // View mode buttons
  const viewModeButtons = [
    { mode: 'simple' as const, label: 'Simple' },
    { mode: 'advanced' as const, label: 'Advanced' },
  ];

  // Category tabs for advanced mode
  const categories: Array<{ id: MachineCategory; label: string }> = [
    { id: 'basic_information', label: 'General' },
    { id: 'extruder', label: 'Extruder' },
    { id: 'motion_ability', label: 'Capabilities' },
    { id: 'machine_gcode', label: 'G-code' },
  ];

  return (
    <div className={`space-y-4 ${className}`}>
      {/* View Mode Selector */}
      {viewModeButtons.length > 1 && (
        <div className="flex gap-2 px-4">
          {viewModeButtons.map((btn) => (
            <Button
              key={btn.mode}
              variant={viewMode === btn.mode ? 'primary' : 'secondary'}
              size="sm"
              onClick={() => setViewMode(btn.mode)}
              disabled={disabled}
            >
              {btn.label}
            </Button>
          ))}
        </div>
      )}

      {viewMode === 'simple' ? (
        // BASIC MODE - Essential machine parameters
        <div className="space-y-4 px-4">
          <SettingSection icon={<WallCountIcon className="w-4 h-4" />} title="Build volume">
            <div className="space-y-3">
              <CompactSettingRow
                type="number"
                label="X (mm)"
                value={settings.bed_size_x}
                onChange={(v) => onUpdate('bed_size_x', v)}
                min={50}
                max={1000}
                step={10}
                disabled={disabled}
              />
              <CompactSettingRow
                type="number"
                label="Y (mm)"
                value={settings.bed_size_y}
                onChange={(v) => onUpdate('bed_size_y', v)}
                min={50}
                max={1000}
                step={10}
                disabled={disabled}
              />
              <CompactSettingRow
                type="number"
                label="Z (mm)"
                value={settings.printable_height}
                onChange={(v) => onUpdate('printable_height', v)}
                min={50}
                max={1000}
                step={10}
                disabled={disabled}
              />
            </div>
          </SettingSection>

          <SettingSection icon={<PrecisionIcon className="w-4 h-4" />} title="Nozzle">
            <div className="space-y-3">
              <CompactSettingRow
                type="number"
                label="Diameter (mm)"
                value={settings.nozzle_diameter}
                onChange={(v) => onUpdate('nozzle_diameter', v)}
                min={0.2}
                max={1.0}
                step={0.2}
                disabled={disabled}
              />
            </div>
          </SettingSection>

          <SettingSection icon={<SpeedIcon className="w-4 h-4" />} title="Performance">
            <div className="space-y-3">
              <CompactSettingRow
                type="number"
                label="Max speed (mm/s)"
                value={settings.max_print_speed}
                onChange={(v) => onUpdate('max_print_speed', v)}
                min={10}
                max={500}
                step={10}
                disabled={disabled}
              />
            </div>
          </SettingSection>
        </div>
      ) : (
        // ADVANCED MODE - Full machine configuration with tabbed interface
        <>
          {/* Category Tabs */}
          <div className="border-b border-pf-border flex px-4 gap-1 overflow-x-auto">
            {categories.map((cat) => (
              // eslint-disable-next-line local/pf-no-raw-html-controls
              <button
                key={cat.id}
                onClick={() => setActiveCategory(cat.id)}
                className={`px-4 py-2 text-sm font-medium border-b-2 transition-colors ${
                  activeCategory === cat.id
                    ? 'border-pf-accent text-pf-accent'
                    : 'border-transparent text-pf-text-muted hover:text-pf-text'
                } ${disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer'}`}
                disabled={disabled}
                type="button"
              >
                {cat.label}
                {isCategoryDirty?.(cat.id) && <span className="ml-2 text-pf-accent">●</span>}
              </button>
            ))}
          </div>

          {/* Tab Content */}
          <div className="space-y-4 px-4">
            {activeCategory === 'basic_information' && (
              <>
                <SettingSection title="Printer identification">
                  <div className="space-y-3">
                    <CompactSettingRow
                      type="text"
                      label="Model"
                      value={advancedSettings.printer_model}
                      onChange={(v) => onUpdate('printer_model', v)}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="text"
                      label="Variant"
                      value={advancedSettings.printer_variant}
                      onChange={(v) => onUpdate('printer_variant', v)}
                      disabled={disabled}
                    />
                  </div>
                </SettingSection>

                <SettingSection icon={<WallCountIcon className="w-4 h-4" />} title="Build volume">
                  <div className="space-y-3">
                    <CompactSettingRow
                      type="number"
                      label="X (mm)"
                      value={advancedSettings.bed_size_x}
                      onChange={(v) => onUpdate('bed_size_x', v)}
                      min={50}
                      max={1000}
                      step={10}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="number"
                      label="Y (mm)"
                      value={advancedSettings.bed_size_y}
                      onChange={(v) => onUpdate('bed_size_y', v)}
                      min={50}
                      max={1000}
                      step={10}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="number"
                      label="Z (mm)"
                      value={advancedSettings.printable_height}
                      onChange={(v) => onUpdate('printable_height', v)}
                      min={50}
                      max={1000}
                      step={10}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="select"
                      label="Shape"
                      value={advancedSettings.bed_shape}
                      onChange={(v) => onUpdate('bed_shape', v)}
                      options={[
                        { value: 'rectangular', label: 'Rectangular' },
                        { value: 'circular', label: 'Circular' },
                      ]}
                      disabled={disabled}
                    />
                  </div>
                </SettingSection>

                <SettingSection title="Print bed">
                  <div className="space-y-3">
                    <CompactSettingRow
                      type="select"
                      label="Surface type"
                      value={advancedSettings.bed_type}
                      onChange={(v) => onUpdate('bed_type', v)}
                      options={[
                        { value: 'textured_pei', label: 'Textured PEI' },
                        { value: 'smooth_pei', label: 'Smooth PEI' },
                        { value: 'glass', label: 'Glass' },
                        { value: 'spring_steel', label: 'Spring Steel' },
                        { value: 'custom', label: 'Custom' },
                      ]}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="checkbox"
                      label="Heated bed"
                      checked={advancedSettings.has_heated_bed}
                      onChange={(v) => onUpdate('has_heated_bed', v)}
                      disabled={disabled}
                    />
                    {advancedSettings.has_heated_bed && (
                      <CompactSettingRow
                        type="number"
                        label="Max temperature (°C)"
                        value={advancedSettings.max_bed_temperature}
                        onChange={(v) => onUpdate('max_bed_temperature', v)}
                        min={0}
                        max={200}
                        step={10}
                        disabled={disabled}
                      />
                    )}
                    <CompactSettingRow
                      type="checkbox"
                      label="Bed leveling probe"
                      checked={advancedSettings.has_bed_probe}
                      onChange={(v) => onUpdate('has_bed_probe', v)}
                      disabled={disabled}
                    />
                    {advancedSettings.has_bed_probe && (
                      <CompactSettingRow
                        type="select"
                        label="Probe type"
                        value={advancedSettings.probe_type}
                        onChange={(v) => onUpdate('probe_type', v)}
                        options={[
                          { value: 'bltouch', label: 'BLTouch' },
                          { value: 'inductive', label: 'Inductive' },
                          { value: 'capacitive', label: 'Capacitive' },
                          { value: 'manual', label: 'Manual' },
                          { value: 'none', label: 'None' },
                        ]}
                        disabled={disabled}
                      />
                    )}
                  </div>
                </SettingSection>
              </>
            )}

            {activeCategory === 'extruder' && (
              <>
                <SettingSection title="Extruder configuration">
                  <div className="space-y-3">
                    <CompactSettingRow
                      type="number"
                      label="Extruder count"
                      value={advancedSettings.extruder_count}
                      onChange={(v) => onUpdate('extruder_count', v)}
                      min={1}
                      max={8}
                      step={1}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="text"
                      label="Extruder offset (mm)"
                      value={advancedSettings.extruder_offset}
                      onChange={(v) => onUpdate('extruder_offset', v)}
                      placeholder="e.g., 0x0,40x0"
                      disabled={disabled}
                    />
                  </div>
                </SettingSection>

                <SettingSection title="Retraction">
                  <div className="space-y-3">
                    <CompactSettingRow
                      type="number"
                      label="Retraction length (mm)"
                      value={advancedSettings.retraction_length}
                      onChange={(v) => onUpdate('retraction_length', v)}
                      min={0}
                      max={10}
                      step={0.5}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="number"
                      label="Retraction speed (mm/s)"
                      value={advancedSettings.retraction_speed}
                      onChange={(v) => onUpdate('retraction_speed', v)}
                      min={10}
                      max={100}
                      step={5}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="number"
                      label="Z hop (mm)"
                      value={advancedSettings.z_hop}
                      onChange={(v) => onUpdate('z_hop', v)}
                      min={0}
                      max={5}
                      step={0.1}
                      disabled={disabled}
                    />
                  </div>
                </SettingSection>

                <SettingSection title="Nozzle">
                  <div className="space-y-3">
                    <CompactSettingRow
                      type="number"
                      label="Diameter (mm)"
                      value={advancedSettings.nozzle_diameter}
                      onChange={(v) => onUpdate('nozzle_diameter', v)}
                      min={0.2}
                      max={1.0}
                      step={0.2}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="select"
                      label="Material"
                      value={advancedSettings.nozzle_type}
                      onChange={(v) => onUpdate('nozzle_type', v)}
                      options={[
                        { value: 'brass', label: 'Brass' },
                        { value: 'hardened_steel', label: 'Hardened Steel' },
                        { value: 'stainless_steel', label: 'Stainless Steel' },
                        { value: 'custom', label: 'Custom' },
                      ]}
                      disabled={disabled}
                    />
                  </div>
                </SettingSection>
              </>
            )}

            {activeCategory === 'motion_ability' && (
              <>
                <SettingSection icon={<TemperatureIcon className="w-4 h-4" />} title="Thermal">
                  <div className="space-y-3">
                    <CompactSettingRow
                      type="number"
                      label="Max hotend temp (°C)"
                      value={advancedSettings.max_hotend_temperature}
                      onChange={(v) => onUpdate('max_hotend_temperature', v)}
                      min={0}
                      max={500}
                      step={10}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="checkbox"
                      label="Heated chamber"
                      checked={advancedSettings.has_heated_chamber}
                      onChange={(v) => onUpdate('has_heated_chamber', v)}
                      disabled={disabled}
                    />
                    {advancedSettings.has_heated_chamber && (
                      <CompactSettingRow
                        type="number"
                        label="Max chamber temp (°C)"
                        value={advancedSettings.max_chamber_temperature}
                        onChange={(v) => onUpdate('max_chamber_temperature', v)}
                        min={0}
                        max={150}
                        step={5}
                        disabled={disabled}
                      />
                    )}
                  </div>
                </SettingSection>

                <SettingSection icon={<SpeedIcon className="w-4 h-4" />} title="Motion system">
                  <div className="space-y-3">
                    <CompactSettingRow
                      type="select"
                      label="Type"
                      value={advancedSettings.motion_type}
                      onChange={(v) => onUpdate('motion_type', v)}
                      options={[
                        { value: 'cartesian', label: 'Cartesian' },
                        { value: 'corexy', label: 'CoreXY' },
                        { value: 'delta', label: 'Delta' },
                        { value: 'belt', label: 'Belt' },
                      ]}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="number"
                      label="Max speed (mm/s)"
                      value={advancedSettings.max_print_speed}
                      onChange={(v) => onUpdate('max_print_speed', v)}
                      min={10}
                      max={500}
                      step={10}
                      disabled={disabled}
                    />
                  </div>
                </SettingSection>

                <SettingSection title="Features">
                  <div className="space-y-3">
                    <CompactSettingRow
                      type="checkbox"
                      label="G2/G3 arc movement"
                      checked={advancedSettings.support_arc_movement}
                      onChange={(v) => onUpdate('support_arc_movement', v)}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="checkbox"
                      label="Multi-material support"
                      checked={advancedSettings.support_multi_material}
                      onChange={(v) => onUpdate('support_multi_material', v)}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="checkbox"
                      label="Filament sensor"
                      checked={advancedSettings.filament_sensor}
                      onChange={(v) => onUpdate('filament_sensor', v)}
                      disabled={disabled}
                    />
                  </div>
                </SettingSection>
              </>
            )}

            {activeCategory === 'machine_gcode' && (
              <>
                <SettingSection title="G-code generation">
                  <div className="space-y-3">
                    <CompactSettingRow
                      type="select"
                      label="Dialect"
                      value={advancedSettings.gcode_flavor}
                      onChange={(v) => onUpdate('gcode_flavor', v)}
                      options={[
                        { value: 'marlin', label: 'Marlin' },
                        { value: 'marlin2', label: 'Marlin 2.0' },
                        { value: 'klipper', label: 'Klipper' },
                        { value: 'reprap', label: 'RepRap' },
                        { value: 'smoothie', label: 'Smoothie' },
                      ]}
                      disabled={disabled}
                    />
                  </div>
                </SettingSection>

                <SettingSection title="Custom G-code">
                  <div className="space-y-3 text-xs text-pf-text-muted">
                    <p>Note: G-code snippets shown in inspector mode. Editing requires profile import/export.</p>
                  </div>
                </SettingSection>
              </>
            )}
          </div>
        </>
      )}
    </div>
  );
}
