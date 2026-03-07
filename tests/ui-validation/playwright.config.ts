import { defineConfig, devices } from '@playwright/test';

/**
 * PrintFarmer UI Validation Tests
 *
 * Standalone Playwright configuration that spins up the .NET API server
 * and the React dev server, waits for both to be healthy, runs the
 * validation suite, and tears everything down.
 *
 * Run:  npm test            (headless)
 *       npm run test:headed (visible browser)
 *       npm run test:ui     (Playwright UI mode)
 */
export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: 1,
  reporter: [['html', { outputFolder: 'playwright-report' }], ['list']],
  timeout: 30_000,
  expect: { timeout: 10_000 },

  use: {
    baseURL: 'http://localhost:3000',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    video: 'on-first-retry',
  },

  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],

  /* The global setup/teardown manages the API + React servers. */
  globalSetup: './global-setup.ts',
  globalTeardown: './global-teardown.ts',
});
