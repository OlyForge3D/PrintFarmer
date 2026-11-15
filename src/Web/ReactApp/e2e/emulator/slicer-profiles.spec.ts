import type { Locator } from '@playwright/test';
import { test, expect, API_BASE_URL, BASE_URL, getStoredAuthToken } from '../fixtures/emulator-setup';

interface PrinterSummary {
  id: string;
  name: string;
  modelId?: string | null;
}

interface ManagedProfile {
  id: string;
  name: string;
}

interface ManagedProfilesResponse {
  machineProfiles: ManagedProfile[];
  processProfiles: ManagedProfile[];
  filamentProfiles: ManagedProfile[];
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

async function getManagedProfiles(page: import('@playwright/test').Page): Promise<ManagedProfilesResponse> {
  const token = await getStoredAuthToken(page);
  expect(token, 'The authenticated emulator fixture must provide an API token').toBeTruthy();

  const response = await page.request.get(`${BASE_URL}/api/slicer/profiles/extended`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  expect(response.ok(), `GET /api/slicer/profiles/extended returned HTTP ${response.status()}`).toBeTruthy();
  return await response.json() as ManagedProfilesResponse;
}

async function selectFirstProfile(
  choices: Locator,
  profileType: string,
  requireUnimported: boolean,
): Promise<string> {
  for (let index = 0; index < await choices.count(); index++) {
    const choice = choices.nth(index);
    const profile = await choice.evaluate((element: HTMLInputElement) => {
      const label = element.labels?.[0];
      const labelText = label?.innerText ?? '';
      return {
        name: labelText.split(/\r?\n/)[0].trim(),
        isImported: labelText.includes('Imported') || Boolean(label?.querySelector('[title="Already imported"]')),
      };
    });
    if (!profile.name || (requireUnimported && (profile.isImported || await choice.isChecked()))) {
      continue;
    }

    if (!(await choice.isChecked())) {
      await choice.check();
    }
    return profile.name;
  }

  throw new Error(`The deployed worker has no ${requireUnimported ? 'unimported ' : ''}${profileType} profile for this printer model`);
}

function getManagedProfileIds(profiles: ManagedProfilesResponse): Set<string> {
  return new Set([
    ...profiles.machineProfiles,
    ...profiles.processProfiles,
    ...profiles.filamentProfiles,
  ].map((profile) => profile.id));
}

async function removeNewManagedProfiles(
  page: import('@playwright/test').Page,
  existingProfileIds: ReadonlySet<string>,
): Promise<void> {
  const profiles = await getManagedProfiles(page);
  const newProfileIds = [
    ...profiles.machineProfiles,
    ...profiles.processProfiles,
    ...profiles.filamentProfiles,
  ]
    .map((profile) => profile.id)
    .filter((id) => !existingProfileIds.has(id));
  if (newProfileIds.length === 0) {
    return;
  }

  const token = await getStoredAuthToken(page);
  expect(token, 'The authenticated emulator fixture must provide an API token').toBeTruthy();
  const response = await page.request.post(`${BASE_URL}/api/slicer/profiles/bulk-delete`, {
    headers: { Authorization: `Bearer ${token}` },
    data: newProfileIds,
  });
  expect(response.ok(), `Deleting imported test profiles returned HTTP ${response.status()}`).toBeTruthy();
  const result = await response.json() as { totalDeleted: number };
  expect(result.totalDeleted, 'Every profile created by this test must be removed').toBe(newProfileIds.length);
}

test.describe('Slicer profile management', () => {
  test('imports compatible worker profiles and opens the managed profile library', async ({ page }) => {
    test.setTimeout(180_000);
    const modelId = await getProfileImportModelId(page);
    const profilesBeforeImport = await getManagedProfiles(page);
    const existingProfileIds = getManagedProfileIds(profilesBeforeImport);
    let testBodyFailed = false;
    let testBodyError: unknown;
    try {
      const importedNamesResponsePromise = page.waitForResponse((response) =>
        new URL(response.url()).pathname.endsWith(`/api/slicer/profiles/imported-names/${modelId}`)
      );
      await page.goto(`/profiles/import?modelId=${modelId}`);
      const importedNamesResponse = await importedNamesResponsePromise;
      expect(importedNamesResponse.ok(), `GET imported profile names returned HTTP ${importedNamesResponse.status()}`)
        .toBeTruthy();
      const importedNames = await importedNamesResponse.json() as {
        machineProfileNames: string[];
        filamentProfileNames: string[];
      };
      await expect(page.getByRole('heading', { name: 'Select Machine Profile(s)' })).toBeVisible({ timeout: 60_000 });
      if (importedNames.machineProfileNames.length > 0) {
        await expect(page.getByText('Imported', { exact: true }).first()).toBeVisible();
      }

      const machineChoices = page.getByRole('checkbox');
      await expect(machineChoices.first(), 'The OrcaSlicer worker must expose compatible machine profiles')
        .toBeVisible({ timeout: 60_000 });
      const machineProfileName = await selectFirstProfile(machineChoices, 'machine', true);
      expect(importedNames.machineProfileNames).not.toContain(machineProfileName);
      await page.getByRole('button', { name: 'Next', exact: true }).click();

      const filamentChoices = page.getByRole('checkbox');
      await expect(filamentChoices.first(), 'The OrcaSlicer worker must expose compatible filament profiles')
        .toBeVisible({ timeout: 60_000 });
      const filamentProfileName = await selectFirstProfile(filamentChoices, 'filament', false);
      await page.getByRole('button', { name: 'Next', exact: true }).click();

      await expect(page.getByRole('heading', { name: 'Review & Import' })).toBeVisible();
      const importRequest = page.waitForRequest((request) =>
        request.method() === 'POST'
        && new URL(request.url()).pathname.endsWith(`/api/slicer/profiles/import-selected-for-model/${modelId}`)
      );
      await page.getByRole('button', { name: 'Import', exact: true }).click();
      const request = await importRequest;
      const payload = JSON.parse(request.postData() ?? '{}') as {
        selectedMachineProfiles?: unknown;
        selectedFilamentProfiles?: unknown;
      };
      expect(payload.selectedMachineProfiles).toContain(machineProfileName);
      expect(payload.selectedFilamentProfiles).toContain(filamentProfileName);

      const importSummary = page.getByText(/^Profiles imported:/);
      await expect(importSummary).toBeVisible({ timeout: 90_000 });
      const importResult = await importSummary.textContent() ?? '';
      expect(importResult).toMatch(/Profiles imported: [1-9]\d* machine\(s\)/);

      const token = await getStoredAuthToken(page);
      expect(token, 'The authenticated emulator fixture must provide an API token').toBeTruthy();
      const importedNamesAfterResponse = await page.request.get(
        `${BASE_URL}/api/slicer/profiles/imported-names/${modelId}`,
        { headers: { Authorization: `Bearer ${token}` } },
      );
      expect(
        importedNamesAfterResponse.ok(),
        `GET imported profile names returned HTTP ${importedNamesAfterResponse.status()}`
      ).toBeTruthy();
      const importedNamesAfter = await importedNamesAfterResponse.json() as {
        machineProfileNames: string[];
        filamentProfileNames: string[];
      };
      expect(
        importedNamesAfter.machineProfileNames,
        `The imported machine profile "${machineProfileName}" must be linked to the printer model`
      ).toContain(machineProfileName);
      expect(
        importedNamesAfter.filamentProfileNames,
        `The selected filament profile "${filamentProfileName}" must be available in the managed library`
      ).toContain(filamentProfileName);

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
    } catch (error) {
      testBodyFailed = true;
      testBodyError = error;
    }

    let cleanupError: unknown;
    try {
      await removeNewManagedProfiles(page, existingProfileIds);
    } catch (error) {
      cleanupError = error;
    }

    if (testBodyFailed) {
      if (cleanupError) {
        await test.info().attach('profile-cleanup-error.txt', {
          body: String(cleanupError),
          contentType: 'text/plain',
        });
      }
      throw testBodyError;
    }
    if (cleanupError) {
      throw cleanupError;
    }
  });
});
