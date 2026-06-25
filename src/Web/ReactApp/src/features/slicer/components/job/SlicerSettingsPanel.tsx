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
}: {
  value: string;
  onChange: (pattern: string) => void;
}) {
  const dropdownId = React.useId();
  const labelId = `${dropdownId}-label`;
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

    const selectedIndex = patterns.findIndex((pattern) => pattern.value === selectedPattern.value);
    setHighlightIndex(selectedIndex >= 0 ? selectedIndex : 0);
  }, [isOpen, patterns, selectedPattern.value]);

  const commitSelection = (patternValue: string) => {
    onChange(patternValue);
    setIsOpen(false);
    triggerRef.current?.focus();
  };

  const handleKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
    const isSpaceKey = event.key === ' ' || event.key === 'Space' || event.key === 'Spacebar';

    if (!isOpen) {
      if (event.key === 'Enter' || isSpaceKey || event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault();
        setIsOpen(true);
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
        aria-labelledby={`${labelId} ${selectedValueId}`}
        className="flex w-full items-center gap-3 rounded-2xl border border-pf-border bg-pf-bg-1/70 px-3 py-3 text-left text-pf-text-primary transition-colors hover:border-pf-border-hover hover:bg-pf-bg-1 focus:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent"
        onClick={(event) => {
          if (event.detail === 0) {
            return;
          }
          setIsOpen((current) => !current);
        }}
        onKeyDown={handleKeyDown}
        onBlur={() => setIsOpen(false)}
      >
        <span className="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl border border-pf-border bg-pf-bg-0">
          <PatternIcon value={selectedPattern.value} className="h-5 w-5" />
        </span>
        <span className="min-w-0 flex-1">
          <span id={labelId} className="block text-xs font-medium uppercase tracking-wide text-pf-text-muted">Infill Pattern</span>
          <span id={selectedValueId} className="block truncate text-base font-medium text-pf-text-primary">{selectedPattern.label}</span>
        </span>
        <span aria-hidden="true" className="flex shrink-0 items-center justify-center">
          <ChevronDownIcon className="h-5 w-5 text-pf-text-muted" ariaLabel="" />
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
                aria-selected={active}
                className={[
                  'flex w-full items-center gap-3 rounded-xl px-3 py-2.5 text-left transition-colors',
                  highlighted
                    ? 'bg-pf-accent-bg/20 text-pf-text-primary'
                    : 'text-pf-text-secondary hover:bg-pf-bg-1 hover:text-pf-text-primary',
                ].join(' ')}
                onMouseEnter={() => setHighlightIndex(index)}
                onMouseDown={(event) => event.preventDefault()}
                onClick={() => commitSelection(pattern.value)}
              >
                <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg border border-pf-border bg-pf-bg-1">
                  <PatternIcon value={pattern.value} className="h-[1.1rem] w-[1.1rem]" />
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

function BedAdhesionIcon({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 24 24" className={className} aria-hidden="true">
      <rect x="5" y="8" width="14" height="10" rx="2.5" fill="none" stroke="currentColor" strokeWidth="1.6" />
      <path d="M5 16h14" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
      <path d="M7 5.5v5" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
      <path d="M10 4.5v6" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
      <path d="M13 4v6.5" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
      <path d="M16 5v5.5" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
    </svg>
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
          <p className="text-xs text-pf-text-secondary">Outer walls that define part strength and surface finish.</p>
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
          <p className="text-xs text-pf-text-secondary">Solid layers on the top surface of the print.</p>
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
          <p className="text-xs text-pf-text-secondary">Solid layers on the build-plate side of the print.</p>
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
          Infill Density
        </span>
        <p className="text-xs text-pf-text-secondary">How much material fills the inside of the print.</p>

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
        <div className="space-y-1.5">
          <span className="block text-xs font-medium text-pf-text-muted uppercase tracking-wide">Infill Pattern</span>
          <p className="text-xs text-pf-text-secondary">Choose the shape pattern used to fill the print.</p>
          <InfillPatternDropdown
            value={settings.infillPattern || DEFAULT_INFILL_PATTERN}
            onChange={(pattern) => updateSetting('infillPattern', pattern)}
          />
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
        <p className="pl-8 text-xs text-pf-text-secondary">Adds support structures for overhanging parts.</p>

        {settings.supportEnabled && (
          <div className="pl-6">
            <label htmlFor="simple-support-type" className="block text-xs text-pf-text-muted mb-1">
              Support Type
            </label>
            <p className="mb-1 text-xs text-pf-text-secondary">Choose the type of support structure.</p>
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
      <div className="space-y-3 rounded-2xl border border-pf-border bg-pf-bg-1/40 p-4">
        <div className="flex items-start gap-3">
          <span className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-xl border border-pf-border bg-pf-bg-0 text-pf-accent">
            <BedAdhesionIcon className="h-4 w-4" />
          </span>
          <div className="min-w-0">
            <h4 className="text-lg font-semibold text-pf-text-primary">Bed Adhesion</h4>
            <p className="mt-1 text-sm text-pf-text-secondary">Choose between skirt or brim for better print adhesion</p>
          </div>
        </div>
        <div className="space-y-4 pl-1" role="radiogroup" aria-label="Bed adhesion type">
          {BED_ADHESION_OPTIONS.map((opt) => (
            <label key={opt.value} className="flex cursor-pointer items-center gap-3 text-lg text-pf-text-primary">
              <input
                type="radio"
                name="bed-adhesion"
                value={opt.value}
                checked={settings.bedAdhesionType === opt.value}
                onChange={() => updateSetting('bedAdhesionType', opt.value)}
                className="h-7 w-7 accent-pf-accent"
              />
              <span className="text-[1.35rem] leading-none">{opt.label}</span>
            </label>
          ))}
        </div>
      </div>
    </div>
  );
};

export default SlicerSettingsPanel;
