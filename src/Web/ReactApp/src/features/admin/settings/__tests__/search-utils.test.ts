import { describe, expect, it } from 'vitest';
import type { SettingPropertyMetadata } from '@/common/components/SettingsPagelet';
import { propertyMatchesQuery } from '../search-utils';

/**
 * The page-local filter searches what the user can *see*, which is not the same
 * set of fields it started out searching.
 *
 * #1025 moved unit suffixes out of `display.name` — where "Client Timeout (ms)"
 * inflated the label track for every row in the card — into `display.unit`,
 * rendered as an adornment beside the control. The text stayed on screen; the
 * field it lived in changed. Nothing caught that the filter had stopped seeing
 * it, because every migrated setting happened to still match on its JSON
 * property name (`clientTimeoutMs`) or its description.
 *
 * That coincidence is not a guarantee, and `SettingsCardFlow.test.tsx` now
 * actively tells future authors to reach for `Unit` instead of writing the unit
 * into the name. This is the guard for the field that instruction feeds.
 */
function prop(display: SettingPropertyMetadata['display']): SettingPropertyMetadata {
  return { name: 'someProperty', type: 'string', display } as SettingPropertyMetadata;
}

describe('propertyMatchesQuery', () => {
  it('matches a unit that is rendered as an adornment rather than in the label', () => {
    const timeout = prop({ name: 'Client Timeout', unit: 'ms' });
    expect(propertyMatchesQuery(timeout, 'ms')).toBe(true);
  });

  it('matches the unit case-insensitively, as it does every other field', () => {
    const upload = prop({ name: 'Max Upload Size', unit: 'MB' });
    expect(propertyMatchesQuery(upload, 'mb')).toBe(true);
    expect(propertyMatchesQuery(upload, 'MB')).toBe(true);
  });

  it('still matches the label, the property name and the description', () => {
    const p = prop({ name: 'Retention Days', description: 'How long rows are kept.' });
    expect(propertyMatchesQuery(p, 'retention')).toBe(true);
    expect(propertyMatchesQuery(p, 'someProp')).toBe(true);
    expect(propertyMatchesQuery(p, 'how long')).toBe(true);
  });

  it('does not match text that appears in none of the searched fields', () => {
    const p = prop({ name: 'Retention Days', unit: 'days' });
    expect(propertyMatchesQuery(p, 'kilograms')).toBe(false);
  });

  it('treats an empty query as no query rather than as a wildcard', () => {
    // A fresh render has an empty search box. Returning true here would mark
    // every property as a search hit and light up the whole page.
    expect(propertyMatchesQuery(prop({ name: 'Anything', unit: 'ms' }), '')).toBe(false);
  });
});
