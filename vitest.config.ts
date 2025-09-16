import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react()],
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: ['src/Web/ReactApp/src/test/setup.ts'],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'lcov'],
      reportsDirectory: './src/Web/ReactApp/coverage',
      include: ['src/Web/ReactApp/src/**/*.{ts,tsx}'],
      exclude: [
        'src/Web/ReactApp/src/**/__tests__/**',
        'src/Web/ReactApp/src/test/**',
        'src/Web/ReactApp/src/**/index.ts',
        'src/Web/ReactApp/src/**/types.ts'
      ],
      lines: 10,
      functions: 10,
      branches: 10,
      statements: 10
    }
  }
});
