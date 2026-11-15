import { test, expect, API_BASE_URL, getStoredAuthToken } from '../fixtures/emulator-setup';

interface PrinterSummary {
  id: string;
  name: string;
  modelId?: string | null;
}

async function getProfileImportModelId(page: import('@playwright/test').Page): Promise<string> {
  const token = await getStoredAuthToken(page);
  expect(token, 'The authenticated emulator fixture must provide an API token').toBeTruthy();

  const printersResponse = await page.request.get(`${API_BASE_URL}/api/printers`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  expect(printersResponse.ok(), `GET /api/printers returned HTTP ${printersResponse.status()}`).toBeTruthy();
  const printers = await printersResponse.json() as PrinterSummary[];
  const seededPrinter = printers.find((printer) => printer.name === 'Moonraker Ready');
  expect(seededPrinter, 'The deterministic profile-import printer must be seeded').toBeDefined();

  expect(seededPrinter!.modelId, 'The seeded printer summary must resolve to a catalog printer model')
    .toMatch(/^[a-f\d-]{36}$/i);
  return seededPrinter!.modelId!;
}

test.describe('Slicer profile management', () => {
  test('imports compatible worker profiles and opens the managed profile library', async ({ page }) => {
    test.setTimeout(180_000);
    const modelId = await getProfileImportModelId(page);
    await page.goto(`/profiles/import?modelId=${modelId}`);
    await expect(page.getByRole('heading', { name: 'Select Machine Profile(s)' })).toBeVisible({ timeout: 60_000 });

    const machineChoices = page.getByRole('checkbox');
    await expect(machineChoices.first(), 'The OrcaSlicer worker must expose compatible machine profiles')
      .toBeVisible({ timeout: 60_000 });
    await machineChoices.first().check();
    await page.getByRole('button', { name: 'Next', exact: true }).click();

    const filamentChoices = page.getByRole('checkbox');
    await expect(filamentChoices.first(), 'The OrcaSlicer worker must expose compatible filament profiles')
      .toBeVisible({ timeout: 60_000 });
    await filamentChoices.first().check();
    await page.getByRole('button', { name: 'Next', exact: true }).click();

    await expect(page.getByRole('heading', { name: 'Review & Import' })).toBeVisible();
    await page.getByRole('button', { name: 'Import', exact: true }).click();
    await expect(page.getByText(/^Profiles imported:/)).toBeVisible({ timeout: 90_000 });

    await page.goto('/admin/settings?scope=system&tab=slicing&sub=profiles');
    await expect(page.getByRole('heading', { name: 'Slicer Profiles' })).toBeVisible({ timeout: 60_000 });
    await expect(page.getByRole('button', { name: 'Import Profiles...' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Import Profile', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Export Orca Bundle' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Upload Custom Profile' })).toBeVisible();

    for (const tab of ['Machines', 'Processes', 'Filaments', 'My Profiles']) {
      const tabButton = page.getByRole('tab', { name: new RegExp(`^${tab}`) });
      await expect(tabButton).toBeVisible();
      await tabButton.click();
      const panelId = await tabButton.getAttribute('aria-controls');
      expect(panelId, `${tab} tab must identify its panel`).toBeTruthy();
      await expect(page.locator(`[role="tabpanel"]#${panelId}`)).toBeVisible();
    }
  });
});
