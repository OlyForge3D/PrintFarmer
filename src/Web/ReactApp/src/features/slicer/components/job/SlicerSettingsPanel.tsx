import React, { useId } from 'react';
import { Checkbox, Select } from '@/common/components/ui';
import { INFILL_PATTERNS } from '@/features/slicer/components/settings/metadataTypes';

export interface SlicerSettings {
  layerHeight: number;
  infillPercent: number;
  infillPattern: string;
  topShellLayers: number;
  bottomShellLayers: number;
  wallLoops: number;
  supportEnabled: boolean;
  supportType: string;
  bedAdhesionType: 'none' | 'brim' | 'raft' | 'skirt';
}

interface SlicerSettingsPanelProps {
  /** Current slicer settings */
  settings: SlicerSettings;
  /** Callback when settings change */
  onSettingsChange: (settings: SlicerSettings) => void;
  /**
   * When true, hides the raw layer-height slider (encoded in the selected
   * quality/process profile preset). All other fields remain visible in
   * Simple mode.
   */
  simpleMode?: boolean;
  /** Optional CSS class name */
  className?: string;
}

const BED_ADHESION_OPTIONS: { value: SlicerSettings['bedAdhesionType']; label: string }[] = [
  { value: 'none', label: 'None' },
  { value: 'skirt', label: 'Skirt' },
  { value: 'brim', label: 'Brim' },
  { value: 'raft', label: 'Raft' },
];

const SUPPORT_TYPE_OPTIONS = [
  { value: 'normal(auto)', label: 'Normal (auto)' },
  { value: 'tree(auto)', label: 'Tree (auto)' },
  { value: 'normal(manual)', label: 'Normal (manual)' },
  { value: 'tree(manual)', label: 'Tree (manual)' },
];

const DEFAULT_INFILL_PATTERN = 'grid';

/**
 * Slicer settings panel for Simple mode.
 * Exposes: infill %, infill pattern, top/bottom layers, perimeters (wall loops),
 * support toggle + type, and bed adhesion via radio buttons.
 */
