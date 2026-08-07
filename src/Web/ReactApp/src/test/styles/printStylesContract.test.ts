import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const SOURCE_ROOT = resolve(__dirname, '../..');
const indexSource = readFileSync(resolve(SOURCE_ROOT, 'index.css'), 'utf8');
const printSource = readFileSync(resolve(SOURCE_ROOT, 'styles/print.css'), 'utf8');
const lightThemeSource = readFileSync(resolve(SOURCE_ROOT, 'design-system/themes/light.css'), 'utf8');
const source = (path: string): string => readFileSync(resolve(SOURCE_ROOT, path), 'utf8');

const importPosition = (path: string): number =>
  indexSource.indexOf(`@import '${path}';`);

describe('print stylesheet contract (#1126)', () => {
  it('imports the unlayered print contract after every selectable theme', () => {
    const printPosition = importPosition('./styles/print.css');
    const themeImports = [
      'base',
      'dark',
      'light',
      'matrix',
      'blueprint',
      'ratos',
      'voron',
      'farm',
      'forge',
    ].map((theme) => importPosition(`./design-system/themes/${theme}.css`));

    expect(printPosition).toBeGreaterThan(-1);
    expect(indexSource).not.toContain("@import './styles/print.css' layer(");
    expect(themeImports.every((position) => position >= 0 && position < printPosition)).toBe(true);
  });

  it('defines a light semantic print palette without important declarations', () => {
    expect(printSource).toContain('@media print');
    expect(printSource).toMatch(/:root\s*\{[\s\S]*color-scheme:\s*light/);
    expect(printSource).not.toContain('!important');

    for (const token of [
      'bg-0',
      'text-primary',
      'text-muted',
      'border',
      'success',
      'success-bg',
      'warning',
      'warning-bg',
      'error',
      'error-bg',
      'info',
      'info-bg',
    ]) {
      expect(printSource).toMatch(new RegExp(`--pf-${token}:\\s*#[0-9a-f]{6}`, 'i'));
    }
  });

  it('overrides the complete selectable-theme color token contract', () => {
    const themeTokens = [...lightThemeSource.matchAll(/(--pf-[\w-]+):\s*[^;]+;/g)]
      .map((match) => match[1])
      .filter((token) => token !== '--pf-theme-name');
    const printTokens = new Set(
      [...printSource.matchAll(/(--pf-[\w-]+):\s*[^;]+;/g)].map((match) => match[1]),
    );

    expect(themeTokens.filter((token) => !printTokens.has(token))).toEqual([]);
  });

  it('uses explicit chrome, content, and pagination selectors', () => {
    expect(printSource).toContain('[data-print-hidden]');
    expect(printSource).toContain('[data-sonner-toaster]');
    expect(printSource).toContain('html .tsqd-open-btn-container');
    expect(printSource).toContain('html .driver-popover');
    expect(printSource).toContain('html .driver-overlay');
    expect(printSource).toContain('[data-main-content]');
    expect(printSource).toContain('[data-pf-card]');
    expect(printSource).toMatch(/#root \*[\s\S]*text-shadow:\s*none/);
    expect(printSource).toMatch(/tr,[\s\S]*break-inside:\s*avoid/);
    expect(printSource).not.toMatch(/button[^{]*\{[^}]*display:\s*none/);
  });

  it('marks only stable application chrome and card roots', () => {
    for (const path of [
      'common/components/Layout.tsx',
      'common/components/InstallBanner.tsx',
      'common/components/FloatingActionButton.tsx',
      'common/components/ThemeSwitcher.tsx',
      'common/components/admin/AdminSaveBar.tsx',
      'features/settings/components/CommandPalette.tsx',
    ]) {
      expect(source(path), path).toContain('data-print-hidden');
    }

    for (const path of [
      'common/components/ui/Card.tsx',
      'features/printers/components/CompactPrinterCard.tsx',
      'features/printers/components/DetailedPrinterCard.tsx',
    ]) {
      expect(source(path), path).toContain('data-pf-card');
    }
  });
});
