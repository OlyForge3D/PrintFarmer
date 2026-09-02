import { execFile } from 'node:child_process';
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { afterAll, describe, expect, it } from 'vitest';

// #2390 — recharts (`vendor-charts`, ~440 kB minified) is only used by
// components reachable through React.lazy()-wrapped Maintenance/Analytics/
// Statistics routes, never from the eager main entry. Despite that,
// rolldown-vite's manual-chunk grouping used to fold small, heavily-shared
// modules (`clsx`, the bare `react-dom` package — both used directly by the
// eager entry AND internally by recharts) into the `vendor-charts` chunk
// itself, forcing a genuine static import edge from the entry back into
// that heavy chunk on every route. This build-manifest assertion locks the
// fix in (dedicated `vendor-clsx`/`vendor-react-dom` groups in
// `output.codeSplitting.groups`, see vite.config.ts) so a future regression
// fails CI instead of silently reintroducing the eager load.

const TEST_DIR = dirname(fileURLToPath(import.meta.url));
const REACT_APP_ROOT = resolve(TEST_DIR, '../../..');
const VITE_CLI = resolve(REACT_APP_ROOT, 'node_modules/vite/bin/vite.js');
const execFileAsync = promisify(execFile);

interface BuiltBundle {
  indexHtml: string;
  assetNames: string[];
  readAsset: (fileName: string) => string;
}

async function build(outDir: string): Promise<BuiltBundle> {
  await execFileAsync(process.execPath, [
    VITE_CLI,
    'build',
    '--outDir',
    outDir,
    '--emptyOutDir',
    '--logLevel',
    'error',
  ], {
    cwd: REACT_APP_ROOT,
    maxBuffer: 20 * 1024 * 1024,
    windowsHide: true,
    // Force a real production build regardless of the ambient shell
    // environment Vitest itself happens to run under.
    env: { ...process.env, NODE_ENV: 'production' },
  });

  const assetsDir = resolve(outDir, 'assets');
  const assetNames = readdirSync(assetsDir);
  const indexHtml = readFileSync(resolve(outDir, 'index.html'), 'utf8');

  return {
    indexHtml,
    assetNames,
    readAsset: (fileName: string) => readFileSync(resolve(assetsDir, fileName), 'utf8'),
  };
}

describe('recharts deferred from the main entry chunk (#2390)', () => {
  const outDir = mkdtempSync(join(tmpdir(), 'printfarmer-vendor-charts-'));
  // Build once and share it across assertions — a real `vite build` costs
  // several seconds; running it once keeps this file's total runtime sane
  // while still exercising the exact same pristine-build path CI does.
  const bundlePromise = build(outDir);

  afterAll(() => {
    rmSync(outDir, { recursive: true, force: true });
  });

  it('never modulepreloads vendor-charts from index.html', async () => {
    const bundle = await bundlePromise;

    expect(
      bundle.indexHtml,
      'a default build must not eagerly modulepreload vendor-charts; only Maintenance/Analytics/Statistics routes use recharts, and they reach it via a lazy import',
    ).not.toMatch(/modulepreload[^>]*vendor-charts/i);
  }, 120_000);

  it('never statically imports a vendor-charts binding from the main entry chunk', async () => {
    const bundle = await bundlePromise;

    const entryChunkName = bundle.assetNames.find((name) => /^index-.*\.js$/.test(name));
    expect(entryChunkName, 'expected exactly one main entry chunk (index-*.js)').toBeDefined();

    const entrySource = bundle.readAsset(entryChunkName!);
    // The entry legitimately references "vendor-charts" once, inside the
    // rolldown/vite dynamic-import dependency map (`__vite__mapDeps`) used
    // to preload chunks when a *lazy* route dynamically imports it. What
    // must never appear is a real static `import ... from "./vendor-charts`
    // binding, which would mean the entry itself depends on the chunk.
    expect(
      entrySource,
      'main entry chunk must not statically import a binding from vendor-charts',
    ).not.toMatch(/\bimport\s*\{[^}]*\}\s*from\s*["']\.\/vendor-charts[^"']*["']/);
  }, 120_000);

  it('still emits a non-empty vendor-charts chunk reachable from the chart-using routes', async () => {
    const bundle = await bundlePromise;

    const chartsChunks = bundle.assetNames.filter((name) => /^vendor-charts-.*\.js$/.test(name));
    expect(chartsChunks.length, 'expected the vendor-charts chunk to still be emitted').toBeGreaterThan(0);

    const hasNonEmptyChunk = chartsChunks.some((chunkName) => bundle.readAsset(chunkName).trim().length > 0);
    expect(hasNonEmptyChunk, 'vendor-charts must still contain live recharts code, not be tree-shaken away').toBe(true);

    // At least one lazy route chunk must reference vendor-charts, proving
    // it is still reachable (just no longer eager) for the routes that need it.
    const referencesCharts = bundle.assetNames.some((name) => {
      if (name === chartsChunks[0] || /^index-.*\.js$/.test(name)) return false;
      if (!name.endsWith('.js')) return false;
      return bundle.readAsset(name).includes('vendor-charts');
    });
    expect(referencesCharts, 'expected at least one non-entry route chunk to still reference vendor-charts').toBe(true);
  }, 120_000);
});