export const SlicerSettingsPanel: React.FC<SlicerSettingsPanelProps> = ({
  settings,
  onSettingsChange,
  simpleMode = false,
  className,
}) => {
  const bedAdhesionGroupName = useId();
  const selectedPattern = INFILL_PATTERNS.find(
    (p) => p.value === (settings.infillPattern || DEFAULT_INFILL_PATTERN)
  );
  const updateSetting = <K extends keyof SlicerSettings>(key: K, value: SlicerSettings[K]) => {
    onSettingsChange({ ...settings, [key]: value });
  };

  return (
    <div className={`bg-pf-panel border border-pf-border rounded-lg p-4 space-y-4 ${className ?? ''}`}>
      <h3 className="text-sm font-semibold text-pf-text-primary">Print Settings</h3>

      {/* Layer Height — hidden in Simple mode (encoded in process profile preset) */}
      {!simpleMode && (
        <div className="space-y-1">
          <span className="block text-xs font-medium text-pf-text-muted uppercase tracking-wide">
            Layer Height
          </span>
          <div className="flex items-center gap-2">
            <input
              type="number"
              className="w-20 px-2 py-1 text-sm bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary"
              min={0.05}
              max={0.4}
              step={0.05}
              value={settings.layerHeight}
              onChange={(e) => updateSetting('layerHeight', Number(e.target.value))}
              aria-label="Layer height in mm"
            />
            <span className="text-xs text-pf-text-muted">mm</span>
          </div>
        </div>
      )}

      {/* ── Strength section ── */}
      <div className="grid grid-cols-3 gap-3">
        {/* Wall Loops (perimeters) */}
        <div className="space-y-1">
          <label htmlFor="simple-wall-loops" className="block text-xs font-medium text-pf-text-muted uppercase tracking-wide">
            Perimeters
          </label>
          <input
            id="simple-wall-loops"
            type="number"
            className="w-full px-2 py-1 text-sm bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary"
            min={1}
            max={20}
            step={1}
            value={settings.wallLoops}
            onChange={(e) => updateSetting('wallLoops', Math.max(1, Math.round(Number(e.target.value))))}
            aria-label="Number of perimeters (wall loops)"
          />
        </div>

        {/* Top Layers */}
        <div className="space-y-1">
          <label htmlFor="simple-top-layers" className="block text-xs font-medium text-pf-text-muted uppercase tracking-wide">
            Top Layers
          </label>
          <input
            id="simple-top-layers"
            type="number"
            className="w-full px-2 py-1 text-sm bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary"
            min={0}
            max={30}
            step={1}
            value={settings.topShellLayers}
            onChange={(e) => updateSetting('topShellLayers', Math.max(0, Math.round(Number(e.target.value))))}
            aria-label="Number of top solid layers"
          />
        </div>

        {/* Bottom Layers */}
        <div className="space-y-1">
          <label htmlFor="simple-bottom-layers" className="block text-xs font-medium text-pf-text-muted uppercase tracking-wide">
            Bottom Layers
          </label>
          <input
            id="simple-bottom-layers"
            type="number"
            className="w-full px-2 py-1 text-sm bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary"
            min={0}
            max={30}
            step={1}
            value={settings.bottomShellLayers}
            onChange={(e) => updateSetting('bottomShellLayers', Math.max(0, Math.round(Number(e.target.value))))}
            aria-label="Number of bottom solid layers"
          />
        </div>
      </div>

      {/* ── Infill ── */}
      <div className="space-y-3">
        <span className="block text-xs font-medium text-pf-text-muted uppercase tracking-wide">
          Infill
        </span>

        {/* Infill % */}
        <div className="flex items-center gap-3">
          <input
            id="simple-infill-pct"
            type="range"
            className="flex-1 accent-pf-accent"
            min={0}
            max={100}
            step={5}
            value={settings.infillPercent}
            onChange={(e) => updateSetting('infillPercent', Number(e.target.value))}
            aria-label="Infill percentage"
          />
          <div className="flex items-center gap-1">
            <input
              type="number"
              className="w-14 px-2 py-1 text-sm bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary text-right"
              min={0}
              max={100}
              step={5}
              value={settings.infillPercent}
              onChange={(e) => updateSetting('infillPercent', Math.min(100, Math.max(0, Number(e.target.value))))}
              aria-label="Infill percentage value"
            />
            <span className="text-xs text-pf-text-muted">%</span>
          </div>
        </div>

        {/* Infill Pattern dropdown with OrcaSlicer icons */}
        <div>
          <label htmlFor="simple-infill-pattern" className="block text-xs text-pf-text-muted mb-1">
            Pattern
          </label>
          <select
            id="simple-infill-pattern"
            className="w-full px-2 py-1.5 text-sm bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary"
            value={settings.infillPattern || DEFAULT_INFILL_PATTERN}
            onChange={(e) => updateSetting('infillPattern', e.target.value)}
            aria-label="Infill pattern"
          >
            {INFILL_PATTERNS.map((p) => (
              <option key={p.value} value={p.value}>
                {p.label}
              </option>
            ))}
          </select>
          {/* Icon + label preview of the selected pattern */}
          {selectedPattern && (
            <div className="mt-1.5 flex items-center gap-2 px-2 py-1 bg-pf-bg-2 rounded border border-pf-border">
              <img
                src={`/icons/orca/param_${selectedPattern.value}.svg`}
                width={18}
                height={18}
                alt=""
                aria-hidden="true"
                className="shrink-0"
              />
              <span className="text-xs text-pf-text-secondary">{selectedPattern.label}</span>
            </div>
          )}
        </div>
      </div>

      {/* ── Supports ── */}
      <div className="space-y-2">
        <div className="flex items-center gap-2">
          <Checkbox
            id="simple-support-enabled"
            checked={settings.supportEnabled}
            onChange={(e) => updateSetting('supportEnabled', e.target.checked)}
          />
          <label htmlFor="simple-support-enabled" className="text-sm text-pf-text-primary cursor-pointer">
            Enable Supports
          </label>
        </div>

        {settings.supportEnabled && (
          <div className="pl-6">
            <label htmlFor="simple-support-type" className="block text-xs text-pf-text-muted mb-1">
              Support Type
            </label>
            <Select
              id="simple-support-type"
              value={settings.supportType}
              onChange={(e) => updateSetting('supportType', e.target.value)}
              aria-label="Support type"
            >
              {SUPPORT_TYPE_OPTIONS.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
            </Select>
          </div>
        )}
      </div>

      {/* ── Bed Adhesion — radio buttons ── */}
      <div className="space-y-2">
        <span className="block text-xs font-medium text-pf-text-muted uppercase tracking-wide">
          Bed Adhesion
        </span>
        <div className="flex flex-wrap gap-x-4 gap-y-1" role="radiogroup" aria-label="Bed adhesion type">
          {BED_ADHESION_OPTIONS.map((opt) => (
            <label key={opt.value} className="flex items-center gap-1.5 cursor-pointer text-sm text-pf-text-primary">
              <input
                type="radio"
                name={bedAdhesionGroupName}
                value={opt.value}
                checked={settings.bedAdhesionType === opt.value}
                onChange={() => updateSetting('bedAdhesionType', opt.value)}
                className="accent-pf-accent"
              />
              {opt.label}
            </label>
          ))}
        </div>
      </div>
    </div>
  );
};

export default SlicerSettingsPanel;
