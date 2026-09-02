import { execFile } from 'node:child_process';
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

// Matches any real static import/export-from binding of a vendor-charts
// chunk: named (`import{x}from"./vendor-charts-h.js"`), namespace
// (`import*as y from"./vendor-charts-h.js"`), re-export forms
// (`export{x}from"./vendor-charts-h.js"`, `export*from"./vendor-charts-h.js"`),
// and bare side-effect imports (`import"./vendor-charts-h.js"`). Deliberately
// does NOT match a dynamic `import("./vendor-charts-h.js")` call (no `(`
// immediately follows `import` before the quote in either alternative) or a
// plain string literal inside the `__vite__mapDeps` dependency-map array
// (which contains no `from`/bare-`import` keyword at all).
const STATIC_VENDOR_CHARTS_IMPORT = /\bfrom\s*["']\.\/vendor-charts[^"']*["']|\bimport\s*["']\.\/vendor-charts[^"']*["']/;

function stripSourceMappingComment(source: string): string {
  return source.replace(/\/\/# sourceMappingURL=.*$/gm, '').trim();
}

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
  let outDir: string;
  let bundle: BuiltBundle;

  // Build once, in a lifecycle hook (not at module-evaluation time), and
  // share it across assertions — a real `vite build` costs several
  // seconds; running it once keeps this file's total runtime sane while
  // still exercising the exact same pristine-build path CI does. Starting
  // it inside `beforeAll` (rather than eagerly when the describe body
  // runs) means a build failure surfaces as a normal failed hook instead
  // of a promise rejection that could go unhandled before any `it()` gets
  // a chance to await it, and gives it its own hook timeout independent of
  // the individual assertions.
  beforeAll(async () => {
    outDir = mkdtempSync(join(tmpdir(), 'printfarmer-vendor-charts-'));
    bundle = await build(outDir);
  }, 120_000);

  afterAll(() => {
    if (outDir) rmSync(outDir, { recursive: true, force: true });
  });

  it('never modulepreloads vendor-charts from index.html', () => {
    expect(
      bundle.indexHtml,
      'a default build must not eagerly modulepreload vendor-charts; only Maintenance/Analytics/Statistics routes use recharts, and they reach it via a lazy import',
    ).not.toMatch(/modulepreload[^>]*vendor-charts/i);
  });

  it('never statically imports a vendor-charts binding from the main entry chunk', () => {
    const entryChunkNames = bundle.assetNames.filter((name) => /^index-.*\.js$/.test(name));
    expect(entryChunkNames.length, 'expected exactly one main entry chunk (index-*.js)').toBe(1);

    const entrySource = bundle.readAsset(entryChunkNames[0]);
    // The entry legitimately references "vendor-charts" once, inside the
    // rolldown/vite dynamic-import dependency map (`__vite__mapDeps`) used
    // to preload chunks when a *lazy* route dynamically imports it — that
    // reference is a bare string in an array literal, with no `from` or
    // bare-`import` keyword attached, so it does not match
    // STATIC_VENDOR_CHARTS_IMPORT. What must never appear is a real static
    // import/export-from binding, which would mean the entry itself
    // depends on the chunk.
    expect(
      entrySource,
      'main entry chunk must not statically import a binding from vendor-charts',
    ).not.toMatch(STATIC_VENDOR_CHARTS_IMPORT);
  });

  it('still emits a non-empty vendor-charts chunk reachable from the chart-using routes', () => {
    const chartsChunks = bundle.assetNames.filter((name) => /^vendor-charts-.*\.js$/.test(name));
    expect(chartsChunks.length, 'expected the vendor-charts chunk to still be emitted').toBeGreaterThan(0);

    const hasNonEmptyChunk = chartsChunks.some(
      (chunkName) => stripSourceMappingComment(bundle.readAsset(chunkName)).length > 0,
    );
    expect(hasNonEmptyChunk, 'vendor-charts must still contain live recharts code, not be tree-shaken away').toBe(true);

    // At least one lazy route chunk must reference vendor-charts, proving
    // it is still reachable (just no longer eager) for the routes that need it.
    const chartsChunkNames = new Set(chartsChunks);
    const referencesCharts = bundle.assetNames.some((name) => {
      if (chartsChunkNames.has(name) || /^index-.*\.js$/.test(name)) return false;
      if (!name.endsWith('.js')) return false;
      return bundle.readAsset(name).includes('vendor-charts');
    });
    expect(referencesCharts, 'expected at least one non-entry route chunk to still reference vendor-charts').toBe(true);
  });
});
