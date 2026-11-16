import type { Locator, Page } from '@playwright/test';
import { API_BASE_URL, dismissTourIfVisible, getStoredAuthToken } from '../fixtures/emulator-setup';
import {
  createMoonrakerControl,
  expect,
  MOONRAKER_PRINTERS,
  openCollapsedPrinterDetailsSidebar,
  test,
} from '../fixtures/moonraker';

async function configureMmuTopology(page: Page, printerId: string, multiMaterial: boolean) {
  const token = await getStoredAuthToken(page);
  expect(token, 'MMU fixture configuration requires authentication').toBeTruthy();
  const headers = { Authorization: `Bearer ${token}` };
  const response = await page.request.get(`${API_BASE_URL}/api/printers/${printerId}/details`, { headers });
  expect(response.ok()).toBe(true);
  const details = await response.json() as { rowVersion: string };
  expect(details.rowVersion, 'MMU configuration requires the reviewed printer revision').toBeTruthy();
  const update = await page.request.put(`${API_BASE_URL}/api/printers/${printerId}`, {
    headers: { ...headers, 'If-Match': `"${details.rowVersion}"` },
    data: { multiMaterial },
  });
  expect(update.ok(), `Failed to configure persisted MMU topology: ${update.status()}`).toBe(true);
  const readback = await page.request.get(`${API_BASE_URL}/api/printers/${printerId}/details`, { headers });
  expect(readback.ok()).toBe(true);
  const saved = await readback.json() as {
    capabilities: { multiMaterial: boolean };
    toolheads: Array<{ index: number; toolheadType: string }>;
  };
  expect(saved.capabilities.multiMaterial).toBe(multiMaterial);
  expect(saved.toolheads.filter(toolhead => toolhead.toolheadType === 'MmuGate').map(toolhead => toolhead.index))
    .toEqual(multiMaterial ? [1, 2, 3, 4] : []);
}

async function getMmuControls(sidebar: Locator): Promise<Locator> {
  const toggle = sidebar.getByRole('button', { name: /^(Expand|Collapse) Materials/ });
  await expect(toggle).toBeVisible({ timeout: 15_000 });
  if (await toggle.getAttribute('aria-expanded') !== 'true') {
    await toggle.click();
  }

  const panelId = await toggle.getAttribute('aria-controls');
  expect(panelId).toBeTruthy();
  const controls = sidebar.locator(`[id="${panelId}"]`).getByTestId('mmu-control-box');
  await expect(controls).toBeVisible();
  await controls.getByTestId('filament-summary').click();
  await expect(controls.getByRole('group', { name: 'Filament slots', exact: true })).toBeVisible();
  return controls;
}

