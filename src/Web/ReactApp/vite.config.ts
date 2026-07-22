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
        manualChunks: {
          routing: ['react-router'],
          three: ['three', '@react-three/fiber', '@react-three/drei', 'three-stdlib']
        }
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
