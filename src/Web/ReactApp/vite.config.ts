/// <reference types="vitest" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tsconfigPaths from 'vite-tsconfig-paths';
import { execSync } from 'node:child_process';

let gitHash = 'dev';
try {
  gitHash = execSync('git rev-parse --short HEAD').toString().trim();
} catch { /* ignore: git not available */ }

export default defineConfig({
  plugins: [react(), tsconfigPaths()],
  resolve: {
    // alias removed, handled by vite-tsconfig-paths
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
      output: {
        manualChunks: {
          react: ['react', 'react-dom'],
          routing: ['react-router-dom'],
          vendor_misc: ['axios', '@tanstack/react-query'],
          three: ['three', '@react-three/fiber', '@react-three/drei', 'three-stdlib'],
          viewers: [
            // Heavy 3D viewer components (ensure paths resolved at build time)
            '@/components/3d/ModelViewer3D',
            '@/components/3d/GCodeViewer3D'
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
