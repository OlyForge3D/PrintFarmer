/// <reference types="vitest" />
import { defineConfig } from 'vitest/config';
import { resolve } from 'node:path';
import react from '@vitejs/plugin-react';
import tsconfigPaths from 'vite-tsconfig-paths';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { publicIdentity } from './public-release-identity.mjs';
import { readReleaseIdentity } from './src/common/utils/releaseIdentity.ts';

const fullCommitShaPattern = /^[0-9a-f]{40}$/i;

export function resolveGitHash(command: 'build' | 'serve') {
  const injectedGitHash = process.env.VITE_GIT_SHA || process.env.GIT_SHA;
  if (injectedGitHash) {
    if (fullCommitShaPattern.test(injectedGitHash)) {
      return injectedGitHash.toLowerCase();
    }
    if (command === 'build') {
      throw new Error(
        'Production builds require VITE_GIT_SHA or GIT_SHA to be a full 40-character commit SHA.',
      );
    }
  }

  try {
    const repositoryGitHash = execFileSync('git', ['rev-parse', 'HEAD'], {
      stdio: ['ignore', 'pipe', 'ignore'],
    }).toString().trim();
    if (fullCommitShaPattern.test(repositoryGitHash)) {
      return repositoryGitHash.toLowerCase();
    }
  } catch {
    // Development servers may run from source archives without Git metadata.
  }

  if (command === 'build') {
    throw new Error(
      'Production builds require a full commit SHA from VITE_GIT_SHA, GIT_SHA, or Git.',
    );
  }
  return 'dev';
}

// Emit dist/version.json at build time so the deployed frontend commit is queryable
// (served by nginx at /version.json), mirroring the backend /api/system/version endpoints.
export function frontendVersionMetadata(
  gitHash: string,
  buildTime: string,
  releaseIdentity?: Record<string, unknown>,
  inventoryIdentity: ReturnType<typeof readReleaseIdentity> = null,
) {
  if (releaseIdentity && releaseIdentity.sourceCommit !== gitHash) {
    throw new Error('Frontend release identity does not match the build source SHA.');
  }
  const projection = releaseIdentity ? publicIdentity(releaseIdentity) : {};
  if (inventoryIdentity) {
    for (const [field, value] of Object.entries(projection)) {
      if (field in inventoryIdentity && inventoryIdentity[field as keyof typeof inventoryIdentity] !== value) {
        throw new Error(`Frontend release identity inputs disagree on ${field}.`);
      }
    }
  }
  return { service: 'frontend', commit: gitHash,
    buildTime: typeof releaseIdentity?.buildTime === 'string' ? releaseIdentity.buildTime : buildTime,
    ...projection,
    ...(inventoryIdentity ? { releaseIdentity: inventoryIdentity } : {}) };
}

