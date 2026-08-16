import { defineConfig } from 'vitest/config';
import { resolve } from 'node:path';
import react from '@vitejs/plugin-react';
import tsconfigPaths from 'vite-tsconfig-paths';
export default defineConfig({
  plugins: [react(), tsconfigPaths()],
  resolve: {
    alias: [
      { find: '@', replacement: resolve(__dirname, 'src') }
    ]
  },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    // #1028: a ~1-in-5 intermittent failure went undiagnosed because the run
    // that caught it was filtered to summary lines and the failing test's name
    // scrolled past. The JSON reporter writes the full result set to disk on
    // every run, so the next occurrence is diagnosable however the console
    // output is piped or truncated. `default` is kept so interactive runs look
    // unchanged. The output file is gitignored.
    reporters: ['default', 'json'],
    outputFile: { json: './test-results/vitest-results.json' },

    // Exclude e2e tests - they use Playwright and must be run separately with npx playwright test.
    // `e2e/**/*.spec.ts` (Playwright's own convention, already covered by the blanket
    // `**/*.spec.ts` rule below) is excluded explicitly for clarity, but plain `e2e/**/*.test.ts`
    // files are intentionally NOT excluded — see `e2e/fixtures/emulator-setup.unit.test.ts`,
    // a Vitest-only unit test for fixture logic that needs no browser/live API and is ignored
    // by Playwright's own runner via `testIgnore` in playwright.config.ts.
    exclude: [
      '**/node_modules/**',
      '**/dist/**',
      '**/e2e/**/*.spec.ts',
      '**/*.spec.ts'
    ],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'lcov'],
      reportsDirectory: 'coverage',
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        'src/**/__tests__/**',
        'src/test/**',
        'src/**/index.ts',
        'src/**/types.ts'
      ],
      thresholds: {
        lines: 5,
        functions: 5,
        branches: 5,
        statements: 5
      }
    }
  }
});
