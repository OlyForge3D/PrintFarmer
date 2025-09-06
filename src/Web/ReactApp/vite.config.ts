/// <reference types="vitest" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'path';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: [
      { find: /^@\/(.*)/, replacement: path.resolve(__dirname, 'src/$1') },
      { find: '@', replacement: path.resolve(__dirname, 'src') },
    ],
  },
  server: {
    port: 3000,
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
  build: {
    sourcemap: true,
    outDir: 'dist',
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    globals: true,
    alias: [
      { find: /^@\/(.*)/, replacement: path.resolve(__dirname, 'src/$1') },
      { find: '@', replacement: path.resolve(__dirname, 'src') },
    ],
  },
});
