import type {
  SettingMetadata,
  SettingPropertyMetadata,
  SettingValue,
} from '@/common/components/SettingsPagelet';
import { SettingInputType } from '@/types/SettingInputType';

/**
 * Deriving "this section needs attention" from real signals.
 *
 * The rule that governs this module: an attention item must correspond to a
 * condition the system can actually detect. No curated list of field keys, no
 * "these sections usually matter" heuristic. If the page says a section needs
 * attention, something is genuinely wrong with it and the user can fix it.
 *
 * Three signals qualify:
 *
 * 1. **A required field is empty.** Requirement comes from the backend, either
 *    as `[Required]` (surfaces as `RequiredAttribute`) or as
 *    `[SettingDisplay(Required = true)]`, optionally gated by `RequiredWhen`
 *    naming a boolean in the same section. The gate exists because settings
 *    classes enforce conditional invariants in their `Validate()` that the UI
 *    otherwise cannot see — discovery subnets must be non-empty, but only while
 *    discovery is enabled.
 * 2. **A value is out of its declared range.** `MinValue` / `MaxValue` come off
 *    the same attribute.
 * 3. **The server rejected the section.** A section-level error from a failed
 *    save is as real as a signal gets.
 *
 * Everything here is pure so it can be tested without rendering, and so the
 * page's save-time validation can share the exact same predicates — if the two
 * ever disagreed, the page would either flag a problem that saving accepts or
 * accept a value the banner calls broken.
 */

export type SectionValues = Record<string, SettingValue>;

/** A single detected problem, addressed to one field. */
export interface SettingsIssue {
  sectionKey: string;
  sectionLabel: string;
  field: string;
  fieldLabel: string;
  /** Banner headline — says what is wrong, away from the field. */
  title: string;
  /** Banner supporting line — says why it matters. */
  detail: string;
  /** Terse inline error rendered under the field itself. */
  message: string;
  severity: 'Error' | 'Warning';
}

/**
 * Does this field hold nothing the server would accept?
 *
 * An array counts as empty when it has no entries *or* when every entry is
 * blank. The multi-value control leaves a blank row behind when a user clears
 * one, and a list of empty strings is not a value — `NetworkDiscoverySettings`
 * rejects it as surely as it rejects `[]`. Treating `['']` as populated would
 * let the page call a section healthy that the save is about to refuse, which
 * is the exact failure this module exists to prevent.
 */
function isEmptyValue(val: SettingValue): boolean {
  if (val === undefined || val === null) return true;
  if (typeof val === 'string') return val.trim() === '';
  if (Array.isArray(val)) {
    return val.every((entry) => typeof entry === 'string' ? entry.trim() === '' : entry === null || entry === undefined);
  }
  return false;
}

/**
 * Is this property required *right now*, given the section's current values?
 *
 * `RequiredWhen` names another property in the same section (by its serialized
 * JSON name) that must be on for the requirement to apply. "On" is read with
 * the same `Boolean(...)` coercion `SettingsPagelet` uses to render the
 * checkbox, so the gate and the control the user is looking at can never
 * disagree about whether the same field is enabled.
 *
 * An unresolvable name means the gate cannot be evaluated, and we treat the
 * field as *not* required rather than nagging about a condition we cannot
 * verify — a stale annotation should not produce an unfixable error banner.
 */
export function isPropertyRequired(
  prop: SettingPropertyMetadata,
  values: SectionValues,
): boolean {
  if (prop.attributes.includes('RequiredAttribute')) return true;
  if (!prop.display?.required) return false;

  const gate = prop.display.requiredWhen;
  if (!gate) return true;
  if (!(gate in values)) return false;
  return Boolean(values[gate]);
}

/** The number a field currently holds, or NaN when it isn't numeric. */
function numericValue(prop: SettingPropertyMetadata, val: SettingValue): number {
  const isNumberType = prop.display?.inputType === SettingInputType.Number
    || ['Number', 'int', 'double'].includes(prop.type);
  if (!isNumberType) return NaN;
  if (typeof val === 'number') return val;
  if (typeof val === 'string' && val !== '') return Number(val);
  return NaN;
}

