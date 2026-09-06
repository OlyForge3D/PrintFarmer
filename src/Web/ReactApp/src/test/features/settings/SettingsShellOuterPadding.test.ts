import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const repoSrc = resolve(here, '../../../');

function read(rel: string): string {
  return readFileSync(resolve(repoSrc, rel), 'utf8');
}

/**
 * Extracts the opening `<PageTemplate ...>` tag's attribute text from a
 * source file, so assertions can inspect its props without matching props on
 * unrelated JSX elsewhere in the file.
 *
 * Guards against silently-vacuous slicing: fails loudly (rather than passing
 * on an empty string) if `<PageTemplate` is missing, and requires the
 * captured span to still contain a prop every real call site sets (`title=`)
 * so a stray `>` inside an earlier prop value (e.g. an inline arrow function
 * or a comparison) can't truncate the slice before it reaches `padding=`.
 */
function pageTemplateOpeningTag(source: string): string {
  const start = source.indexOf('<PageTemplate');
  expect(start).toBeGreaterThan(-1);
  const end = source.indexOf('>', start);
  expect(end).toBeGreaterThan(-1);
  const tag = source.slice(start, end + 1);
  expect(tag).toContain('title=');
  return tag;
}

/**
 * Regression coverage for #1416: Settings (`/settings`, `/admin/settings`) and
 * configuration all render through `SettingsShell`, and the Admin
 * Control Center (`/admin`) renders through `AdminControlCenterPage`. Both
 * mount `<PageTemplate>` directly, so the outer horizontal padding every route
 * gets is whatever `padding` prop (or lack of one) each call site passes.
 *
 * `SettingsShell` used to pass `padding="px-0"`, zeroing out the 16px
 * `PageTemplate` contributes by default and leaving Settings/Manage 16px
 * narrower than `/admin` at every viewport width (8px of `Layout`'s own
 * padding vs. the hub's 8px + 16px). Asserting neither call site names an
 * explicit `padding` override keeps them locked to the same default token
 * without hardcoding the token's value in two places.
 */
describe('Settings/Manage shell shares the Admin Control Center outer padding token', () => {
  it('SettingsShell does not override PageTemplate padding', () => {
    const source = read('features/settings/pages/SettingsShell.tsx');
    expect(pageTemplateOpeningTag(source)).not.toMatch(/padding=/);
  });

  it('AdminControlCenterPage does not override PageTemplate padding', () => {
    const source = read('features/admin/pages/AdminControlCenterPage.tsx');
    expect(pageTemplateOpeningTag(source)).not.toMatch(/padding=/);
  });
});
