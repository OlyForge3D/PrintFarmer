import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const repoSrc = resolve(here, '../../../');

const SHELL_FILES = [
  'features/settings/pages/SettingsShell.tsx',
  'features/settings/components/SettingsSubTabs.tsx',
  'features/settings/components/SettingsSection.tsx',
  'features/settings/components/SettingsContentTransition.tsx',
];

function read(rel: string): string {
  return readFileSync(resolve(repoSrc, rel), 'utf8');
}

/**
 * The settings shell is the frame every admin destination is rendered inside,
 * so decoration it invents is decoration the whole admin surface inherits.
 * DESIGN-LANGUAGE.md caps rectangular radii at 8px, reserves blur for modal
 * overlays, and allows only the five shadow tokens — these assertions keep the
 * shell from drifting back to bespoke chrome.
 */
describe('settings shell honours the design-language chrome contract', () => {
  it.each(SHELL_FILES)('%s uses no radius above --pf-radius-lg', (file) => {
    const source = read(file);
    // rounded-xl and up, plus arbitrary values like rounded-[1.5rem].
    expect(source).not.toMatch(/rounded(-[a-z]+)?-(xl|2xl|3xl|4xl)\b/);
    expect(source).not.toMatch(/rounded(-[a-z]+)?-\[/);
  });

  it.each(SHELL_FILES)('%s declares no arbitrary shadow', (file) => {
    expect(read(file)).not.toMatch(/shadow-\[/);
  });

  it.each(SHELL_FILES)('%s uses no backdrop blur', (file) => {
    // Blur is reserved for modal overlays; on a full-height frame it costs a
    // compositing layer for every scroll frame and buys nothing.
    expect(read(file)).not.toMatch(/backdrop-blur/);
  });

  it.each(SHELL_FILES)('%s hardcodes no literal colour', (file) => {
    const source = read(file);
    expect(source).not.toMatch(/rgba?\(/);
    expect(source).not.toMatch(/#[0-9a-fA-F]{3,8}\b/);
  });

  it.each(SHELL_FILES)('%s applies no alpha modifier to a theme token', (file) => {
    // `border-pf-border/70` re-tunes a value the theme already tuned, and the
    // result inverts between Light and Dark.
    expect(read(file)).not.toMatch(/-pf-[a-z0-9-]+\/\d+/);
  });

  it('renders the shell frame at the card radius on a panel surface', () => {
    const source = read('features/settings/pages/SettingsShell.tsx');
    expect(source).toContain('rounded-md border border-pf-border bg-pf-panel');
  });

  it('carries no decorative grid, noise or scrim overlay', () => {
    const source = read('features/settings/pages/SettingsShell.tsx');
    expect(source).not.toContain('feTurbulence');
    expect(source).not.toMatch(/bg-gradient-to-[bt]\b/);
  });
});
