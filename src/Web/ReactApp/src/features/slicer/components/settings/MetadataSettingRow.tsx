/**
 * MetadataSettingRow — renders a single setting from orcaSettingsMetadata.json.
 *
 * Reads a setting's metadata (type, label, tooltip, unit, min/max, default,
 * enum_values, gui_type) and renders the correct input control through the
 * existing SettingRow component.  Handles:
 *  • checkbox, number, text, color, select, textarea, point controls
 *  • paired temperature rows (first-layer + other-layers side-by-side)
 *  • change tracking with reset-to-original button
 */
import React from 'react';
import { Button, Textarea } from '@/common/components/ui';
import { SettingRow, ResetIcon } from '@/features/slicer/components/settings/SettingRow';
import type { SettingMetadata, FieldRef } from '@/features/slicer/components/settings/metadataTypes';
import {
  KNOWN_ENUMS,
  resolveControlType,
  toNumber,
  toBool,
  parsePoint,
  parseCoFloats,
  toString,
} from '@/features/slicer/components/settings/metadataTypes';

// ── Props ───────────────────────────────────────────────────────────────

export interface MetadataSettingRowProps {
  /** The field reference from the section layout */
  field: FieldRef;
  /** Metadata for this field's key */
  meta: SettingMetadata;
  /** Current settings values keyed by setting key */
  values: Record<string, unknown>;
  /** Original (saved) values for change tracking */
  originalValues?: Record<string, unknown>;
  /** Fires when the user changes this setting's value */
  onUpdate: (key: string, value: unknown) => void;
  /** Whether controls are disabled */
  disabled: boolean;
  /**
   * Optional paired "other layers" key for temperature rows.
   * When set, renders a side-by-side first-layer / other-layers layout.
   */
  pairedOtherKey?: string;
  /** Metadata for the paired key (required when pairedOtherKey is set) */
  pairedOtherMeta?: SettingMetadata;
  /** Whether to show the label (textareas in single-field sections may hide it) */
  showLabel?: boolean;
}

// ── Helpers ─────────────────────────────────────────────────────────────

function useChangeTracking(
  key: string,
  values: Record<string, unknown>,
  originalValues?: Record<string, unknown>,
) {
  const origVal = originalValues?.[key];
  const curVal = values[key];
  const isModified =
    originalValues !== undefined &&
    origVal !== undefined &&
    JSON.stringify(curVal) !== JSON.stringify(origVal);
  return { origVal, isModified };
}

// ── Component ───────────────────────────────────────────────────────────

