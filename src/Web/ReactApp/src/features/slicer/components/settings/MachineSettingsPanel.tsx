/**
 * OrcaSlicer-style Machine Settings Panel
 * Displays machine/printer profile configuration with Basic | Advanced view modes
 */
import React, { useState, useCallback } from 'react';
import { Button } from '@/common/components/ui';
import { SettingRow, CompactSettingRow, SettingSection } from './SettingRow';
import {
  WallCountIcon,
  SpeedIcon,
  TemperatureIcon,
  PrecisionIcon,
} from './SlicerSettingIcons';
import type {
  MachineSettingsViewMode,
  MachineSettingsCategory,
  BasicMachineSettings,
  AdvancedMachineSettings,
} from './machineSettingsTypes';

interface MachineSettingsPanelProps {
  /** Current machine settings values */
  settings: BasicMachineSettings | AdvancedMachineSettings;
  /** Called when any setting changes */
  onChange: (settings: BasicMachineSettings | AdvancedMachineSettings) => void;
  /** Initial view mode */
  initialViewMode?: MachineSettingsViewMode;
  /** Disable all controls */
  disabled?: boolean;
  /** Custom class name */
  className?: string;
  /** Optional function to check if a category has modified settings */
  isCategoryDirty?: (category: MachineSettingsCategory) => boolean;
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
  const [activeCategory, setActiveCategory] = useState<MachineSettingsCategory>('general');

  const isAdvanced = (settings as AdvancedMachineSettings).printerModel !== undefined;
  const advancedSettings = settings as AdvancedMachineSettings;

  const onUpdate = useCallback(
    (key: string, value: unknown) => {
      const updated = { ...settings, [key]: value } as BasicMachineSettings | AdvancedMachineSettings;
      onChange(updated);
    },
    [settings, onChange]
  );

  // View mode buttons
  const viewModeButtons = [
    { mode: 'simple' as const, label: 'Simple' },
    ...(isAdvanced ? [{ mode: 'advanced' as const, label: 'Advanced' }] : []),
  ];

