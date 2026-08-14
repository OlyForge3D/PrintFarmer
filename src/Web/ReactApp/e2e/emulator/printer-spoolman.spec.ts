import { dismissTourIfVisible, getStoredAuthToken, API_BASE_URL } from '../fixtures/emulator-setup';
import { test, expect, MOONRAKER_PRINTERS, getPrinterCardByName } from '../fixtures/moonraker';

/**
 * Printer Spoolman Presentation E2E Tests — Moonraker emulator-backed.
 *
 * Spoolman is an independent integration boundary (see `e2e/README.md` and
 * `docs/MOONRAKER_EMULATOR_VALIDATION.md`, "Application and discovery
 * integration") — the printer card only renders a Spool/Spools section when
 * the application's own Spoolman integration is ready or the printer
 * already has spool assignment data. Rather than accepting either outcome
 * unconditionally, this suite reads the real ground truth from the API
 * first and then asserts the UI matches it exactly — never a bare "either
 * is fine" OR.
 */

interface SpoolmanHealth {
  success?: boolean;
}

interface SpoolmanConfig {
  baseUrl?: string | null;
}

test.describe('Printer Spoolman Presentation — Moonraker', () => {
  test.describe.configure({ mode: 'serial' });

  test.beforeEach(async ({ page }) => {
    await page.goto('/printers');
    await page.waitForLoadState('networkidle');
    await dismissTourIfVisible(page);
  });

  test('the Spool section presence matches real Spoolman readiness', async ({ page }) => {
    const token = await getStoredAuthToken(page);
    expect(token).toBeTruthy();
    const authHeaders = { Authorization: `Bearer ${token}` };

    const configRes = await page.request.get(`${API_BASE_URL}/api/spoolman/config`, { headers: authHeaders });
    expect([200, 204]).toContain(configRes.status());
    const config = configRes.status() === 200
      ? (await configRes.json()) as SpoolmanConfig
      : { baseUrl: null };
    const configured = !!config.baseUrl?.trim();

    let healthy = false;
    if (configured) {
      const healthRes = await page.request.get(`${API_BASE_URL}/api/spoolman/health`, { headers: authHeaders });
      expect(healthRes.ok()).toBeTruthy();
      const health = (await healthRes.json()) as SpoolmanHealth;
      healthy = !!health.success;
    }
    const spoolmanReady = configured && healthy;

    const printersRes = await page.request.get(`${API_BASE_URL}/api/printers`, { headers: authHeaders });
    expect(printersRes.ok()).toBeTruthy();
    const printers = (await printersRes.json()) as Array<{
      name: string;
      spoolInfo?: { hasActiveSpool?: boolean };
      currentSpoolId?: string | null;
    }>;

    for (const name of Object.values(MOONRAKER_PRINTERS)) {
      const printer = printers.find((p) => p.name === name);
      expect(printer, `expected seeded printer "${name}" in the API response`).toBeTruthy();

      const hasSpoolData = spoolmanReady || !!printer!.spoolInfo || !!printer!.currentSpoolId;
      const card = getPrinterCardByName(page, name);
      await expect(card).toBeVisible();

      const spoolHeading = card.getByText(/^Spools?$/, { exact: true });
      if (hasSpoolData) {
        await expect(
          spoolHeading,
          `${name}: Spoolman is ready (or the printer already has spool data), so the card must show its Spool section`
        ).toBeVisible();
      } else {
        await expect(
          spoolHeading,
          `${name}: Spoolman is not ready and the printer has no spool data, so no Spool section should render`
        ).toHaveCount(0);
      }
    }
  });
});
