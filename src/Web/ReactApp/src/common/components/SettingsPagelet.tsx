import React from 'react';
import clsx from 'clsx';
import { SettingInputType } from '@/types/SettingInputType';
import { InfoIcon, PlusIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { Button, Input, Select, Textarea, Checkbox } from '@/common/components/ui';
import { HighlightedText } from '@/features/admin/settings/HighlightedText';

export type SettingValue = string | number | boolean | string[] | number[] | (string | number)[] | Record<string, unknown> | undefined;

export interface SettingPropertyDisplayMetadata {
  name?: string;
  description?: string;
  icon?: string;
  group?: string;
  order?: number;
  inputType?: SettingInputType;
  isMulti?: boolean;
  /** Declared on the backend settings class; the field must have a value. */
  required?: boolean;
  /**
   * JSON name of a boolean property in the same section that gates `required`.
   * When set, the field is only required while that property is `true` — e.g.
   * discovery subnets are required only while discovery is enabled.
   */
  requiredWhen?: string;
  allowedValues?: unknown[];
  minValue?: number;
  maxValue?: number;
}

export interface SettingPropertyMetadata {
  name: string;
  type: string;
  attributes: string[];
  display?: SettingPropertyDisplayMetadata;
}

export interface SettingMetadata {
  key: string;
  className: string;
  displayName?: string;
  description?: string;
  icon?: string;
  group?: string;
  order?: number;
  properties: SettingPropertyMetadata[];
}

export interface SettingsPageletProps {
  metadata: SettingMetadata;
  values: Record<string, SettingValue>;
  onChange: (field: string, value: SettingValue) => void;
  fieldErrors?: Record<string, string> | null;
  isSaving?: boolean;
  error?: string | null;
  /** When true, renders only fields without the outer card wrapper and title */
  compact?: boolean;
  /**
   * Optional case-insensitive substring to highlight in property labels. Empty
   * string / undefined renders labels unchanged. Only the visible label is
   * highlighted — descriptions live in the info tooltip's `title` attribute,
   * which cannot contain rich markup, so no highlighting is applied there.
   */
  searchQuery?: string;
}

// Helper — string / number are directly usable as input values; anything else
// (undefined, arrays, objects) is coerced to '' so React doesn't warn.
function getInputValue(val: SettingValue): string | number | '' {
  if (typeof val === 'number' || typeof val === 'string') return val as string | number;
  return '';
}

/**
 * Machine values — counts, timeouts, addresses, paths — render in the mono face
 * with tabular figures so digits do not jitter between rows and `0`/`O` stay
 * distinguishable. DESIGN-LANGUAGE.md, "Numeric data".
 *
 * Array-typed settings are always machine lists in this model (subnets, hosts,
 * file extensions, directories), so their entries take the mono face too — the
 * metadata carries no element type to narrow it further.
 */
const MONO_INPUT_TYPES = new Set<SettingInputType>([
  SettingInputType.Number,
  SettingInputType.IpAddress,
  SettingInputType.Subnet,
  SettingInputType.Hostname,
  SettingInputType.Url,
  SettingInputType.File,
  SettingInputType.Directory,
]);

const MONO_FIELD_CLASS = 'font-pf-mono tabular-nums';

/**
 * Label / control split for a field row.
 *
 * The threshold and the ratio are two halves of one decision. `23rem` (368px)
 * is the narrowest card that still reads as two columns; below it the row
 * stacks. The `0.36fr / 0.64fr` ratio — rather than the fixed `w-64` (256px)
 * this replaced — is what guarantees the control keeps ~64% of the card's
 * inner width at *every* card size. A fixed label gutter cannot: inside a
 * 420px card it left roughly 164px for the actual input.
 *
 * The threshold is set by the narrowest card the page flow can produce, not
 * by taste. Bands flow into columns on the settings page, and the tightest
 * case — a 1440px window, three columns' worth of bands — lands a card at
 * 435px outer, 401px inner. A `26rem` (416px) threshold would collapse every
 * one of those rows back to stacked, which is the layout this ratio exists to
 * avoid. `23rem` clears it with 33px to spare while still floring the label
 * at `9rem` so long labels do not shred one word per line.
 *
 * Past `52rem` the ratio inverts into a problem: 36% of a 1000px card puts
 * 360px of empty space between a label and the control it names, and the pair
 * stops reading as one row. So the label track switches to a hard 16rem cap
 * and the control takes the remainder.
 */
const FIELD_ROW_CLASS =
  'grid grid-cols-1 items-start gap-x-4 gap-y-1 py-2.5 '
  + '@[23rem]:grid-cols-[minmax(9rem,0.36fr)_minmax(0,0.64fr)] '
  + '@[52rem]:grid-cols-[minmax(0,16rem)_minmax(0,1fr)]';

/**
 * The 0.64fr track is a *floor* for narrow cards. `max-w-[40rem]` is the
 * matching ceiling: a band holding one section renders that card at the full
 * content width, and a 750px-wide number input reads as a mistake. The cap is
 * set so the control still clears 60% of the card's inner width at the widest
 * card the flow will produce.
 */
const FIELD_CONTROL_CLASS = 'min-w-0 @[23rem]:max-w-[40rem]';

const InfoTooltip: React.FC<{ description: string }> = ({ description }) => (
  <span
    className="inline-flex items-center ml-1.5 text-pf-text-secondary hover:text-pf-accent cursor-help transition-colors"
    title={description}
    aria-label={description}
  >
    <InfoIcon className="w-4 h-4" />
  </span>
);

/**
 * Metadata-driven form renderer for a single settings section. Given the section
 * metadata and current values, this component renders each property as the
 * appropriate control from the shared UI library (`Input`, `Select`, `Textarea`,
 * `Checkbox`). It does NOT own state, dirty tracking, or save behaviour — those
 * belong to the parent (`SettingsPage`).
 *
 * Section-specific UI that doesn't fit the metadata (e.g. Obico's server table
 * or SlicerSettings' per-engine map) is contributed via the section-renderer
 * registry (`section-renderers.tsx`) and rendered by `SettingsPage`, not here.
 */
export const SettingsPagelet: React.FC<SettingsPageletProps> = ({ metadata, values, onChange, fieldErrors, error, compact, searchQuery }) => {
  const query = searchQuery ?? '';
  const content = (
    <div className="@container max-w-[64rem] divide-y divide-pf-border-divider">
      {metadata.properties.map((prop0: SettingPropertyMetadata) => {
        const prop = prop0 as SettingPropertyMetadata & { displayName?: string; required?: boolean };
        const displayName = (prop.display && (prop.display.name as string | undefined)) || prop.displayName || prop.name;
        const isRequired = (prop.attributes && prop.attributes.includes('RequiredAttribute')) || Boolean(prop.required);
        const err = fieldErrors?.[prop.name];
        const hasDescription = Boolean(prop.display?.description);
        const invalid = Boolean(err);
        // Property names are not unique across sections — `Enabled` is declared
        // on 13 settings classes, several of which render on the same page. A
        // bare `prop.name` id therefore emits duplicate DOM ids and points every
        // matching label at whichever control rendered first.
        const fieldId = `${metadata.key}.${prop.name}`;

        const label = (
          <label
            className="flex items-start text-sm font-medium text-pf-text-primary @[23rem]:pt-2"
            htmlFor={fieldId}
          >
            <span className="break-words">
              {query ? <HighlightedText text={displayName} query={query} /> : displayName}
            </span>
            {isRequired && <span className="text-pf-accent ml-1">*</span>}
            {hasDescription && <InfoTooltip description={prop.display!.description!} />}
          </label>
        );

        const isArray = prop.display?.inputType === SettingInputType.Array
          && prop.display?.isMulti
          && Array.isArray(values[prop.name]);
        const isBoolean = prop.display?.inputType === SettingInputType.Boolean
          || prop.type === 'Boolean'
          || prop.type === 'bool';
        const isTextArea = prop.display?.inputType === SettingInputType.TextArea;
        const isNumber = prop.display?.inputType === SettingInputType.Number
          || prop.type === 'number'
          || prop.type === 'Number'
          || prop.type === 'Int32'
          || prop.type === 'Int64'
          || prop.type === 'Double'
          || prop.type === 'Single'
          || prop.type === 'Decimal';
        const isSelect = prop.display?.inputType === SettingInputType.Select
          && Array.isArray(prop.display?.allowedValues);
        const inputType = prop.display?.inputType;
        const isMono = isNumber || (inputType !== undefined && MONO_INPUT_TYPES.has(inputType));

        let control: React.ReactNode;

        if (isArray) {
          const arr = values[prop.name] as (string | number)[];
          control = (
            <div className={FIELD_CONTROL_CLASS}>
              {arr.map((val, idx) => (
                <div key={idx} className="flex items-center mb-1.5 gap-1.5">
                  <Input
                    type={typeof val === 'number' ? 'number' : 'text'}
                    value={typeof val === 'number' ? val : typeof val === 'string' ? val : ''}
                    placeholder={displayName}
                    title={prop.display?.description || displayName}
                    aria-label={`${displayName} ${idx + 1}`}
                    className={clsx('flex-1', MONO_FIELD_CLASS)}
                    onChange={(e) => {
                      const next = Array.isArray(values[prop.name])
                        ? [...(values[prop.name] as (string | number)[])]
                        : [];
                      next[idx] = typeof val === 'number' ? Number(e.currentTarget.value) : e.currentTarget.value;
                      onChange(prop.name, next);
                    }}
                  />
                  <Button
                    type="button"
                    variant="secondary"
                    size="sm"
                    iconLeft={<CloseIcon className="w-3.5 h-3.5" />}
                    aria-label={`Remove ${displayName} ${idx + 1}`}
                    onClick={() => {
                      const next = Array.isArray(values[prop.name])
                        ? [...(values[prop.name] as (string | number)[])]
                        : [];
                      next.splice(idx, 1);
                      onChange(prop.name, next);
                    }}
                  />
                </div>
              ))}
              <Button
                type="button"
                variant="primary"
                size="sm"
                iconLeft={<PlusIcon className="w-3.5 h-3.5" />}
                aria-label={`Add ${displayName}`}
                onClick={() => {
                  const next = Array.isArray(values[prop.name])
                    ? [...(values[prop.name] as (string | number)[])]
                    : [];
                  const numeric = Array.isArray(values[prop.name])
                    && typeof (values[prop.name] as (string | number)[])[0] === 'number';
                  next.push(numeric ? 0 : '');
                  onChange(prop.name, next);
                }}
              >
                Add
              </Button>
              {err && <div className="text-pf-error text-xs mt-1" role="alert">{err}</div>}
            </div>
          );
        } else if (isBoolean) {
          control = (
            <div className={clsx(FIELD_CONTROL_CLASS, "@[23rem]:pt-1.5")}>
              <Checkbox
                id={fieldId}
                name={fieldId}
                aria-label={displayName}
                checked={Boolean(values[prop.name])}
                invalid={invalid}
                onChange={(e) => onChange(prop.name, e.currentTarget.checked)}
              />
              {err && <div className="text-pf-error text-xs mt-1" role="alert">{err}</div>}
            </div>
          );
        } else if (isTextArea) {
          control = (
            <div className={FIELD_CONTROL_CLASS}>
              <Textarea
                id={fieldId}
                name={fieldId}
                rows={2}
                value={String(getInputValue(values[prop.name] as SettingValue))}
                onChange={(e) => onChange(prop.name, e.currentTarget.value)}
                placeholder={displayName}
                title={prop.display?.description || displayName}
                aria-label={displayName}
                invalid={invalid}
                className={clsx(isMono && MONO_FIELD_CLASS)}
              />
              {err && <div className="text-pf-error text-xs mt-1" role="alert">{err}</div>}
            </div>
          );
        } else if (isNumber) {
          control = (
            <div className={FIELD_CONTROL_CLASS}>
              <Input
                id={fieldId}
                name={fieldId}
                type="number"
                value={getInputValue(values[prop.name] as SettingValue)}
                min={prop.display?.minValue}
                max={prop.display?.maxValue}
                step={prop.type === 'Double' || prop.type === 'Single' || prop.type === 'Decimal' ? 'any' : '1'}
                onChange={(e) => onChange(prop.name, e.currentTarget.value === '' ? '' : Number(e.currentTarget.value))}
                placeholder={displayName}
                title={prop.display?.description || displayName}
                aria-label={displayName}
                invalid={invalid}
                className={MONO_FIELD_CLASS}
              />
              {err && <div className="text-pf-error text-xs mt-1" role="alert">{err}</div>}
            </div>
          );
        } else if (isSelect) {
          control = (
            <div className={FIELD_CONTROL_CLASS}>
              <Select
                id={fieldId}
                name={fieldId}
                value={String(getInputValue(values[prop.name] as SettingValue))}
                onChange={(e) => onChange(prop.name, e.currentTarget.value)}
                aria-label={displayName}
                invalid={invalid}
              >
                <option value="">Select...</option>
                {prop.display!.allowedValues!.map((opt, idx) => (
                  <option key={idx} value={String(opt)}>{String(opt)}</option>
                ))}
              </Select>
              {err && <div className="text-pf-error text-xs mt-1" role="alert">{err}</div>}
            </div>
          );
        } else {
          control = (
            <div className={FIELD_CONTROL_CLASS}>
              <Input
                id={fieldId}
                name={fieldId}
                type={prop.display?.inputType === SettingInputType.Password ? 'password' : 'text'}
                value={String(getInputValue(values[prop.name] as SettingValue))}
                onChange={(e) => onChange(prop.name, e.currentTarget.value)}
                placeholder={displayName}
                title={prop.display?.description || displayName}
                aria-label={displayName}
                invalid={invalid}
                className={clsx(isMono && MONO_FIELD_CLASS)}
              />
              {err && <div className="text-pf-error text-xs mt-1" role="alert">{err}</div>}
            </div>
          );
        }

        return (
          <div
            className={FIELD_ROW_CLASS}
            key={prop.name}
            data-setting-property={`${metadata.key}.${prop.name}`}
          >
            {label}
            {control}
          </div>
        );
      })}

      {error && <div className="text-pf-error font-medium text-sm pt-2" role="alert">{error}</div>}
    </div>
  );

  if (compact) {
    return content;
  }

  return (
    <div className="settings-pagelet bg-pf-panel border border-pf-border rounded-lg p-4 mb-6">
      <h3 className="text-sm font-semibold text-pf-text-primary mb-1">{metadata.displayName || metadata.className}</h3>
      {content}
    </div>
  );
};

export default SettingsPagelet;
