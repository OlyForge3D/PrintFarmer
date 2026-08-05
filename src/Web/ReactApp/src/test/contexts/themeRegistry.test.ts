/**
 * Theme registry consistency.
 *
 * PrintFarmer previously kept five independent theme lists: the `Theme` union,
 * the `toggleTheme` cycle, `ThemeToggle`'s options, `ThemeSwitcher`'s
 * `THEME_OPTIONS`, and the `VALID` array in the boot script in `index.html`.
 * They drifted. Three themes (`forge`, `github-dark`, `printfarmer-dark`) had
 * stylesheets and picker entries but no design-system tokens, and rendered as
 * `dark`; three real themes (`ratos`, `voron`, `farm`) were missing from the
 * boot script and so flashed as `dark` on every load until React hydrated.
 *
 * None of that was visible to the type checker, because each list was
 * independently well-typed. These tests exist to make drift fail loudly.
 */
import { describe, it, expect } from 'vitest';
import fs from 'node:fs';
import path from 'node:path';
import { SELECTABLE_THEMES } from '@/contexts/ThemeContext';

const appRoot = path.resolve(__dirname, '../..', '..');
const read = (p: string) => fs.readFileSync(path.join(appRoot, p), 'utf8');

describe('theme registry', () => {
  it('has a design-system stylesheet for every selectable theme', () => {
    const dir = path.join(appRoot, 'src/design-system/themes');
    const files = fs.readdirSync(dir);
    for (const theme of SELECTABLE_THEMES) {
      expect(files, `missing design-system/themes/${theme}.css`).toContain(`${theme}.css`);
    }
  });

  it('declares every selectable theme in the index.css import list', () => {
    const indexCss = read('src/index.css');
    for (const theme of SELECTABLE_THEMES) {
      expect(indexCss, `${theme}.css is never imported`).toContain(`themes/${theme}.css`);
    }
  });

  it('lists every selectable theme in the index.html boot script', () => {
    // A theme missing here still works, but flashes `dark` until hydration —
    // which is exactly the bug this guards against, and is invisible in tests
    // that only exercise React.
    const html = read('index.html');
    const match = html.match(/var VALID = \[([^\]]*)\]/);
    expect(match, 'boot script VALID array not found in index.html').toBeTruthy();
    const valid = match![1].split(',').map((s) => s.trim().replace(/['"]/g, '')).filter(Boolean);
    expect([...valid].sort()).toEqual([...SELECTABLE_THEMES].sort());
  });

  it('offers every selectable theme in the settings ThemeSwitcher', () => {
    const src = read('src/common/components/ThemeSwitcher.tsx');
    const ids = [...src.matchAll(/^\s*id: '([a-z-]+)',$/gm)].map((m) => m[1]);
    expect([...ids].sort()).toEqual([...SELECTABLE_THEMES].sort());
  });

  it('does not reference retired themes anywhere in src', () => {
    // `forge`/`github-dark`/`printfarmer-dark` had stylesheets that could never
    // win the cascade: they lived in layer(base) while the design-system themes
    // are unlayered. Selecting one rendered byte-identical `dark`.
    const retired = ['github-dark', 'printfarmer-dark', 'forge'];
    // Files allowed to name them, and why. Anything else is a resurrection.
    const allowed = new Set([
      'src/contexts/ThemeContext.tsx',            // declares the migration map
      'src/test/contexts/ThemeContext.test.tsx',  // tests the migration
      'src/test/contexts/themeRegistry.test.ts',  // this file
    ]);
    const offenders: string[] = [];

    const walk = (dir: string) => {
      for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) {
          if (!/node_modules|dist/.test(entry.name)) walk(full);
          continue;
        }
        if (!/\.(ts|tsx|css)$/.test(entry.name)) continue;
        const rel = path.relative(appRoot, full).replace(/\\/g, '/');
        if (allowed.has(rel)) continue;
        const text = fs.readFileSync(full, 'utf8');
        for (const name of retired) {
          if (text.includes(`'${name}'`) || text.includes(`"${name}"`)) {
            offenders.push(`${rel} -> ${name}`);
          }
        }
      }
    };
    walk(path.join(appRoot, 'src'));

    expect(offenders).toEqual([]);
  });

  it('keeps every selectable theme visually distinct', () => {
    // The failure mode that hid the retired themes for so long was that they
    // resolved to a palette identical to `dark`. Comparing declared token
    // values catches a future theme file that is a copy of another.
    const palettes = new Map<string, string>();
    for (const theme of SELECTABLE_THEMES) {
      const css = read(`src/design-system/themes/${theme}.css`);
      const keys = ['--pf-bg-0', '--pf-bg-1', '--pf-accent', '--pf-text-primary'];
      const values = keys.map((k) => {
        const m = css.match(new RegExp(`${k}\\s*:\\s*([^;]+);`));
        expect(m, `${theme}.css does not declare ${k}`).toBeTruthy();
        return m![1].trim();
      });
      palettes.set(theme, values.join('|'));
    }
    const seen = new Map<string, string>();
    for (const [theme, palette] of palettes) {
      const clash = seen.get(palette);
      expect(clash, `${theme} has the same core palette as ${clash}`).toBeUndefined();
      seen.set(palette, theme);
    }
  });
});
