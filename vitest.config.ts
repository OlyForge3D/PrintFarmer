import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  test: {
    globals: true,
    environment: 'jsdom',
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
      lines: 60,
      functions: 55,
      branches: 50,
      statements: 60
    }
  }
});
