import React, { useMemo } from 'react';
import clsx from 'clsx';
import { SettingInputType } from '@/types/SettingInputType';
import { InfoIcon, PlusIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { Button, Input, Select, Textarea, Checkbox } from '@/common/components/ui';
import { HighlightedText } from '@/features/admin/settings/HighlightedText';
import { isPropertyRequired, isPropertyAlwaysRequired } from '@/features/admin/settings/settingsAttention';

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
 * The floor, the threshold and the ratio are three halves of one decision, and
 * the floor is the one that matters. It was `9rem` (144px), justified as being
 * there "so long labels do not shred one word per line" — which it was not
 * achieving. Measured in Chromium against the real `System Config` labels:
 *
 *   196px  "Enable Background Scanning"   <- worst case text
 *   171px  "Enable Database Logging"
 *   138px  "Allowed Extensions *"
 *   134px  "Discovery Subnets *"
 *
 * Against a 144px track the first of those wrapped to *three* lines and the
 * next three to two, at every window width from 900px to 2200px.
 *
 * The number the track has to clear is not the text width, though. The label
 * is a flex row holding the text *and* `InfoTooltip`, which measures a
 * consistent 22px (16px icon + its `ml-1.5`) on every field that has one.
 *
 * The floor is sized against *every* label the app ships, not the ones on the
 * tab that happened to be open. All 131 distinct `[SettingDisplay(Name = ...)]`
 * values were rendered in this exact face (`500 14px Inter`) and measured. The
 * widest is "Print Warmup Grace Period (seconds)" on `ObicoSettings`, then
 * "Runout Warning Lead Time (minutes)" on `SpoolCoverageSettings` and
 * "Analysis Request Timeout (seconds)" — none of which are on System Config.
 * An earlier pass sized this at 232px from that one tab and left nine labels
 * wrapping on Automation & Costs, one click away. Independent measurements of
 * the worst label came in between 265px and 275px depending on whether the
 * required marker was counted; the floor is sized against the largest of them,
 * so worst case is 275 + 22 = 297px and the floor is `19.5rem` (312px) — the
 * full label block plus 15px of headroom.
 *
 * `SettingsCardFlow.test.tsx` reads the C# attributes back and fails if a
 * longer label is ever added, because that measurement cannot be repeated from
 * a unit test and would otherwise rot silently — which is exactly how the
 * 232px version shipped.
 *
 * The floor now dominates the `0.36fr` ratio everywhere the row is side by
 * side: 0.36 only overtakes 312px at 867px inner, past the `52rem` (832px)
 * inversion, which caps it straight back to the same 19.5rem. That is
 * deliberate — cap and floor are one number, so the label gutter is a constant
 * 312px at every card width and the crossover has no step in it.
 *
 * That inversion exists because 36% of a 1000px card would put 360px of empty
 * space between a label and the control it names. Holding the cap equal to the
 * floor also means the inversion cannot reintroduce wrapping, which a lower
 * cap silently did: the previous 16rem (256px) cap sat under the 297px worst
 * case, so the widest labels wrapped in *wide* single-column cards while
 * narrow ones stayed clean — the least findable form of this bug.
 *
 * `33rem` (528px) is the narrowest card that reads as two columns, derived
 * rather than chosen: 312px of label + 16px gap leaves 200px of control, which
 * is what the widest content needs (a `255.255.255.255/32` CIDR in the mono
 * face plus its clear button). Below `33rem` the row stacks, which is the
 * *most* legible option at that size — the label gets the full width and
 * cannot wrap at all. `bandFlowClass` is pinned to the same 528px, so no flow
 * can produce a card that lands between the two rules.
 *
 * The cost of carrying a 312px gutter for labels this long is real, and the
 * better long-term fix is to stop shipping the unit inside the label at all —
 * "Runout Warning Lead Time" with `minutes` rendered as a control adornment
 * drops every offender under 200px and would let the column thresholds come
 * back down. That is a metadata/content change rather than a layout one, so it
 * is tracked separately rather than smuggled into this fix.
 */
const FIELD_ROW_CLASS =
  'grid grid-cols-1 items-start gap-x-4 gap-y-1 py-2.5 '
  + '@[33rem]:grid-cols-[minmax(19.5rem,0.36fr)_minmax(0,0.64fr)] '
  + '@[52rem]:grid-cols-[minmax(0,19.5rem)_minmax(0,1fr)]';

/**
 * The 0.64fr track is a *floor* for narrow cards. `max-w-[40rem]` is the
 * matching ceiling: a band holding one section renders that card at the full
 * content width, and a 750px-wide number input reads as a mistake. The cap is
 * set so the control still clears 60% of the card's inner width at the widest
 * card the flow will produce.
 */
const FIELD_CONTROL_CLASS = 'min-w-0 @[33rem]:max-w-[40rem]';

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

  // Required fields lead, everything else keeps its declared order (#1012).
  // `sort` is stable in every engine we target, so the non-required tail is
  // untouched and a section with no required fields renders exactly as before.
  const orderedProperties = useMemo(
    () =>
      [...metadata.properties].sort(
        (a, b) => Number(isPropertyAlwaysRequired(b)) - Number(isPropertyAlwaysRequired(a)),
      ),
    [metadata.properties],
  );

  const content = (
    <div className="@container max-w-[64rem] divide-y divide-pf-border-divider">
      {orderedProperties.map((prop0: SettingPropertyMetadata) => {
        const prop = prop0 as SettingPropertyMetadata & { displayName?: string };
        const displayName = (prop.display && (prop.display.name as string | undefined)) || prop.displayName || prop.name;
        // `isPropertyRequired` is the one predicate that knows about `RequiredWhen`.
        // Reading `prop.required` here instead would be wrong twice over: the
        // metadata lives at `display.required`, so the flag never arrives, and a
        // conditionally-required field would claim to be required even while its
        // gate is off. The attention banner already uses this predicate, so
        // sharing it is what stops the asterisk and the banner disagreeing.
        const isRequired = isPropertyRequired(prop0, values);
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
            className="flex items-start text-sm font-medium text-pf-text-primary @[33rem]:pt-2"
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
            // The requirement is "at least one entry", which is a property of the
            // collection rather than of any one row. Marking each row required
            // would tell a screen reader every existing row must stay filled.
            //
            // `aria-required` is NOT a supported attribute on `role="group"`
            // (ARIA allows it on textbox, combobox, listbox, radiogroup and
            // friends), so assistive tech drops it. A description the group
            // points at is valid on any role and is actually announced.
            <div
              className={FIELD_CONTROL_CLASS}
              role="group"
              aria-label={displayName}
              aria-describedby={isRequired ? `${fieldId}-required` : undefined}
            >
              {isRequired && (
                <span id={`${fieldId}-required`} className="sr-only">
                  Required — enter at least one value.
                </span>
              )}
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
            <div className={clsx(FIELD_CONTROL_CLASS, "@[33rem]:pt-1.5")}>
              <Checkbox
                id={fieldId}
                name={fieldId}
                aria-label={displayName}
                aria-required={isRequired || undefined}
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
                aria-required={isRequired || undefined}
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
                aria-required={isRequired || undefined}
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
                aria-required={isRequired || undefined}
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
                aria-required={isRequired || undefined}
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
