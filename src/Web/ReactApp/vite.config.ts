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
        // Chunk-splitting policy:
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
        manualChunks: {
          routing: ['react-router'],
          // Keep only framework-agnostic Three modules here. Fiber and Drei
          // follow the lazy 3D consumers so their React dependencies cannot
          // turn this shared core into an eager app-shell chunk.
          three: ['three', 'three-stdlib'],
          // Charting library — used across analytics/statistics/maintenance
          // dashboards; ~400 kB minified. Splitting keeps it out of the
          // main entry chunk (loaded only when a chart-using route mounts).
          'vendor-charts': ['recharts'],
          // Real-time transport. Isolated so the initial bundle does not
          // pay for the SignalR client until a hub is actually contacted.
          'vendor-signalr': ['@microsoft/signalr'],
          // File/archive utilities intentionally follow their lazy consumers.
          // Combining PDF, HTML capture, ZIP, and 3MF parsing in one manual
          // chunk makes every interaction pay for all of them and can pull
          // Vite's preload helper into that otherwise optional chunk.
          // `@opentelemetry/api` gets its OWN chunk, separate from the
          // heavy SDK below. unifiedLogging.ts imports it eagerly (it's
          // just the no-op-by-default tracer interface, used from the main
          // app entry regardless of whether telemetry is configured). If it
          // were grouped into `vendor-otel`, Rollup would merge that whole
          // manual-chunk group into one file and have the eager entry
          // statically import from it — dragging the (otherwise lazy) SDK
          // chunk back onto the critical path. Keeping it isolated ensures
          // only this tiny API shim loads eagerly.
          'vendor-otel-api': ['@opentelemetry/api'],
          // OpenTelemetry web SDK — instrumentation stack used by the
          // telemetry provider. main.tsx only dynamically imports
          // telemetry/config.ts when VITE_OTEL_EXPORTER_OTLP_ENDPOINT is
          // set, so this chunk is excluded from the critical path (and from
          // index.html's modulepreload list) in the default, unconfigured
          // build.
          'vendor-otel': [
            '@opentelemetry/semantic-conventions',
            '@opentelemetry/exporter-trace-otlp-http',
            '@opentelemetry/auto-instrumentations-web',
            '@opentelemetry/instrumentation-fetch',
            '@opentelemetry/sdk-trace-web',
            '@opentelemetry/resources',
            '@opentelemetry/instrumentation-user-interaction',
            '@opentelemetry/instrumentation-xml-http-request',
          ],
          // React Query — used by nearly every page. Splitting it out
          // shrinks per-route chunks. react-query-devtools is left in the
          // main entry chunk because bundling it with react-query creates
          // a circular chunk cycle via recharts. react-virtual is used
          // only transitively (via drei), so it does not need its own
          // chunk.
          'vendor-tanstack': ['@tanstack/react-query'],
          // Icon libraries: pulled from many pages. Grouping icons keeps
          // the tree-shakeable icon sets off the main entry.
          'vendor-icons': ['@mdi/js', 'lucide-react', '@heroicons/react/24/outline', '@heroicons/react/24/solid'],
          // Date utilities — pulled in from many pages.
          'vendor-datetime': ['date-fns'],
        },
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
