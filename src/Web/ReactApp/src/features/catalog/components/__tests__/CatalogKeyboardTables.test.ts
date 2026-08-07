import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const catalogTables = [
  ['ExtrudersCatalog.tsx', 'Extruders catalog'],
  ['FilamentsCatalog.tsx', 'Filaments catalog'],
  ['HotendsCatalog.tsx', 'Hotends catalog'],
  ['NozzlesCatalog.tsx', 'Nozzles catalog'],
  ['PrinterModelsCatalog.tsx', 'Printer models catalog'],
  ['ToolheadsCatalog.tsx', 'Toolheads catalog'],
] as const;

describe('catalog keyboard tables', () => {
  it.each(catalogTables)('%s opts into the labeled keyboard-navigation contract', (fileName, label) => {
    const source = readFileSync(resolve(__dirname, '..', fileName), 'utf8');
    const dataTable = source.match(/<DataTable\s[\s\S]*?\/>/)?.[0];

    expect(dataTable).toBeDefined();
    expect(dataTable).toMatch(/\bkeyboardNavigation\b/);
    expect(dataTable).toContain(`ariaLabel="${label}"`);
  });
});
