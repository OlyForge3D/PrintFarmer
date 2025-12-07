/// <reference types="vitest" />
import { defineConfig } from 'vite';
import { resolve } from 'node:path';
import { existsSync } from 'node:fs';
import react from '@vitejs/plugin-react';
import tsconfigPaths from 'vite-tsconfig-paths';
import { execSync } from 'node:child_process';

let gitHash = 'dev';
try {
  gitHash = execSync('git rev-parse --short HEAD').toString().trim();
} catch { /* ignore: git not available */ }

// Resolve OrcaSlicer UI path - check Docker path first, then local dev path
const orcaSlicerUiPath = existsSync('/Slicers/Farm.Slicers.OrcaSlicer.v2_3_1/ui')
  ? '/Slicers/Farm.Slicers.OrcaSlicer.v2_3_1/ui'
  : resolve(__dirname, '../../Slicers/Farm.Slicers.OrcaSlicer.v2_3_1/ui');

export default defineConfig({
  plugins: [react(), tsconfigPaths()],
  logLevel: 'info', // Only show info and above; suppress debug/warnings
  resolve: {
    // Keep an explicit fallback alias mapping for environments where
    // the vite-tsconfig-paths plugin may not run (tests/CI). This
    // mirrors the tsconfig path mapping for '@/...' -> './src/...'
    alias: [
      { find: '@', replacement: resolve(__dirname, 'src') },
      // OrcaSlicer workspace package - uses dynamic path resolution
      // In Docker: /Slicers/... (absolute path copied by Dockerfile)
      // In local dev: ../../Slicers/... (relative path from /repo/src/Web/ReactApp)
      { find: '@farm/slicers-orcaslicer-v2_3_1', replacement: orcaSlicerUiPath },
      // Ensure all peerDependencies from OrcaSlicer workspace package resolve from root node_modules
      // npm symlinks these but in Docker build context, Rollup needs explicit paths
      { find: /^react\/jsx-runtime$/, replacement: resolve(__dirname, '../../../node_modules/react/jsx-runtime.js') },
      { find: /^react$/, replacement: resolve(__dirname, '../../../node_modules/react') },
      { find: /^react-dom$/, replacement: resolve(__dirname, '../../../node_modules/react-dom') },
      { find: /^axios$/, replacement: resolve(__dirname, '../../../node_modules/axios') },
      { find: /^@tanstack\/react-query$/, replacement: resolve(__dirname, '../../../node_modules/@tanstack/react-query') },
      { find: /^lucide-react$/, replacement: resolve(__dirname, '../../../node_modules/lucide-react') }
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
    port: 3000,
    hmr: {
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
          routing: ['react-router-dom'],
          three: ['three', '@react-three/fiber', '@react-three/drei', 'three-stdlib'],
          viewers: [
            // Heavy 3D viewer components (ensure paths resolved at build time)
            'src/components/3d/ModelViewer3D.tsx',
            'src/components/3d/GCodeViewer3D.tsx'
          ]
        }
      }
    }
  },
  define: {
    __BUILD_TIME__: JSON.stringify(new Date().toISOString()),
    __GIT_HASH__: JSON.stringify(gitHash),
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    globals: true,
    // vite-tsconfig-paths will handle aliases for tests too
  },
});