  // Category tabs for advanced mode - properly typed as MachineSettingsCategory
  const categories: Array<{ id: MachineSettingsCategory; label: string }> = [
    { id: 'general' as const, label: 'General' },
    ...(isAdvanced ? [
      { id: 'extruder' as const, label: 'Extruder' },
      { id: 'printbed' as const, label: 'Print bed' },
      { id: 'capabilities' as const, label: 'Capabilities' },
      { id: 'gcode' as const, label: 'G-code' },
    ] : []),
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
                value={settings.buildVolumeX}
                onChange={(v) => onUpdate('buildVolumeX', v)}
                min={50}
                max={1000}
                step={10}
                disabled={disabled}
              />
              <CompactSettingRow
                type="number"
                label="Y (mm)"
                value={settings.buildVolumeY}
                onChange={(v) => onUpdate('buildVolumeY', v)}
                min={50}
                max={1000}
                step={10}
                disabled={disabled}
              />
              <CompactSettingRow
                type="number"
                label="Z (mm)"
                value={settings.buildVolumeZ}
                onChange={(v) => onUpdate('buildVolumeZ', v)}
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
                value={settings.nozzleDiameter}
                onChange={(v) => onUpdate('nozzleDiameter', v)}
                min={0.2}
                max={1.0}
                step={0.2}
                unit="mm"
                disabled={disabled}
              />
            </div>
          </SettingSection>

          <SettingSection icon={<SpeedIcon className="w-4 h-4" />} title="Performance">
            <div className="space-y-3">
              <CompactSettingRow
                type="number"
                label="Max print speed (mm/s)"
                value={settings.maxPrintSpeed}
                onChange={(v) => onUpdate('maxPrintSpeed', v)}
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
            {activeCategory === 'general' && (
              <>
                <SettingSection title="Printer identification">
                  <div className="space-y-3">
                    <SettingRow
                      type="text"
                      label="Machine name"
                      value={advancedSettings.name}
                      onChange={(v) => onUpdate('name', v)}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="text"
                      label="Model"
                      value={advancedSettings.printerModel}
                      onChange={(v) => onUpdate('printerModel', v)}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="text"
                      label="Variant"
                      value={advancedSettings.printerVariant}
                      onChange={(v) => onUpdate('printerVariant', v)}
                      disabled={disabled}
                    />
                  </div>
                </SettingSection>

                <SettingSection icon={<WallCountIcon className="w-4 h-4" />} title="Build volume">
                  <div className="space-y-3">
                    <CompactSettingRow
                      type="number"
                      label="X (mm)"
                      value={advancedSettings.buildVolumeX}
                      onChange={(v) => onUpdate('buildVolumeX', v)}
                      min={50}
                      max={1000}
                      step={10}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="number"
                      label="Y (mm)"
                      value={advancedSettings.buildVolumeY}
                      onChange={(v) => onUpdate('buildVolumeY', v)}
                      min={50}
                      max={1000}
                      step={10}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="number"
                      label="Z (mm)"
                      value={advancedSettings.buildVolumeZ}
                      onChange={(v) => onUpdate('buildVolumeZ', v)}
                      min={50}
                      max={1000}
                      step={10}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="select"
                      label="Shape"
                      value={advancedSettings.bedShape}
                      onChange={(v) => onUpdate('bedShape', v)}
                      options={[
                        { value: 'rectangular', label: 'Rectangular' },
                        { value: 'circular', label: 'Circular' },
                      ]}
                      disabled={disabled}
                    />
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
                      value={advancedSettings.extruderCount}
                      onChange={(v) => onUpdate('extruderCount', v)}
                      min={1}
                      max={8}
                      step={1}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="text"
                      label="Extruder offset (mm)"
                      value={advancedSettings.extruderOffset}
                      onChange={(v) => onUpdate('extruderOffset', v)}
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
                      value={advancedSettings.retractionLength}
                      onChange={(v) => onUpdate('retractionLength', v)}
                      min={0}
                      max={10}
                      step={0.5}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="number"
                      label="Retraction speed (mm/s)"
                      value={advancedSettings.retractionSpeed}
                      onChange={(v) => onUpdate('retractionSpeed', v)}
                      min={10}
                      max={100}
                      step={5}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="number"
                      label="Z hop (mm)"
                      value={advancedSettings.retractionLiftZ}
                      onChange={(v) => onUpdate('retractionLiftZ', v)}
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
                      value={advancedSettings.nozzleDiameter}
                      onChange={(v) => onUpdate('nozzleDiameter', v)}
                      min={0.2}
                      max={1.0}
                      step={0.2}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="select"
                      label="Material"
                      value={advancedSettings.nozzleType}
                      onChange={(v) => onUpdate('nozzleType', v)}
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

            {activeCategory === 'printbed' && (
              <>
                <SettingSection title="Print bed">
                  <div className="space-y-3">
                    <CompactSettingRow
                      type="select"
                      label="Surface type"
                      value={advancedSettings.bedType}
                      onChange={(v) => onUpdate('bedType', v)}
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
                      checked={advancedSettings.hasHeatedBed}
                      onChange={(v) => onUpdate('hasHeatedBed', v)}
                      disabled={disabled}
                    />
                    {advancedSettings.hasHeatedBed && (
                      <CompactSettingRow
                        type="number"
                        label="Max temperature (°C)"
                        value={advancedSettings.maxBedTemperature}
                        onChange={(v) => onUpdate('maxBedTemperature', v)}
                        min={0}
                        max={200}
                        step={10}
                        disabled={disabled}
                      />
                    )}
                    <CompactSettingRow
                      type="checkbox"
                      label="Bed leveling probe"
                      checked={advancedSettings.hasBedProbe}
                      onChange={(v) => onUpdate('hasBedProbe', v)}
                      disabled={disabled}
                    />
                    {advancedSettings.hasBedProbe && (
                      <CompactSettingRow
                        type="select"
                        label="Probe type"
                        value={advancedSettings.probeType}
                        onChange={(v) => onUpdate('probeType', v)}
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

            {activeCategory === 'capabilities' && (
              <>
                <SettingSection icon={<TemperatureIcon className="w-4 h-4" />} title="Thermal">
                  <div className="space-y-3">
                    <CompactSettingRow
                      type="number"
                      label="Max hotend temp (°C)"
                      value={advancedSettings.maxHotendTemperature}
                      onChange={(v) => onUpdate('maxHotendTemperature', v)}
                      min={0}
                      max={500}
                      step={10}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="checkbox"
                      label="Heated chamber"
                      checked={advancedSettings.hasHeatedChamber}
                      onChange={(v) => onUpdate('hasHeatedChamber', v)}
                      disabled={disabled}
                    />
                    {advancedSettings.hasHeatedChamber && (
                      <CompactSettingRow
                        type="number"
                        label="Max chamber temp (°C)"
                        value={advancedSettings.maxChamberTemperature}
                        onChange={(v) => onUpdate('maxChamberTemperature', v)}
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
                      value={advancedSettings.motionType}
                      onChange={(v) => onUpdate('motionType', v)}
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
                      value={advancedSettings.maxPrintSpeed}
                      onChange={(v) => onUpdate('maxPrintSpeed', v)}
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
                      checked={advancedSettings.supportArcMovement}
                      onChange={(v) => onUpdate('supportArcMovement', v)}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="checkbox"
                      label="Multi-material support"
                      checked={advancedSettings.supportMultiMaterial}
                      onChange={(v) => onUpdate('supportMultiMaterial', v)}
                      disabled={disabled}
                    />
                    <CompactSettingRow
                      type="checkbox"
                      label="Filament sensor"
                      checked={advancedSettings.filamentSensor}
                      onChange={(v) => onUpdate('filamentSensor', v)}
                      disabled={disabled}
                    />
                  </div>
                </SettingSection>
              </>
            )}

            {activeCategory === 'gcode' && (
              <>
                <SettingSection title="G-code generation">
                  <div className="space-y-3">
                    <CompactSettingRow
                      type="select"
                      label="Dialect"
                      value={advancedSettings.gcodeDialect}
                      onChange={(v) => onUpdate('gcodeDialect', v)}
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
