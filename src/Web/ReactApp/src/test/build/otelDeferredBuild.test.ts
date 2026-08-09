import { execFile } from 'node:child_process';
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { afterEach, describe, expect, it } from 'vitest';

// #1238 — the OpenTelemetry web SDK must never block first paint. main.tsx
// only dynamically imports telemetry/config.ts when
// VITE_OTEL_EXPORTER_OTLP_ENDPOINT is configured, so a default (unconfigured)
// production build must ship with the heavy OTel SDK graph either absent
// from the build or unreferenced by index.html's eager modulepreload list.
// This build-manifest assertion locks that win in so a future regression
// (e.g. a new eager import reachable from main.tsx) fails CI instead of
// silently reintroducing the first-paint block.

const TEST_DIR = dirname(fileURLToPath(import.meta.url));
const REACT_APP_ROOT = resolve(TEST_DIR, '../../..');
const VITE_CLI = resolve(REACT_APP_ROOT, 'node_modules/vite/bin/vite.js');
const execFileAsync = promisify(execFile);

interface BuiltBundle {
  indexHtml: string;
  assetNames: string[];
  readAsset: (fileName: string) => string;
}

async function build(outDir: string, env: NodeJS.ProcessEnv): Promise<BuiltBundle> {
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
    env,
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

describe('OpenTelemetry deferred from first paint (#1238)', () => {
  const outDirs: string[] = [];

  afterEach(() => {
    while (outDirs.length > 0) {
      const outDir = outDirs.pop();
      if (outDir) rmSync(outDir, { recursive: true, force: true });
    }
  });

  it('never modulepreloads the OTel SDK chunk in a default (unconfigured) build', async () => {
    const outDir = mkdtempSync(join(tmpdir(), 'printfarmer-otel-default-'));
    outDirs.push(outDir);

    // Explicitly unset so this assertion holds regardless of the ambient
    // shell environment the test happens to run in. Force NODE_ENV to
    // production so `import.meta.env.DEV` resolves the same way it does in
    // a real production deploy (Vitest's own process may run under
    // NODE_ENV=test, which would otherwise leak into this child build and
    // make Vite treat it as a dev build).
    const env = { ...process.env, NODE_ENV: 'production' };
    delete env.VITE_OTEL_EXPORTER_OTLP_ENDPOINT;

    const bundle = await build(outDir, env);

    expect(
      bundle.indexHtml,
      'default build must not eagerly modulepreload the heavy OTel SDK chunk (vendor-otel-api, the tiny no-op API shim, is fine)',
    ).not.toMatch(/modulepreload[^>]*vendor-otel-(?!api-)/i);

    const heavyOtelChunks = bundle.assetNames.filter(
      (name) => /^vendor-otel-(?!api-).*\.js$/.test(name),
    );
    for (const chunkName of heavyOtelChunks) {
      const contents = bundle.readAsset(chunkName)
        .replace(/\/\/# sourceMappingURL=.*$/gm, '')
        .trim();
      expect(
        contents.length,
        `${chunkName} must be tree-shaken to nothing but a sourcemap comment in a default build, not ship live SDK code`,
      ).toBe(0);
    }
  }, 120_000);

  it('still loads and initializes the OTel SDK when an OTLP endpoint is configured', async () => {
    const outDir = mkdtempSync(join(tmpdir(), 'printfarmer-otel-enabled-'));
    outDirs.push(outDir);

    const env = {
      ...process.env,
      NODE_ENV: 'production',
      VITE_OTEL_EXPORTER_OTLP_ENDPOINT: 'http://localhost:4318/v1/traces',
    };

    const bundle = await build(outDir, env);

    const heavyOtelChunks = bundle.assetNames.filter(
      (name) => /^vendor-otel-(?!api-).*\.js$/.test(name),
    );
    expect(
      heavyOtelChunks.length,
      'configured build must emit the OTel SDK chunk',
    ).toBeGreaterThan(0);

    const hasNonEmptyChunk = heavyOtelChunks.some(
      (chunkName) => bundle.readAsset(chunkName).trim().length > 0,
    );
    expect(
      hasNonEmptyChunk,
      'configured build must include live SDK code (registerInstrumentations/WebTracerProvider), not a tree-shaken stub',
    ).toBe(true);

    // Even in a configured build, the SDK chunk must still not be eagerly
    // modulepreloaded — it is only ever reached via the awaited dynamic
    // import in main.tsx. #1364 tracked a Vite 8/rolldown regression where
    // this eager preload briefly reappeared; it has since been resolved
    // upstream of this test file, and this assertion locks the fix in.
    expect(
      bundle.indexHtml,
      'even a configured build must not eagerly modulepreload the OTel SDK chunk',
    ).not.toMatch(/modulepreload[^>]*vendor-otel-(?!api)/i);
  }, 120_000);
});
