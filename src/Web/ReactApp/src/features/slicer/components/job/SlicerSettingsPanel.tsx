import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Checkbox, Select } from '@/common/components/ui';
import { INFILL_PATTERNS } from '@/features/slicer/components/settings/metadataTypes';
import { ChevronDownIcon } from '@/common/components/icons/MdiIcons';

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
  { value: 'normal(auto)', label: 'Normal' },
  { value: 'tree(auto)', label: 'Tree' },
];

const DEFAULT_INFILL_PATTERN = 'grid';

function PatternIcon({ value, className }: { value: string; className?: string }) {
  return (
    <img
      src={`/icons/orca/param_${value}.svg`}
      width={18}
      height={18}
      alt=""
      aria-hidden="true"
      className={className}
    />
  );
}

function InfillPatternDropdown({
  value,
  onChange,
  ariaLabelledBy,
  ariaDescribedBy,
}: {
  value: string;
  onChange: (pattern: string) => void;
  ariaLabelledBy?: string;
  ariaDescribedBy?: string;
}) {
  const dropdownId = React.useId();
  const selectedValueId = `${dropdownId}-value`;
  const listboxId = `${dropdownId}-listbox`;
  const triggerRef = useRef<HTMLButtonElement>(null);
  const rootRef = useRef<HTMLDivElement>(null);
  const [isOpen, setIsOpen] = useState(false);
  const [highlightIndex, setHighlightIndex] = useState(0);

  const patterns = INFILL_PATTERNS;
  const selectedPattern = useMemo(
    () => patterns.find((pattern) => pattern.value === (value || DEFAULT_INFILL_PATTERN)) ?? patterns[0],
    [patterns, value]
  );

  useEffect(() => {
    if (!isOpen) {
      return undefined;
    }

    const handlePointerDown = (event: PointerEvent) => {
      if (rootRef.current && !rootRef.current.contains(event.target as Node)) {
        setIsOpen(false);
      }
    };

    document.addEventListener('pointerdown', handlePointerDown);
    return () => document.removeEventListener('pointerdown', handlePointerDown);
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    document.getElementById(`${listboxId}-option-${highlightIndex}`)?.scrollIntoView?.({ block: 'nearest' });
  }, [highlightIndex, isOpen, listboxId]);

  const commitSelection = (patternValue: string) => {
    onChange(patternValue);
    setIsOpen(false);
    triggerRef.current?.focus();
  };

  const openDropdown = () => {
    const selectedIndex = patterns.findIndex((pattern) => pattern.value === selectedPattern.value);
    setHighlightIndex(selectedIndex >= 0 ? selectedIndex : 0);
    setIsOpen(true);
  };

  const handleKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
    const isSpaceKey = event.key === ' ' || event.key === 'Space' || event.key === 'Spacebar';

    if (!isOpen) {
      if (event.key === 'Enter' || isSpaceKey || event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault();
        openDropdown();
      }
      return;
    }

    if (event.key === 'Escape') {
      event.preventDefault();
      setIsOpen(false);
      triggerRef.current?.focus();
      return;
    }

    if (event.key === 'Tab') {
      setIsOpen(false);
      return;
    }

    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setHighlightIndex((current) => Math.min(current + 1, patterns.length - 1));
      return;
    }

    if (event.key === 'ArrowUp') {
      event.preventDefault();
      setHighlightIndex((current) => Math.max(current - 1, 0));
      return;
    }

    if (event.key === 'Home') {
      event.preventDefault();
      setHighlightIndex(0);
      return;
    }

    if (event.key === 'End') {
      event.preventDefault();
      setHighlightIndex(patterns.length - 1);
      return;
    }

    if (event.key === 'Enter' || isSpaceKey) {
      event.preventDefault();
      commitSelection(patterns[highlightIndex]?.value ?? selectedPattern.value);
    }
  };

  return (
    <div ref={rootRef} className="relative">
      <button
        ref={triggerRef}
        type="button"
        role="combobox"
        aria-expanded={isOpen}
        aria-controls={listboxId}
        aria-haspopup="listbox"
        aria-activedescendant={isOpen ? `${listboxId}-option-${highlightIndex}` : undefined}
        aria-labelledby={ariaLabelledBy ? `${ariaLabelledBy} ${selectedValueId}` : selectedValueId}
        aria-describedby={ariaDescribedBy}
        className="flex w-full items-center gap-2.5 rounded-xl border border-pf-border bg-pf-bg-1/70 px-3 py-2 text-left text-pf-text-primary transition-colors hover:border-pf-border-hover hover:bg-pf-bg-1 focus:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent"
        onClick={() => {
          if (isOpen) {
            setIsOpen(false);
            return;
          }
          openDropdown();
        }}
        onKeyDown={handleKeyDown}
      >
        <span className="flex h-7 w-7 shrink-0 items-center justify-center">
          <PatternIcon value={selectedPattern.value} className="h-4 w-4" />
        </span>
        <span id={selectedValueId} className="min-w-0 flex-1 truncate text-sm font-medium text-pf-text-primary">
          {selectedPattern.label}
        </span>
        <span aria-hidden="true" className="flex shrink-0 items-center justify-center">
          <ChevronDownIcon className="h-5 w-5 text-pf-text-muted" />
        </span>
      </button>

      {isOpen && (
        <ul
          id={listboxId}
          role="listbox"
          aria-label="Infill pattern options"
          className="absolute z-20 mt-2 max-h-80 w-full overflow-auto rounded-2xl border border-pf-border bg-pf-bg-0 p-2 shadow-2xl shadow-black/30"
        >
          {patterns.map((pattern, index) => {
            const active = pattern.value === selectedPattern.value;
            const highlighted = index === highlightIndex;
            return (
              <li
                key={pattern.value}
                id={`${listboxId}-option-${index}`}
                role="option"
                aria-selected={highlighted}
                className={[
                  'flex w-full items-center gap-2.5 rounded-lg px-2.5 py-2 text-left transition-colors',
                  highlighted
                    ? 'bg-pf-accent-bg/20 text-pf-text-primary'
                    : 'text-pf-text-secondary hover:bg-pf-bg-1 hover:text-pf-text-primary',
                ].join(' ')}
                onMouseEnter={() => setHighlightIndex(index)}
                onMouseDown={(event) => event.preventDefault()}
                onClick={() => commitSelection(pattern.value)}
              >
                <span className="flex h-6 w-6 shrink-0 items-center justify-center">
                  <PatternIcon value={pattern.value} className="h-4 w-4" />
                </span>
                <span className="min-w-0 flex-1 truncate text-sm font-medium">{pattern.label}</span>
                {active && <span className="text-xs font-semibold uppercase tracking-wide text-pf-accent">Selected</span>}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

function DeferredNumberInput({
  value,
  min,
  max,
  step,
  className,
  onCommit,
  normalize,
  ...rest
}: Omit<React.InputHTMLAttributes<HTMLInputElement>, 'type' | 'value' | 'onChange' | 'min' | 'max' | 'step' | 'className'> & {
  value: number;
  min: number;
  max: number;
  step: number;
  className: string;
  onCommit: (value: number) => void;
  normalize?: (value: number) => number;
}) {
  const [draft, setDraft] = useState(String(value));

  useEffect(() => {
    setDraft(String(value));
  }, [value]);

  const commitValue = () => {
    const parsed = Number(draft);
    if (!Number.isFinite(parsed)) {
      setDraft(String(value));
      return;
    }

    const nextValue = normalize ? normalize(parsed) : Math.min(max, Math.max(min, parsed));
    setDraft(String(nextValue));
    onCommit(nextValue);
  };

  return (
    <input
      {...rest}
      type="number"
      value={draft}
      min={min}
      max={max}
      step={step}
      className={className}
      onChange={(event) => setDraft(event.target.value)}
      onBlur={commitValue}
      onKeyDown={(event) => {
        if (event.key === 'Enter') {
          event.preventDefault();
          event.currentTarget.blur();
        }
      }}
    />
  );
}

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
  const panelId = React.useId();
  const layerHeightId = `${panelId}-layer-height`;
  const layerHeightDescId = `${panelId}-layer-height-desc`;
  const wallLoopsId = `${panelId}-wall-loops`;
  const wallLoopsDescId = `${panelId}-wall-loops-desc`;
  const topLayersId = `${panelId}-top-layers`;
  const topLayersDescId = `${panelId}-top-layers-desc`;
  const bottomLayersId = `${panelId}-bottom-layers`;
  const bottomLayersDescId = `${panelId}-bottom-layers-desc`;
  const infillDensityId = `${panelId}-infill-density`;
  const infillDensityDescId = `${panelId}-infill-density-desc`;
  const infillPatternLabelId = `${panelId}-infill-pattern-label`;
  const infillPatternDescId = `${panelId}-infill-pattern-desc`;
  const supportTypeId = `${panelId}-support-type`;
  const supportTypeDescId = `${panelId}-support-type-desc`;
  const bedAdhesionId = `${panelId}-bed-adhesion`;
  const bedAdhesionDescId = `${panelId}-bed-adhesion-desc`;
  const updateSetting = <K extends keyof SlicerSettings>(key: K, value: SlicerSettings[K]) => {
    onSettingsChange({ ...settings, [key]: value });
  };

  return (
    <div className={`bg-pf-panel border border-pf-border rounded-lg p-4 space-y-4 ${className ?? ''}`}>
      <h3 className="text-sm font-semibold text-pf-text-primary">Print Settings</h3>

      {/* Layer Height — hidden in Simple mode (encoded in process profile preset) */}
      {!simpleMode && (
        <div className="space-y-1">
          <label htmlFor={layerHeightId} className="block text-xs font-medium text-pf-text-muted uppercase tracking-wide">
            Layer Height
          </label>
          <p id={layerHeightDescId} className="text-xs text-pf-text-secondary">
            Encoded in the selected process profile preset.
          </p>
          <div className="flex items-center gap-2">
            <DeferredNumberInput
              id={layerHeightId}
              className="w-20 px-2 py-1 text-sm bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary"
              min={0.05}
              max={0.4}
              step={0.05}
              value={settings.layerHeight}
              normalize={(nextValue) => Math.min(0.4, Math.max(0.05, nextValue))}
              onCommit={(nextValue) => updateSetting('layerHeight', nextValue)}
              aria-describedby={layerHeightDescId}
            />
            <span className="text-xs text-pf-text-muted">mm</span>
          </div>
        </div>
      )}

      {/* ── Strength section ── */}
      <div className="space-y-3">
        {/* Wall Loops (perimeters) */}
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0 flex-1 space-y-1">
            <label htmlFor={wallLoopsId} className="block text-xs font-medium text-pf-text-muted uppercase tracking-wide">
              Perimeters
            </label>
            <p id={wallLoopsDescId} className="text-xs text-pf-text-secondary">Wall count for strength and finish.</p>
          </div>
          <DeferredNumberInput
            id={wallLoopsId}
            className="w-20 shrink-0 px-2 py-1 text-sm bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary text-right"
            min={1}
            max={20}
            step={1}
            value={settings.wallLoops}
            normalize={(nextValue) => Math.max(1, Math.round(nextValue))}
            onCommit={(nextValue) => updateSetting('wallLoops', nextValue)}
            aria-describedby={wallLoopsDescId}
          />
        </div>

        {/* Top Layers */}
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0 flex-1 space-y-1">
            <label htmlFor={topLayersId} className="block text-xs font-medium text-pf-text-muted uppercase tracking-wide">
              Top Layers
            </label>
            <p id={topLayersDescId} className="text-xs text-pf-text-secondary">Solid layers at the top surface.</p>
          </div>
          <DeferredNumberInput
            id={topLayersId}
            className="w-20 shrink-0 px-2 py-1 text-sm bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary text-right"
            min={0}
            max={30}
            step={1}
            value={settings.topShellLayers}
            normalize={(nextValue) => Math.max(0, Math.round(nextValue))}
            onCommit={(nextValue) => updateSetting('topShellLayers', nextValue)}
            aria-describedby={topLayersDescId}
          />
        </div>

        {/* Bottom Layers */}
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0 flex-1 space-y-1">
            <label htmlFor={bottomLayersId} className="block text-xs font-medium text-pf-text-muted uppercase tracking-wide">
              Bottom Layers
            </label>
            <p id={bottomLayersDescId} className="text-xs text-pf-text-secondary">Solid layers on the build-plate side.</p>
          </div>
          <DeferredNumberInput
            id={bottomLayersId}
            className="w-20 shrink-0 px-2 py-1 text-sm bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary text-right"
            min={0}
            max={30}
            step={1}
            value={settings.bottomShellLayers}
            normalize={(nextValue) => Math.max(0, Math.round(nextValue))}
            onCommit={(nextValue) => updateSetting('bottomShellLayers', nextValue)}
            aria-describedby={bottomLayersDescId}
          />
        </div>
      </div>

      {/* ── Infill ── */}
      <div className="space-y-3">
        <span id={infillDensityId} className="block text-xs font-medium text-pf-text-muted uppercase tracking-wide">
          Infill Density
        </span>
        <p id={infillDensityDescId} className="text-xs text-pf-text-secondary">Amount of internal fill material.</p>

        {/* Infill % */}
        <div className="flex items-center gap-3">
          <input
            id={`${panelId}-simple-infill-pct`}
            type="range"
            className="flex-1 accent-pf-accent"
            min={0}
            max={100}
            step={5}
            value={settings.infillPercent}
            onChange={(e) => updateSetting('infillPercent', Number(e.target.value))}
            aria-labelledby={infillDensityId}
            aria-describedby={infillDensityDescId}
          />
          <div className="flex items-center gap-1">
            <DeferredNumberInput
              id={`${panelId}-infill-percent`}
              className="w-14 px-2 py-1 text-sm bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary text-right"
              min={0}
              max={100}
              step={5}
              value={settings.infillPercent}
              normalize={(nextValue) => Math.min(100, Math.max(0, Math.round(nextValue)))}
              onCommit={(nextValue) => updateSetting('infillPercent', nextValue)}
              aria-labelledby={infillDensityId}
              aria-describedby={infillDensityDescId}
            />
            <span className="text-xs text-pf-text-muted">%</span>
          </div>
        </div>

        {/* Infill Pattern dropdown with OrcaSlicer icons */}
        <div className="space-y-1.5">
          <span id={infillPatternLabelId} className="block text-xs font-medium text-pf-text-muted uppercase tracking-wide">
            Infill Pattern
          </span>
          <p id={infillPatternDescId} className="text-xs text-pf-text-secondary">Pattern used for internal fill.</p>
          <InfillPatternDropdown
            value={settings.infillPattern || DEFAULT_INFILL_PATTERN}
            onChange={(pattern) => updateSetting('infillPattern', pattern)}
            ariaLabelledBy={infillPatternLabelId}
            ariaDescribedBy={infillPatternDescId}
          />
        </div>
      </div>

      {/* ── Supports ── */}
      <div className="space-y-2">
        <div className="flex items-center gap-2">
          <Checkbox
            id={`${panelId}-simple-support-enabled`}
            checked={settings.supportEnabled}
            onChange={(e) => updateSetting('supportEnabled', e.target.checked)}
          />
          <label htmlFor={`${panelId}-simple-support-enabled`} className="text-sm text-pf-text-primary cursor-pointer">
            Enable Supports
          </label>
        </div>
        <p className="pl-8 text-xs text-pf-text-secondary">Adds support under overhangs.</p>

        {settings.supportEnabled && (
          <div className="pl-6">
            <label htmlFor={supportTypeId} className="block text-xs text-pf-text-muted mb-1">
              Support Type
            </label>
            <p id={supportTypeDescId} className="mb-1 text-xs text-pf-text-secondary">Select support style.</p>
            <Select
              id={supportTypeId}
              value={settings.supportType}
              onChange={(e) => updateSetting('supportType', e.target.value)}
              aria-describedby={supportTypeDescId}
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

      {/* ── Bed Adhesion ── */}
      <div className="space-y-1">
        <label htmlFor={bedAdhesionId} className="block text-xs font-medium text-pf-text-muted uppercase tracking-wide">
          Bed Adhesion
        </label>
        <p id={bedAdhesionDescId} className="text-xs text-pf-text-secondary">Extra first-layer lines for better bed grip.</p>
        <Select
          id={bedAdhesionId}
          value={settings.bedAdhesionType}
          onChange={(e) => updateSetting('bedAdhesionType', e.target.value as SlicerSettings['bedAdhesionType'])}
          aria-describedby={bedAdhesionDescId}
        >
          {BED_ADHESION_OPTIONS.map((opt) => (
            <option key={opt.value} value={opt.value}>
              {opt.label}
            </option>
          ))}
        </Select>
      </div>
    </div>
  );
};

export default SlicerSettingsPanel;
