/// <reference types="vitest" />
import { defineConfig } from 'vitest/config';
import { resolve } from 'node:path';
import react from '@vitejs/plugin-react';
import tsconfigPaths from 'vite-tsconfig-paths';
import { execSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';

const buildTime = new Date().toISOString();

// Prefer the live git short SHA. In container builds the .git directory is absent, so
// fall back to the VITE_GIT_SHA/GIT_SHA build arg injected by the Docker frontend stage.
let gitHash = process.env.VITE_GIT_SHA || process.env.GIT_SHA || 'dev';
try {
  gitHash = execSync('git rev-parse --short HEAD').toString().trim();
} catch { /* ignore: git not available (e.g. Docker build) — keep injected VITE_GIT_SHA */ }

// Chunk-splitting policy (see full rationale in the `manualChunks` comment below):
//   package name -> manual chunk name. Rolldown (Vite 8's bundler) requires
//   `manualChunks` to be a function rather than the object form Rollup accepted,
//   so this lookup table is resolved to a chunk name per-module via `getPackageName`.
const manualChunkPackages: Record<string, string> = {
  'react-router': 'routing',
  // Keep only framework-agnostic Three modules here. Fiber and Drei
  // follow the lazy 3D consumers so their React dependencies cannot
  // turn this shared core into an eager app-shell chunk.
  three: 'three',
  'three-stdlib': 'three',
  recharts: 'vendor-charts',
  '@microsoft/signalr': 'vendor-signalr',
  '@opentelemetry/api': 'vendor-otel-api',
  '@opentelemetry/semantic-conventions': 'vendor-otel',
  '@opentelemetry/exporter-trace-otlp-http': 'vendor-otel',
  '@opentelemetry/auto-instrumentations-web': 'vendor-otel',
  '@opentelemetry/instrumentation-fetch': 'vendor-otel',
  '@opentelemetry/sdk-trace-web': 'vendor-otel',
  '@opentelemetry/resources': 'vendor-otel',
  '@opentelemetry/instrumentation-user-interaction': 'vendor-otel',
  '@opentelemetry/instrumentation-xml-http-request': 'vendor-otel',
  '@tanstack/react-query': 'vendor-tanstack',
  '@mdi/js': 'vendor-icons',
  'lucide-react': 'vendor-icons',
  '@heroicons/react': 'vendor-icons',
  'date-fns': 'vendor-datetime',
};

// Resolve the npm package name (including scope) that a module id was
// imported from, e.g. `.../node_modules/@mdi/js/index.js` -> `@mdi/js`.
function getPackageName(id: string): string | undefined {
  const match = id.match(/node_modules\/((?:@[^/]+\/)?[^/]+)\//);
  return match?.[1];
}

function manualChunks(id: string): string | undefined {
  const pkg = getPackageName(id);
  return pkg ? manualChunkPackages[pkg] : undefined;
}

// Emit dist/version.json at build time so the deployed frontend commit is queryable
// (served by nginx at /version.json), mirroring the backend /api/system/version endpoints.
function emitVersionJson() {
  let outDir = 'dist';
  return {
    name: 'printfarmer-version-json',
    apply: 'build' as const,
    configResolved(config: { build: { outDir: string } }) {
      outDir = config.build.outDir;
    },
    closeBundle() {
      try {
        mkdirSync(outDir, { recursive: true });
        writeFileSync(
          resolve(outDir, 'version.json'),
          JSON.stringify({ service: 'frontend', commit: gitHash, buildTime }, null, 2),
        );
      } catch { /* non-fatal: version.json is best-effort */ }
    },
  };
}

export default defineConfig({
  plugins: [react(), tsconfigPaths(), emitVersionJson()],
  logLevel: 'info', // Only show info and above; suppress debug/warnings
  resolve: {
    // Keep an explicit fallback alias mapping for environments where
    // the vite-tsconfig-paths plugin may not run (tests/CI). This
    // mirrors the tsconfig path mapping for '@/...' -> './src/...'
    alias: [
      { find: '@', replacement: resolve(__dirname, 'src') }
    ]
  },
  optimizeDeps: {
    include: [
      '@opentelemetry/api',
      '@opentelemetry/semantic-conventions',
      '@opentelemetry/exporter-trace-otlp-http',
      '@opentelemetry/auto-instrumentations-web',
      '@opentelemetry/instrumentation-fetch',
      '@opentelemetry/sdk-trace-web',
      '@opentelemetry/resources',
      '@opentelemetry/instrumentation-user-interaction',
      '@opentelemetry/instrumentation-xml-http-request'
    ]
  },
  server: {
    host: '0.0.0.0',  // Listen on all network interfaces
    port: 3000,
    hmr: {
      host: undefined,  // Let client determine host from window.location
      protocol: 'ws',
      port: 3001, // Use a different port for HMR WebSocket to avoid conflicts
    },
    proxy: {
      '/api': {
        target: 'http://localhost:5245',
        changeOrigin: true,
      },
      '/hubs': {
        target: 'http://localhost:5245',
        changeOrigin: true,
        ws: true,
      },
      '/grafana': {
        target: 'http://localhost:80',
        changeOrigin: true,
      },
      '/jaeger': {
        target: 'http://localhost:80',
        changeOrigin: true,
      },
    },
  },
  preview: {
    port: 3000,
  },
  build: {
    sourcemap: true,
    outDir: 'dist',
    chunkSizeWarningLimit: 1200,
    modulePreload: {
      // Rolldown (Vite 8's bundler) eagerly injects a <link rel="modulepreload">
      // into index.html for the heavy OTel SDK chunk whenever main.tsx's dynamic
      // `import('./telemetry/config')` is statically known to always execute
      // (i.e. VITE_OTEL_EXPORTER_OTLP_ENDPOINT is set at build time). That
      // defeats the deferred-load contract in main.tsx (see #1238): telemetry
      // must load via a runtime dynamic import awaited after first paint, never
      // block it via an eager <head> preload. Filter it out of the `html`
      // preload list specifically; the runtime `js` preload (used by the
      // dynamic import itself, right before it resolves) is left untouched.
      resolveDependencies: (_filename, deps, { hostType }) =>
        hostType === 'html' ? deps.filter((dep) => !/vendor-otel-(?!api)/i.test(dep)) : deps,
    },
    rollupOptions: {
      onwarn(warning, defaultHandler) {
        // Suppress upstream annotation warnings from @microsoft/signalr which are safe
        if (warning.code === 'INVALID_ANNOTATION' && typeof warning.message === 'string' && warning.message.includes('@microsoft/signalr')) {
          return;
        }
        defaultHandler(warning);
      },
      // NOTE: Do NOT mark dependencies as external for a Vite SPA
      // External modules expect to be provided by the runtime environment
      // In a browser SPA, we need all dependencies bundled
      output: {
        // Chunk-splitting policy (full per-package rationale lives in
        // `manualChunkPackages` above):
        //   1. Keep the routing chunk small and independent so the router
        //      shell can render before the rest of the app is parsed.
        //   2. Keep three.js core shared while allowing Drei to follow its
        //      lazy 3D consumers. Manually owning Drei can absorb ReactDOM and
        //      make the otherwise route-specific 3D dependency eager.
        //   3. Split heavy vendor libraries out of the main `index-*.js`
        //      bundle so it stays under the 1200 kB warning threshold.
        //   4. NEVER raise `chunkSizeWarningLimit`. If a new heavy library
        //      is added, add it here (or lazy-load its consumers) instead
        //      of silencing the warning.
        manualChunks,
      }
    }
  },
  define: {
    __BUILD_TIME__: JSON.stringify(buildTime),
    __GIT_HASH__: JSON.stringify(gitHash),
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    globals: true,
    // vite-tsconfig-paths will handle aliases for tests too
    // Exclude e2e tests - they use Playwright and must be run separately
    exclude: [
      '**/node_modules/**',
      '**/dist/**',
      '**/e2e/**',
      '**/*.spec.ts'  // Playwright convention is .spec.ts
    ],
  },
});
