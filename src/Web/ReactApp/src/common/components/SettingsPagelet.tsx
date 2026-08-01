import React, { useMemo } from 'react';
import clsx from 'clsx';
import { SettingInputType } from '@/types/SettingInputType';
import { InfoIcon, PlusIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { Button, Input, Select, Textarea, Toggle } from '@/common/components/ui';
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
  /**
   * Unit of measure, rendered as an adornment beside the control instead of
   * inside the label. Bare and lowercase ("minutes"); this component supplies
   * the presentation.
   */
  unit?: string;
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
 * Label / control split for a field row (#1030).
 *
 * The label track is a **cap**, not a floor:
 *
 *     @[26rem]:grid-cols-[minmax(0,13.5rem)_minmax(0,1fr)]
 *
 * That is the ACC proposal's number (`acc-consistency-proposal.html`, `.p-f`),
 * and the distinction is the whole point. `minmax(0, 13.5rem)` lets the track
 * *shrink*, so a long label may wrap. `minmax(19.5rem, 0.36fr)` — what #1020
 * shipped — could not, because it was sized so that no label could ever wrap
 * at any width.
 *
 * That "never wrap" invariant was never in the proposal. Deriving it from the
 * widest of 131 shipped `[SettingDisplay(Name = ...)]` labels ("Print Warmup
 * Grace Period (seconds)", 275px + 22px for `InfoTooltip` = 297px, floored to
 * 312px) meant every row in the app paid 96px for one label on one tab. Two
 * columns then needed a ~1738px viewport where the proposal reaches them at
 * 1180px — the density complaint that opened #1029.
 *
 * The proposal buys legibility back by other means, all of which are now in
 * place rather than traded away:
 *
 *   - 13px label instead of 14px (~7% narrower)
 *   - units live in a control adornment, not the label text (#1025), which is
 *     what actually retires the long-label problem at source
 *   - the rare genuinely-long label is allowed to wrap onto a second line
 *
 * There is no `@[52rem]` inversion any more, and there does not need to be.
 * That band existed only to stop a *floor* from being stretched by an `fr`
 * ratio into a 360px gutter on a wide card. A cap cannot be stretched, so the
 * crossover it guarded no longer exists — which also retires the entire class
 * of bug that produced #1020 and its two follow-up fixes, where cap and floor
 * were chosen independently and disagreed.
 *
 * `26rem` (416px) is the stack threshold: below it the row stacks, label above
 * control, which is the most legible option at that size and cannot wrap at
 * all. `bandFlowClass` is pinned to the same number so no flow produces a card
 * that lands between the two rules.
 *
 * `SettingsCardFlow.test.tsx` still walks the C# attributes and fails when a
 * longer label appears. It no longer proves "nothing wraps" — that is not a
 * property we hold. It proves we still know what the longest label is, so a
 * regression in the #1025 adornment work is visible rather than silent.
 */
const FIELD_ROW_CLASS =
  'grid grid-cols-1 items-start gap-x-3 gap-y-1 py-2.5 '
  + '@[26rem]:grid-cols-[minmax(0,13.5rem)_minmax(0,1fr)]';

/**
 * The control's matching ceiling. A band holding one section renders that card
 * at the full content width, and a 750px-wide number input reads as a mistake.
 *
 * Pinned to the same `26rem` threshold as `FIELD_ROW_CLASS` so the control cap
 * and the side-by-side layout switch on together — they described one decision
 * even when they were two numbers, and #1020 shipped twice with them disagreeing.
 * The cap *value* is retuned separately in #1033.
 */
const FIELD_CONTROL_CLASS = 'min-w-0 @[26rem]:max-w-[40rem]';

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
 * `Toggle`). It does NOT own state, dirty tracking, or save behaviour — those
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

  // Matches the `74rem` cap on a single-card flow in `SettingsPage`. If this
  // stayed narrower, the `divide-y` rules between rows would stop short of the
  // card's own right padding on a wide single-column layout, which reads as a
  // rendering fault rather than a measure. Widening is safe: the label track is
  // capped at `13.5rem` and the control at `40rem`, so the extra width lands as
  // trailing space and cannot move a label away from the control it names.
  const content = (
    <div className="@container max-w-[74rem] divide-y divide-pf-border-divider">
      {orderedProperties.map((prop0: SettingPropertyMetadata) => {
        const prop = prop0 as SettingPropertyMetadata & { displayName?: string };
        const displayName = (prop.display && (prop.display.name as string | undefined)) || prop.displayName || prop.name;
        // Every control below sets `aria-label`, which *overrides* the `<label>`
        // element rather than adding to it. So when the unit moved out of the
        // visible label (#1025) it had to be put back here, or a screen reader
        // would announce "Runout Warning Lead Time" and never mention minutes.
        // This keeps the spoken name identical to what it was before the move.
        const accessibleName = prop.display?.unit
          ? `${displayName} (${prop.display.unit})`
          : displayName;
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
            className="flex items-start text-[13px] font-medium text-pf-text-secondary @[26rem]:pt-1.5"
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
              aria-label={accessibleName}
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
            <div className={clsx(FIELD_CONTROL_CLASS, "@[26rem]:pt-1")}>
              {/*
                A switch, not a checkbox (#1019). Every boolean in this surface
                is a live setting that takes effect on save, not an item being
                selected from a set — which is the distinction the two controls
                actually carry, and the proposal renders them as switches.

                `size="sm"` (32x16) rather than the proposal's 38x20. The design
                system ships two switch sizes, 32x16 and 44x24, and both are in
                use elsewhere. Introducing a third that exists only on this page
                would be precisely the kind of local divergence this epic was
                opened to remove, so the deviation is 6px of width and is taken
                deliberately.
              */}
              <Toggle
                id={fieldId}
                name={fieldId}
                size="sm"
                aria-label={accessibleName}
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
                aria-label={accessibleName}
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
                aria-label={accessibleName}
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
                aria-label={accessibleName}
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
                aria-label={accessibleName}
                aria-required={isRequired || undefined}
                invalid={invalid}
                className={clsx(isMono && MONO_FIELD_CLASS)}
              />
              {err && <div className="text-pf-error text-xs mt-1" role="alert">{err}</div>}
            </div>
          );
        }

        // The unit sits beside the control, not inside the label (#1025).
        //
        // Writing it into the label — "Runout Warning Lead Time (minutes)" —
        // is what made the label track the widest thing on the page: nine of
        // the ten labels that wrapped did so only because of a parenthetical.
        // Beside the control it also reads better, because a unit describes
        // the value the user is typing, not the thing being named.
        //
        // Wrapped at the row rather than inside each control branch: there are
        // six of those, and the unit's relationship to the control is the same
        // in all of them. `items-start` + `pt-2` keeps the unit on the input's
        // line when a validation error pushes a second line underneath.
        const unit = prop.display?.unit;
        const controlWithUnit = unit
          ? (
            <div className="flex min-w-0 items-start gap-2">
              {control}
              <span
                className="shrink-0 pt-2 text-[12px] text-pf-text-secondary"
                data-setting-unit
                aria-hidden="true"
              >
                {unit}
              </span>
            </div>
          )
          : control;

        return (
          <div
            className={FIELD_ROW_CLASS}
            key={prop.name}
            data-setting-property={`${metadata.key}.${prop.name}`}
          >
            {label}
            {controlWithUnit}
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
