import { dismissTourIfVisible } from '../fixtures/emulator-setup';
import {
  createMoonrakerControl,
  expect,
  getMoonrakerEmulatorUrl,
  getPrinterCardByName,
  MOONRAKER_PRINTERS,
  test,
} from '../fixtures/moonraker';

test.describe('Printer Spoolman presentation — Moonraker', () => {
  test.describe.configure({ mode: 'serial' });

  test.beforeEach(async ({ page, request }) => {
    await createMoonrakerControl(request).resetAll();
    await page.goto('/printers');
    await expect(getPrinterCardByName(page, MOONRAKER_PRINTERS.ready)).toBeVisible({ timeout: 15_000 });
    await dismissTourIfVisible(page);
  });

  test('reachable printers show the exact seeded active spool from Moonraker', async ({ page }) => {
    for (const name of [
      MOONRAKER_PRINTERS.ready,
      MOONRAKER_PRINTERS.printing,
      MOONRAKER_PRINTERS.paused,
      MOONRAKER_PRINTERS.shutdown,
    ]) {
      const card = getPrinterCardByName(page, name);
      await expect(card).toBeVisible();
      await expect(card).toContainText('Generic PLA', { timeout: 15_000 });
      await expect(card).toContainText('PLA | 1.00kg');
    }

    await expect(
      getPrinterCardByName(page, MOONRAKER_PRINTERS.offline)
        .getByText(/Generic PLA/)
    ).toHaveCount(0);
  });

  test('active spool mutation and reset are deterministic at the consumed Moonraker boundary', async ({ request }) => {
    const baseUrl = getMoonrakerEmulatorUrl('ready');

    const setResponse = await request.post(`${baseUrl}/server/spoolman/spool_id`, {
      data: { spool_id: 2 },
    });
    expect(setResponse.ok()).toBeTruthy();

    const changedResponse = await request.get(`${baseUrl}/server/spoolman/spool_id`);
    expect(changedResponse.ok()).toBeTruthy();
    await expect(changedResponse.json()).resolves.toMatchObject({
      result: { spool_id: 2 },
    });

    await createMoonrakerControl(request).reset('ready');
    const resetResponse = await request.get(`${baseUrl}/server/spoolman/spool_id`);
    expect(resetResponse.ok()).toBeTruthy();
    await expect(resetResponse.json()).resolves.toMatchObject({
      result: { spool_id: 1 },
    });
  });
});