test.describe('Printer MMU controls — Moonraker', () => {
  test.describe.configure({ mode: 'serial' });

  test.beforeEach(async ({ page, request, moonrakerSeedReady }) => {
    const control = createMoonrakerControl(request);
    await control.reset('ready');
    await control.setMmuMode('ready', 'HappyHare');
    await configureMmuTopology(page, moonrakerSeedReady.get(MOONRAKER_PRINTERS.ready)!.id, true);

    await page.goto('/printers');
    await page.waitForLoadState('domcontentloaded');
    await dismissTourIfVisible(page);
  });

  test.afterEach(async ({ page, request, moonrakerSeedReady }) => {
    const control = createMoonrakerControl(request);
    try {
      await configureMmuTopology(page, moonrakerSeedReady.get(MOONRAKER_PRINTERS.ready)!.id, false);
    } finally {
      try {
        await control.setMmuMode('ready', 'None');
      } finally {
        await control.reset('ready');
      }
    }
  });

  test('Happy Hare gates render and load/eject through the real backend', async ({ page }) => {
    const sidebar = await openCollapsedPrinterDetailsSidebar(page, MOONRAKER_PRINTERS.ready);
    const controls = await getMmuControls(sidebar);

    const plaGate = controls.getByTestId('mmu-gate-0');
    await expect(plaGate).toHaveAccessibleName(/^1A: PLA,/);
    await expect(plaGate).toHaveAttribute('data-active', 'loaded');

    const petgGate = controls.getByTestId('mmu-gate-1');
    await expect(petgGate).toHaveAccessibleName(/^1B: PETG/);
    await expect(petgGate).toBeVisible();
    await petgGate.click();
    await expect(petgGate).toHaveAttribute('aria-expanded', 'true');
    await expect(controls.getByText('Spool #102', { exact: true })).toBeVisible();

    await expect(controls.getByRole('status').filter({ hasText: /Saved gate layout does not match/ })).toHaveCount(0);
    await expect(controls.getByRole('button', { name: 'Change spool in slot 1B', exact: true })).toBeEnabled();

    await controls.getByRole('button', { name: 'Load slot 1B', exact: true }).click();
    await expect(petgGate).toHaveAttribute('data-active', 'loaded', { timeout: 15_000 });
    await expect(plaGate).not.toHaveAttribute('data-active', 'loaded');

    await controls.getByRole('button', { name: 'Unload slot 1B', exact: true }).click();
    await expect(petgGate).not.toHaveAttribute('data-active', 'loaded', { timeout: 15_000 });
    await expect(controls.locator('[data-active="loaded"]')).toHaveCount(0);
  });

  test('AFC lanes render and load through the real backend', async ({ page, request }) => {
    await createMoonrakerControl(request).setMmuMode('ready', 'Afc');
    await page.reload();

    const sidebar = await openCollapsedPrinterDetailsSidebar(page, MOONRAKER_PRINTERS.ready);
    const controls = await getMmuControls(sidebar);
    const plaGate = controls.getByTestId('mmu-gate-0');
    await expect(plaGate).toHaveAttribute('data-active', 'loaded');

    const lane = controls.getByTestId('mmu-gate-1');
    await expect(lane).toHaveAccessibleName(/^1B: PETG/);
    await lane.click();
    await controls.getByRole('button', { name: 'Load slot 1B', exact: true }).click();
    await expect(lane).toHaveAttribute('data-active', 'loaded', { timeout: 15_000 });
    await expect(plaGate).not.toHaveAttribute('data-active', 'loaded');
  });

  test('Qidibox slots render and unload through the real backend', async ({ page, request }) => {
    await createMoonrakerControl(request).setMmuMode('ready', 'Qidibox');
    await page.reload();

    const sidebar = await openCollapsedPrinterDetailsSidebar(page, MOONRAKER_PRINTERS.ready);
    const controls = await getMmuControls(sidebar);
    const slot = controls.getByTestId('mmu-gate-0');
    await expect(slot).toHaveAccessibleName(/^1A: PLA,/);
    await expect(slot).toHaveAttribute('data-active', 'loaded');
    await slot.click();
    await controls.getByRole('button', { name: 'Unload slot 1A', exact: true }).click();
    await expect(controls).toBeVisible();
    await expect(slot).not.toHaveAttribute('data-active', 'loaded', { timeout: 15_000 });
    await expect(controls.locator('[data-active="loaded"]')).toHaveCount(0);
  });

  test('Snapmaker U1 physical toolheads render as four material slots', async ({ page, request }) => {
    await createMoonrakerControl(request).setMmuMode('ready', 'SnapmakerU1');
    await page.reload();

    const sidebar = await openCollapsedPrinterDetailsSidebar(page, MOONRAKER_PRINTERS.ready);
    const materials = sidebar.getByTestId('material-loadout');
    await expect(materials).toBeVisible({ timeout: 15_000 });
    await materials.getByTestId('filament-summary').click();
    const toolheads = materials.getByRole('group', { name: 'Filament tools', exact: true });
    await expect(toolheads.getByRole('button')).toHaveCount(4);
    await expect(toolheads.getByRole('button', { name: /^T0: PLA/ })).toBeVisible();
    await expect(toolheads.getByRole('button', { name: /^T1: PETG/ })).toBeVisible();
    await expect(toolheads.getByRole('button', { name: /^T2: Assign spool/ })).toBeVisible();
    await expect(toolheads.getByRole('button', { name: /^T3: Assign spool/ })).toBeVisible();
  });
});