function emitVersionJson(gitHash: string, buildTime: string) {
  const inventoryIdentity = readReleaseIdentity(process.env.PRINTFARMER_RELEASE_IDENTITY, gitHash);
  let outDir = 'dist';
  return {
    name: 'printfarmer-version-json',
    apply: 'build' as const,
    configResolved(config: { build: { outDir: string } }) {
      outDir = config.build.outDir;
    },
    closeBundle() {
      const identityPath = resolve('public', 'release-identity.json');
      const releaseIdentity: Record<string, unknown> | undefined = existsSync(identityPath)
        ? JSON.parse(readFileSync(identityPath, 'utf8')) as Record<string, unknown>
        : undefined;
      mkdirSync(outDir, { recursive: true });
      const metadata = JSON.stringify(frontendVersionMetadata(gitHash, buildTime, releaseIdentity, inventoryIdentity), null, 2);
      writeFileSync(resolve(outDir, 'version.json'), metadata);
      if (releaseIdentity) {
        // Vite copies public assets verbatim; sanitize that copy as well.
        writeFileSync(resolve(outDir, 'release-identity.json'), metadata);
      }
      const serviceWorkerPath = resolve(outDir, 'sw.js');
      const serviceWorker = readFileSync(serviceWorkerPath, 'utf8')
        .replaceAll('__PRINTFARMER_BUILD_TIME__', buildTime)
        .replaceAll('__PRINTFARMER_GIT_HASH__', gitHash);
      writeFileSync(serviceWorkerPath, serviceWorker);
    },
  };
}

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
//
// NOTE: this is expressed via `output.codeSplitting.groups` (rolldown's
// modern replacement for the deprecated `manualChunks` function form), not
// `manualChunks` itself. `manualChunks(id)` returning a single, ad-hoc
// chunk name per id is NOT equivalent here: rolldown treats every distinct
// name returned from one `manualChunks` function as sub-groups of ONE
// overarching group, and small/heavily-shared modules (e.g. `clsx`, the
// bare `react-dom` package) were empirically observed to get silently
// re-merged back into whichever other manual chunk already pulled them in
// (`vendor-charts`) instead of materializing their own chunk — reproducing
// #2390 even with a dedicated entry. Declaring each package family as its
// own independent `{ name, test, priority }` group below (the officially
// documented multi-group pattern) does not have that failure mode: each
// group is matched purely by `test`, with no cross-group fallback merging.
// Priority mirrors this array's order (first = highest) even though the
// `test` patterns below don't currently overlap, to keep the "first match
// wins" intent explicit and safe against future additions.
const MANUAL_CHUNK_GROUPS: Array<[string, string[]]> = [
  ['routing', ['react-router']],
  // Keep only framework-agnostic Three modules here. Fiber and Drei
  // follow the lazy 3D consumers so their React dependencies cannot
  // turn this shared core into an eager app-shell chunk.
  ['three', ['three', 'three-stdlib']],
  // `clsx` and the bare `react-dom` package are used directly by the
  // eager main entry (e.g. `clsx` for class-name composition across the
  // app shell, `createPortal` from `react-dom` for Modal-style portals)
  // AND internally by `recharts` (its Legend/Tooltip/portal layers do
  // `import { createPortal } from 'react-dom'`, and it uses `clsx`
  // throughout). Neither module had a manual-chunk entry of its own, so
  // rolldown-vite's default algorithm physically homed both inside the
  // `vendor-charts` chunk below, then had to add a genuine static
  // `import` edge from the main entry back into that ~400 kB chunk just
  // to reach these two small, otherwise-unrelated bindings — forcing
  // every route to fetch/parse all of `vendor-charts` (#2390). Same
  // technique as `vendor-otel-api` above: give the shared piece its own
  // stable micro-chunk so the heavy chunk below no longer needs to be
  // referenced from the entry at all.
  ['vendor-clsx', ['clsx']],
  ['vendor-react-dom', ['react-dom']],
  // Charting library — used across analytics/statistics/maintenance
  // dashboards; ~400 kB minified. Splitting keeps it out of the
  // main entry chunk (loaded only when a chart-using route mounts).
  ['vendor-charts', ['recharts']],
  // Real-time transport. Isolated so the initial bundle does not
  // pay for the SignalR client until a hub is actually contacted.
  ['vendor-signalr', ['@microsoft/signalr']],
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
  ['vendor-otel-api', ['@opentelemetry/api']],
  // OpenTelemetry web SDK — instrumentation stack used by the
  // telemetry provider. main.tsx only dynamically imports
  // telemetry/config.ts when VITE_OTEL_EXPORTER_OTLP_ENDPOINT is
  // set, so this chunk is excluded from the critical path (and from
  // index.html's modulepreload list) in the default, unconfigured
  // build.
  ['vendor-otel', [
    '@opentelemetry/semantic-conventions',
    '@opentelemetry/exporter-trace-otlp-http',
    '@opentelemetry/auto-instrumentations-web',
    '@opentelemetry/instrumentation-fetch',
    '@opentelemetry/sdk-trace-web',
    '@opentelemetry/resources',
    '@opentelemetry/instrumentation-user-interaction',
    '@opentelemetry/instrumentation-xml-http-request',
  ]],
  // React Query — used by nearly every page. Splitting it out
  // shrinks per-route chunks. react-query-devtools is left in the
  // main entry chunk because bundling it with react-query creates
  // a circular chunk cycle via recharts. react-virtual is used
  // only transitively (via drei), so it does not need its own
  // chunk.
  ['vendor-tanstack', ['@tanstack/react-query']],
  // Icon libraries: pulled from many pages. Grouping icons keeps
  // the tree-shakeable icon sets off the main entry.
  ['vendor-icons', ['@mdi/js', 'lucide-react', '@heroicons/react/24/outline', '@heroicons/react/24/solid']],
  // Date utilities — pulled in from many pages.
  ['vendor-datetime', ['date-fns']],
];

const createConfig = (gitHash: string, buildTime: string) => ({
  plugins: [react(), tsconfigPaths(), emitVersionJson(gitHash, buildTime)],
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
      // The heavy OTel SDK chunk (`vendor-otel`) is only reached via an
      // awaited dynamic import() from main.tsx (see comment there), never
      // statically. Under rolldown-vite the bundler's default preload-dep
      // resolution still emits an eager <link rel="modulepreload"> for it
      // in index.html because it shares the manual-chunk boundary keyed by
      // package path — strip it here so it stays fetched lazily, exactly
      // like the original Rollup-era behavior this app depends on (#1238).
      // `vendor-otel-api` (the tiny no-op shim used eagerly from main.tsx)
      // is intentionally left alone.
      resolveDependencies: (_filename, deps) =>
        deps.filter((dep) => !/vendor-otel-(?!api)/i.test(dep)),
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
        // See the MANUAL_CHUNK_GROUPS chunk-splitting policy above. Each
        // group is independent (matched only by its own `test`), so a
        // module shared by two groups' consumers (e.g. `clsx`/`react-dom`
        // needed by both the entry and `vendor-charts`) reliably gets its
        // own dedicated chunk instead of being folded into whichever
        // group's modules happen to import it first (#2390).
        codeSplitting: {
          groups: MANUAL_CHUNK_GROUPS.map(([name, packages], index) => ({
            name,
            test: (id: string) => packages.some((pkg) => id.includes(`/node_modules/${pkg}/`)),
            priority: MANUAL_CHUNK_GROUPS.length - index,
          })),
        },
      }
    }
  },
  define: {
    __BUILD_TIME__: JSON.stringify(buildTime),
    __GIT_HASH__: JSON.stringify(gitHash),
    __RELEASE_IDENTITY__: JSON.stringify(readReleaseIdentity(process.env.PRINTFARMER_RELEASE_IDENTITY, gitHash)),
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

export default defineConfig(({ command }) =>
  createConfig(resolveGitHash(command), new Date().toISOString()));