export const MetadataSettingRow: React.FC<MetadataSettingRowProps> = ({
  field,
  meta,
  values,
  originalValues,
  onUpdate,
  disabled,
  pairedOtherKey,
  pairedOtherMeta,
  showLabel = true,
}) => {
  const { origVal, isModified } = useChangeTracking(field.key, values, originalValues);
  // Always call the paired hook unconditionally (rules of hooks)
  const { origVal: otherOrigVal, isModified: otherIsModified } = useChangeTracking(
    pairedOtherKey ?? field.key,
    values,
    originalValues,
  );
  const controlType = resolveControlType(meta);

  const resetProps = isModified
    ? {
        isModified: true,
        originalValue: origVal,
        onReset: () => onUpdate(field.key, origVal),
      }
    : {};

  // ── Paired temperature row ────────────────────────────────────────
  if (pairedOtherKey && pairedOtherMeta) {
    const anyModified = isModified || otherIsModified;
    const plateName = field.key
      .replace('_temp_initial_layer', '')
      .replace(/_/g, ' ')
      .replace(/\b\w/g, (c) => c.toUpperCase())
      .replace('Supertack', 'SuperTack')
      .replace('Eng', 'Engineering')
      .replace('Hot', 'Smooth PEI / High Temp');

    return (
      <div className="flex items-center gap-1.5 py-0.5">
        <div className="w-2/5 shrink-0 truncate">
          <span
            className={`text-xs font-medium ${anyModified ? 'text-pf-warning' : 'text-pf-text'}`}
            title={meta.tooltip}
          >
            {plateName}
          </span>
        </div>
        <div className="w-[30%] shrink-0 flex items-center gap-1">
          <span className="text-[10px] text-pf-text-muted whitespace-nowrap">First layer</span>
          <div className="flex items-center flex-1">
            <input
              type="number"
              title={`${plateName} first layer`}
              className="w-full py-1 px-2 bg-pf-panel border border-pf-border text-pf-text text-xs text-right rounded-l-lg rounded-r-none border-r-0 hover:border-pf-border-light focus:border-pf-accent-2 focus:outline-hidden"
              value={toNumber(values[field.key], meta)}
              onChange={(e) => onUpdate(field.key, Number(e.target.value))}
              disabled={disabled}
            />
            <span className="text-xs text-pf-text-muted px-1.5 bg-pf-border rounded-r-lg w-8 shrink-0 self-stretch flex items-center">
              °C
            </span>
          </div>
        </div>
        <div className="w-[30%] shrink-0 flex items-center gap-1">
          <span className="text-[10px] text-pf-text-muted whitespace-nowrap">Other layers</span>
          <div className="flex items-center flex-1">
            <input
              type="number"
              title={`${plateName} other layers`}
              className="w-full py-1 px-2 bg-pf-panel border border-pf-border text-pf-text text-xs text-right rounded-l-lg rounded-r-none border-r-0 hover:border-pf-border-light focus:border-pf-accent-2 focus:outline-hidden"
              value={toNumber(values[pairedOtherKey], pairedOtherMeta)}
              onChange={(e) => onUpdate(pairedOtherKey, Number(e.target.value))}
              disabled={disabled}
            />
            <span className="text-xs text-pf-text-muted px-1.5 bg-pf-border rounded-r-lg w-8 shrink-0 self-stretch flex items-center">
              °C
            </span>
          </div>
        </div>
        <div className="w-7 shrink-0 flex justify-center">
          {anyModified && (
            <Button
              variant="subtle"
              type="button"
              onClick={() => {
                if (isModified) onUpdate(field.key, origVal);
                if (otherIsModified) onUpdate(pairedOtherKey, otherOrigVal);
              }}
              className="p-0.5 text-pf-warning hover:text-pf-warning transition-colors hover:bg-pf-warning/10 rounded"
              title="Reset to original"
              aria-label={`Reset ${plateName} temperatures to original values`}
            >
              <ResetIcon className="w-4 h-4" />
            </Button>
          )}
        </div>
      </div>
    );
  }

  // ── Standard control types ────────────────────────────────────────
  switch (controlType) {
    case 'checkbox':
      return (
        <SettingRow
          type="checkbox"
          label={meta.label}
          tooltip={meta.tooltip}
          checked={toBool(values[field.key], meta)}
          onChange={(v) => onUpdate(field.key, v)}
          disabled={disabled}
          {...resetProps}
        />
      );

    case 'number':
      return (
        <SettingRow
          type="number"
          label={meta.label}
          tooltip={meta.tooltip}
          value={toNumber(values[field.key], meta)}
          min={meta.min}
          max={meta.max}
          step={meta.type === 'int' ? 1 : 0.01}
          unit={meta.type === 'float_or_percent' ? 'mm or %' : meta.unit}
          onChange={(v) => onUpdate(field.key, v)}
          disabled={disabled}
          {...resetProps}
        />
      );

    case 'color':
      return (
        <SettingRow
          type="color"
          label={meta.label}
          tooltip={meta.tooltip}
          value={toString(values[field.key], meta)}
          onChange={(v) => onUpdate(field.key, v)}
          disabled={disabled}
          {...resetProps}
        />
      );

    case 'select': {
      const options =
        KNOWN_ENUMS[field.key] ??
        meta.enum_values?.map((v: string) => ({ value: v, label: v })) ??
        [];
      return (
        <SettingRow
          type="select"
          label={meta.label}
          tooltip={meta.tooltip}
          value={toString(values[field.key], meta)}
          options={options}
          onChange={(v) => onUpdate(field.key, v)}
          disabled={disabled}
          {...resetProps}
        />
      );
    }

    case 'textarea':
      return (
        <div className="py-0.5">
          {(showLabel || isModified) && (
            <div className="flex items-center gap-1.5 mb-1">
              {showLabel && (
                <span
                  className={`text-xs ${isModified ? 'text-pf-warning font-medium' : 'text-pf-text-secondary'}`}
                  title={meta.tooltip}
                >
                  {meta.label}
                </span>
              )}
              {isModified && (
                <Button
                  variant="subtle"
                  type="button"
                  onClick={() => onUpdate(field.key, origVal)}
                  className="p-0.5 text-pf-warning hover:text-pf-warning transition-colors hover:bg-pf-warning/10 rounded shrink-0"
                  title="Reset to original"
                  aria-label={`Reset ${meta.label} to original value`}
                >
                  <ResetIcon className="w-4 h-4" />
                </Button>
              )}
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

    case 'point': {
      const [px, py] = parsePoint(values[field.key], meta);
      return (
        <div className="flex items-center gap-1.5 py-0.5">
          <div className="flex items-center gap-1.5 w-2/5 shrink-0">
            <span
              className={`text-xs truncate ${isModified ? 'text-pf-warning font-medium' : 'text-pf-text-secondary'}`}
              title={meta.tooltip}
            >
              {meta.label}
            </span>
          </div>
          <div className="flex items-center gap-1.5 flex-1 min-w-0">
            <div className="flex-1 flex items-center bg-pf-panel border border-pf-border rounded overflow-hidden">
              <span className="px-1.5 text-xs text-pf-text-muted select-none">X</span>
              <input
                type="number"
                title={`${meta.label} X`}
                className="flex-1 px-1 py-1 text-xs text-right bg-transparent border-none outline-none"
                value={px}
                onChange={(e) => onUpdate(field.key, `${e.target.value},${py}`)}
                disabled={disabled}
              />
            </div>
            <div className="flex-1 flex items-center bg-pf-panel border border-pf-border rounded overflow-hidden">
              <span className="px-1.5 text-xs text-pf-text-muted select-none">Y</span>
              <input
                type="number"
                title={`${meta.label} Y`}
                className="flex-1 px-1 py-1 text-xs text-right bg-transparent border-none outline-none"
                value={py}
                onChange={(e) => onUpdate(field.key, `${px},${e.target.value}`)}
                disabled={disabled}
              />
            </div>
          </div>
          <div className="w-7 shrink-0 flex justify-center">
            {isModified && (
              <Button
                variant="subtle"
                type="button"
                onClick={() => onUpdate(field.key, origVal)}
                className="p-0.5 text-pf-warning hover:text-pf-warning transition-colors hover:bg-pf-warning/10 rounded"
                title="Reset to original"
                aria-label={`Reset ${meta.label} to original value`}
              >
                <ResetIcon className="w-4 h-4" />
              </Button>
            )}
          </div>
        </div>
      );
    }

    case 'coFloats': {
      const floats = parseCoFloats(values[field.key], meta);
      const isSingle = floats.length <= 1;

      if (isSingle) {
        return (
          <SettingRow
            type="number"
            label={meta.label}
            tooltip={meta.tooltip}
            value={floats[0] ?? 0}
            min={meta.min}
            max={meta.max}
            step={0.01}
            unit={meta.unit}
            onChange={(v) => onUpdate(field.key, String(v))}
            disabled={disabled}
            {...resetProps}
          />
        );
      }

      return (
        <div className="flex items-center gap-1.5 py-0.5">
          <div className="w-2/5 shrink-0 truncate">
            <span
              className={`text-xs font-medium ${isModified ? 'text-pf-warning' : 'text-pf-text-secondary'}`}
              title={meta.tooltip}
            >
              {meta.label}
            </span>
          </div>
          <div className="flex items-center gap-1.5 flex-1 min-w-0">
            {floats.map((val, idx) => (
              <div key={idx} className="flex items-center gap-0.5 flex-1 min-w-0">
                <span className="text-[10px] text-pf-text-muted whitespace-nowrap">
                  {`E${idx + 1}`}
                </span>
                <div className="flex items-center flex-1 min-w-0">
                  <input
                    type="number"
                    title={`${meta.label} Extruder ${idx + 1}`}
                    className="w-full py-1 px-2 bg-pf-panel border border-pf-border text-pf-text text-xs text-right rounded-l-lg rounded-r-none border-r-0 hover:border-pf-border-light focus:border-pf-accent-2 focus:outline-hidden"
                    value={val}
                    min={meta.min}
                    max={meta.max}
                    step={0.01}
                    onChange={(e) => {
                      const updated = [...floats];
                      updated[idx] = Number(e.target.value);
                      onUpdate(field.key, updated.join(','));
                    }}
                    disabled={disabled}
                  />
                  {meta.unit && (
                    <span className="text-[10px] text-pf-text-muted px-1 bg-pf-border rounded-r-lg shrink-0 self-stretch flex items-center">
                      {meta.unit}
                    </span>
                  )}
                </div>
              </div>
            ))}
          </div>
          <div className="w-7 shrink-0 flex justify-center">
            {isModified && (
              <Button
                variant="subtle"
                type="button"
                onClick={() => onUpdate(field.key, origVal)}
                className="p-0.5 text-pf-warning hover:text-pf-warning transition-colors hover:bg-pf-warning/10 rounded"
                title="Reset to original"
                aria-label={`Reset ${meta.label} to original values`}
              >
                <ResetIcon className="w-4 h-4" />
              </Button>
            )}
          </div>
        </div>
      );
    }

    default:
      return (
        <SettingRow
          type="text"
          label={meta.label}
          tooltip={meta.tooltip}
          value={toString(values[field.key], meta)}
          onChange={(v) => onUpdate(field.key, v)}
          disabled={disabled}
          {...resetProps}
        />
      );
  }
};