/**
 * Validate one section. Returns `{ [fieldName]: message }`.
 *
 * This is the single validation implementation: the save path calls it to block
 * a bad save, and the attention derivation calls it to decide what to flag. One
 * function, so the two cannot drift.
 */
export function validateSection(
  metaItem: SettingMetadata,
  valuesObj: SectionValues,
): Record<string, string> {
  const errs: Record<string, string> = {};
  for (const prop of metaItem.properties) {
    const val = valuesObj[prop.name];

    if (isPropertyRequired(prop, valuesObj) && isEmptyValue(val)) {
      errs[prop.name] = 'This field is required.';
      continue;
    }

    // A required list with a blank row in it is not empty, so the check above
    // lets it through — but the server does not. NetworkDiscoverySettings
    // rejects `DiscoverySubnets.Any(string.IsNullOrWhiteSpace)` outright, so
    // `['', '10.0.0.0/24']` saves clean here and 400s there. Catch it while the
    // user can still see which row is blank.
    if (isPropertyRequired(prop, valuesObj) && Array.isArray(val)) {
      const blankAt = val.findIndex(
        (entry) => entry === null || entry === undefined || (typeof entry === 'string' && entry.trim() === ''),
      );
      if (blankAt >= 0) {
        errs[prop.name] = `Entry ${blankAt + 1} is blank. Remove it or fill it in.`;
        continue;
      }
    }

    const num = numericValue(prop, val);
    if (!Number.isNaN(num)) {
      if (typeof prop.display?.minValue === 'number' && num < prop.display.minValue) {
        errs[prop.name] = `Minimum is ${prop.display.minValue}`;
      }
      if (typeof prop.display?.maxValue === 'number' && num > prop.display.maxValue) {
        errs[prop.name] = `Maximum is ${prop.display.maxValue}`;
      }
    }
  }
  return errs;
}

function labelForProperty(prop: SettingPropertyMetadata): string {
  return prop.display?.name || prop.name;
}

function labelForSection(meta: SettingMetadata): string {
  return meta.displayName || meta.className;
}

/**
 * Write the banner's copy for one field error.
 *
 * The inline error under a field can afford to be terse — "This field is
 * required." reads fine when the field it belongs to is six pixels above it.
 * The attention band is somewhere else on the page entirely, so it has to say
 * which section, which field, and why it matters. Two audiences, two strings;
 * `message` stays the inline one and never changes.
 *
 * The copy is derived from metadata rather than written per field, so a new
 * setting gets a usable sentence the day it is added and nobody has to
 * remember to author one.
 */
function describeIssue(
  meta: SettingMetadata,
  prop: SettingPropertyMetadata,
  message: string,
  values: SectionValues,
): { title: string; detail: string } {
  const section = labelForSection(meta);
  const field = labelForProperty(prop);

  if (isPropertyRequired(prop, values) && isEmptyValue(values[prop.name])) {
    const gateName = prop.display?.requiredWhen;
    const gateProp = gateName
      ? meta.properties.find((candidate) => candidate.name === gateName)
      : undefined;

    if (gateProp) {
      // Naming the switch that made this required is the whole point: without
      // it the user sees a mandatory field they never asked for.
      return {
        title: `${section} is on but ${field} is not set`,
        detail: `${field} is required while ${labelForProperty(gateProp)} is enabled.`,
      };
    }
    return {
      title: `${section} is missing ${field}`,
      detail: `${field} is required.`,
    };
  }

  return { title: `${field} is out of range in ${section}`, detail: message };
}

/**
 * Detect every issue across the rendered sections.
 *
 * `sectionErrors` carries server-reported, section-level failures (a
 * `ValidationException` with no member name). Those have no field to focus, so
 * they attach to the section itself via an empty `field`.
 */
