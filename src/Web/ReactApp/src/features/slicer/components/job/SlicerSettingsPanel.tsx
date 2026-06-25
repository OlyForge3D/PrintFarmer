import React from 'react';
import { Button, Checkbox, Slider } from '@/common/components/ui';

export interface SlicerSettings {
  layerHeight: number;
  infillPercent: number;
  supportEnabled: boolean;
  bedAdhesionType: 'none' | 'brim' | 'raft' | 'skirt';
}

interface SlicerSettingsPanelProps {
  /** Current slicer settings */
  settings: SlicerSettings;
  /** Callback when settings change */
  onSettingsChange: (settings: SlicerSettings) => void;
  /**
   * When true, hides the raw layer-height slider (encoded in the selected
   * quality/process profile preset). Infill, supports, and bed adhesion remain
   * visible to match Prusa EasyPrint's Simple controls.
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

/**
 * Slicer settings panel for adjusting print parameters.
 * Provides controls for layer height, infill, supports, and bed adhesion.
 */
export const SlicerSettingsPanel: React.FC<SlicerSettingsPanelProps> = ({
  settings,
  onSettingsChange,
  simpleMode = false,
  className
}) => {
  const updateSetting = <K extends keyof SlicerSettings>(
    key: K,
    value: SlicerSettings[K]
  ) => {
    onSettingsChange({ ...settings, [key]: value });
  };

  return (
    <div className={`bg-pf-panel border border-pf-border rounded-lg p-4 space-y-4 ${className ?? ''}`}>
      <h3 className="text-sm font-semibold text-pf-text-primary">Print Settings</h3>
      
      {/* Layer Height — hidden in Simple mode (encoded in process profile) */}
      {!simpleMode && (
        <div>
          <label className="block text-sm text-pf-text-muted mb-1">
            Layer Height: {settings.layerHeight}mm
          </label>
          <Slider
            min={0.05}
            max={0.4}
            step={0.05}
            value={settings.layerHeight}
            onChange={(value) => updateSetting('layerHeight', value)}
            aria-label="Layer height"
          />
        </div>
      )}
      
      {/* Infill Percentage — shown in Simple (EasyPrint exposes infill) */}
      <div>
        <label className="block text-sm text-pf-text-muted mb-1">
          Infill: {settings.infillPercent}%
        </label>
        <Slider
          min={0}
          max={100}
          step={5}
          value={settings.infillPercent}
          onChange={(value) => updateSetting('infillPercent', value)}
          aria-label="Infill percentage"
        />
      </div>
      
      {/* Support Enabled */}
      <div className="flex items-center gap-2">
        <Checkbox
          id="support-enabled"
          checked={settings.supportEnabled}
          onChange={(e) => updateSetting('supportEnabled', e.target.checked)}
        />
        <label htmlFor="support-enabled" className="text-sm text-pf-text-primary">
          Enable Supports
        </label>
      </div>
      
      {/* Bed Adhesion Type */}
      <div>
        <label className="block text-sm text-pf-text-muted mb-1">
          Bed Adhesion
        </label>
        <div className="flex flex-wrap gap-2">
          {BED_ADHESION_OPTIONS.map(option => (
            <Button
              key={option.value}
              type="button"
              variant={settings.bedAdhesionType === option.value ? 'primary' : 'secondary'}
              size="sm"
              onClick={() => updateSetting('bedAdhesionType', option.value)}
            >
              {option.label}
            </Button>
          ))}
        </div>
      </div>
    </div>
  );
};

export default SlicerSettingsPanel;