export function deriveSettingsIssues(
  metadataItems: readonly SettingMetadata[],
  valuesBySection: Readonly<Record<string, SectionValues>>,
  sectionErrors?: Readonly<Record<string, string>> | null,
): SettingsIssue[] {
  const issues: SettingsIssue[] = [];

  for (const meta of metadataItems) {
    const sectionLabel = labelForSection(meta);
    const values = valuesBySection[meta.key] ?? {};

    const serverError = sectionErrors?.[meta.key];
    if (serverError) {
      issues.push({
        sectionKey: meta.key,
        sectionLabel,
        field: '',
        fieldLabel: sectionLabel,
        title: `${sectionLabel} was rejected`,
        detail: serverError,
        message: serverError,
        severity: 'Error',
      });
    }

    const fieldErrors = validateSection(meta, values);
    for (const prop of meta.properties) {
      const message = fieldErrors[prop.name];
      if (!message) continue;
      const { title, detail } = describeIssue(meta, prop, message, values);
      issues.push({
        sectionKey: meta.key,
        sectionLabel,
        field: prop.name,
        fieldLabel: labelForProperty(prop),
        title,
        detail,
        message,
        // Unfinished, not broken. The farm is running on its saved config; this
        // is work the admin still has to do. The Control Center already draws
        // that line as Degraded (amber) vs Unhealthy (red) — matching it keeps
        // red meaning "something failed", which is what a rejected save above
        // actually is.
        severity: 'Warning',
      });
    }
  }

  return issues;
}

/** Group issues by the section they belong to, for per-card badges. */
export function countIssuesBySection(
  issues: readonly SettingsIssue[],
): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const issue of issues) {
    counts[issue.sectionKey] = (counts[issue.sectionKey] ?? 0) + 1;
  }
  return counts;
}

/**
 * Worst severity per section, so a card can badge itself honestly: a section
 * whose save the server rejected is an error, one the admin simply hasn't
 * finished filling in is a warning.
 */
export function severityBySection(
  issues: readonly SettingsIssue[],
): Record<string, SettingsIssue['severity']> {
  const worst: Record<string, SettingsIssue['severity']> = {};
  for (const issue of issues) {
    if (worst[issue.sectionKey] !== 'Error') worst[issue.sectionKey] = issue.severity;
  }
  return worst;
}

/** How long the "look here" highlight stays on a focused row. */
const HIGHLIGHT_MS = 2000;

/**
 * Reveal and highlight a setting's field row.
 *
 * Reuses the `data-setting-property` hook and the `pf-setting-focus` highlight
 * the `?field=` deep-link already relies on, so "Fix" lands the user in exactly
 * the same place a shared link would — one focus behaviour, not two.
 *
 * Focus moves to the row's first control when it has one; otherwise the row
 * itself is focused via a temporary tabindex, so a keyboard or screen-reader
 * user is never left behind at the banner after activating the button.
 */
export function focusSettingProperty(sectionKey: string, field: string): void {
  if (typeof document === 'undefined') return;

  const qualified = `${sectionKey}.${field}`;
  const escaped = qualified.replace(/["\\]/g, '\\$&');
  const row = document.querySelector<HTMLElement>(`[data-setting-property="${escaped}"]`);
  if (!row) return;

  // Focus and highlight first, and scroll last. Scrolling is the cosmetic part;
  // moving focus is what actually lets a keyboard or screen-reader user act on
  // the row. Doing it in the other order means any environment without
  // `scrollIntoView` — jsdom, older embedded webviews — throws before the
  // meaningful work happens and the Fix button silently does nothing.
  const control = row.querySelector<HTMLElement>(
    'input, select, textarea, button, [tabindex]:not([tabindex="-1"])',
  );
  if (control) {
    control.focus({ preventScroll: true });
  } else {
    row.setAttribute('tabindex', '-1');
    row.focus({ preventScroll: true });
  }

  row.classList.add('pf-setting-focus');
  window.setTimeout(() => row.classList.remove('pf-setting-focus'), HIGHLIGHT_MS);

  if (typeof row.scrollIntoView !== 'function') return;
  const prefersReducedMotion = typeof window !== 'undefined'
    && typeof window.matchMedia === 'function'
    && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  row.scrollIntoView({
    block: 'center',
    behavior: prefersReducedMotion ? 'auto' : 'smooth',
  });
}
