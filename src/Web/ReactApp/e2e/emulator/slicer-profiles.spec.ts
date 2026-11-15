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

interface HierarchyModel {
  modelId: string;
  machineProfiles: ManagedProfile[];
}

interface HierarchicalProfilesResponse {
  byHierarchy: Record<string, { models: Record<string, HierarchyModel> }>;
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

async function getMachineProfileOwnerModelId(
  page: import('@playwright/test').Page,
  machineProfileId: string,
): Promise<string | null> {
  const token = await getStoredAuthToken(page);
  expect(token, 'The authenticated emulator fixture must provide an API token').toBeTruthy();

  const response = await page.request.get(`${BASE_URL}/api/slicer/profiles/hierarchy`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  expect(response.ok(), `GET /api/slicer/profiles/hierarchy returned HTTP ${response.status()}`).toBeTruthy();
  const hierarchy = await response.json() as HierarchicalProfilesResponse;
  const profileModel = Object.values(hierarchy.byHierarchy)
    .flatMap((manufacturer) => Object.values(manufacturer.models))
    .find((model) => model.machineProfiles.some((profile) => profile.id === machineProfileId));
  if (!profileModel) {
    throw new Error(`Managed machine profile "${machineProfileId}" is missing from the profile hierarchy`);
  }

  return profileModel.modelId === machineProfileId ? null : profileModel.modelId;
}

async function selectMachineProfile(
  choices: Locator,
  importedProfileNames: string[],
  managedProfileNames: string[],
): Promise<{ name: string; alreadyImported: boolean; managedProfileExists: boolean }> {
  const profiles: Array<{
    choice: Locator;
    name: string;
    alreadyImported: boolean;
    managedProfileExists: boolean;
  }> = [];
  for (let index = 0; index < await choices.count(); index++) {
    const choice = choices.nth(index);
    const name = await choice.evaluate((element: HTMLInputElement) =>
      element.labels?.[0]?.innerText.split(/\r?\n/)[0].trim() ?? ''
    );
    if (!name) {
      continue;
    }

    const alreadyImported = importedProfileNames.includes(name);
    if (alreadyImported) {
      await expect(choice, `The already-imported machine profile "${name}" must be selected by the wizard`)
        .toBeChecked();
    }
    profiles.push({
      choice,
      name,
      alreadyImported,
      managedProfileExists: managedProfileNames.includes(name),
    });
  }

  const profile = profiles.find((candidate) => !candidate.alreadyImported && !candidate.managedProfileExists)
    ?? profiles.find((candidate) => !candidate.alreadyImported)
    ?? profiles.find((candidate) => candidate.alreadyImported);
  if (!profile) {
    throw new Error('The deployed worker has no compatible machine profile for this printer model');
  }
  if (!profile.alreadyImported && !(await profile.choice.isChecked())) {
    await profile.choice.check();
  }
  return {
    name: profile.name,
    alreadyImported: profile.alreadyImported,
    managedProfileExists: profile.managedProfileExists,
  };
}

async function selectAlreadyImportedProfile(
  choices: Locator,
  importedProfileNames: string[],
  profileType: string,
): Promise<string> {
  for (let index = 0; index < await choices.count(); index++) {
    const choice = choices.nth(index);
    const profileName = await choice.evaluate((element: HTMLInputElement) =>
      element.labels?.[0]?.innerText.split(/\r?\n/)[0].trim() ?? ''
    );
    if (!profileName || !importedProfileNames.includes(profileName)) {
      continue;
    }

    if (!(await choice.isChecked())) {
      await choice.check();
    }
    return profileName;
  }

  throw new Error(`The deployed worker has no already-imported ${profileType} profile for this printer model`);
}

function expectSameValues(actual: string[], expected: string[], description: string): void {
  expect(
    [...actual].sort(),
    `${description} must be unchanged by the profile import`,
  ).toEqual([...expected].sort());
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
  test('imports worker profiles, handles retries, and opens the managed profile library', async ({ page }) => {
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
        processProfileNames: string[];
        filamentProfileNames: string[];
      };
      await expect(page.getByRole('heading', { name: 'Select Machine Profile(s)' })).toBeVisible({ timeout: 60_000 });

      const machineChoices = page.getByRole('checkbox');
      await expect(machineChoices.first(), 'The OrcaSlicer worker must expose compatible machine profiles')
        .toBeVisible({ timeout: 60_000 });
      const { name: machineProfileName, alreadyImported: machineAlreadyImported, managedProfileExists } =
        await selectMachineProfile(
          machineChoices,
          importedNames.machineProfileNames,
          profilesBeforeImport.machineProfiles.map((profile) => profile.name),
        );
      const managedMachineProfiles = profilesBeforeImport.machineProfiles.filter(
        (profile) => profile.name === machineProfileName,
      );
      expect(managedMachineProfiles.length, `Managed profile "${machineProfileName}" inventory must match its state`)
        .toBe(managedProfileExists ? 1 : 0);
      const existingOwnerModelId = managedMachineProfiles[0]
        ? await getMachineProfileOwnerModelId(page, managedMachineProfiles[0].id)
        : null;
      if (machineAlreadyImported) {
        expect(existingOwnerModelId?.toLowerCase(), `Profile "${machineProfileName}" must be linked to this model`)
          .toBe(modelId.toLowerCase());
      }
      await page.getByRole('button', { name: 'Next', exact: true }).click();

      const filamentChoices = page.getByRole('checkbox');
      await expect(filamentChoices.first(), 'The OrcaSlicer worker must expose compatible filament profiles')
        .toBeVisible({ timeout: 60_000 });
      const filamentProfileName = await selectAlreadyImportedProfile(
        filamentChoices,
        importedNames.filamentProfileNames,
        'filament',
      );
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
      const importSummaryMatch = importResult.match(
        /^Profiles imported: (\d+) machine\(s\), 0 process\(s\), 0 filament\(s\) \([1-9]\d* skipped as duplicates\)$/
      );
      if (!importSummaryMatch) {
        throw new Error(`Unexpected import summary for "${machineProfileName}": ${importResult}`);
      }
      const importedMachineCount = Number(importSummaryMatch[1]);
      if (machineAlreadyImported) {
        expect(importedMachineCount, `Already-linked profile "${machineProfileName}" must be skipped`)
          .toBe(0);
      } else if (!managedProfileExists) {
        expect(importedMachineCount, `New profile "${machineProfileName}" must be imported`)
          .toBe(1);
      } else if (existingOwnerModelId === null) {
        expect(importedMachineCount, `Unscoped profile "${machineProfileName}" must be linked to this model`)
          .toBe(1);
      } else {
        expect(
          existingOwnerModelId.toLowerCase(),
          `Profile "${machineProfileName}" must be linked to a different model`,
        ).not.toBe(modelId.toLowerCase());
        // The import API currently reports aggregate skips; #3839 tracks per-profile outcomes.
        expect(importedMachineCount, `Profile "${machineProfileName}" linked elsewhere must be skipped`)
          .toBe(0);
      }

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
        processProfileNames: string[];
        filamentProfileNames: string[];
      };
      const expectedMachineProfileNames = machineAlreadyImported
        || importedMachineCount === 0
        ? importedNames.machineProfileNames
        : [...importedNames.machineProfileNames, machineProfileName];
      expectSameValues(importedNamesAfter.machineProfileNames, expectedMachineProfileNames, 'Machine profile links');
      expectSameValues(importedNamesAfter.processProfileNames, importedNames.processProfileNames, 'Process profile links');
      expectSameValues(importedNamesAfter.filamentProfileNames, importedNames.filamentProfileNames, 'Filament profile links');

      const profilesAfterImport = await getManagedProfiles(page);
      const newManagedProfileIds = [...getManagedProfileIds(profilesAfterImport)]
        .filter((id) => !existingProfileIds.has(id));
      if (!managedProfileExists) {
        const newMachineProfiles = profilesAfterImport.machineProfiles.filter((profile) =>
          profile.name === machineProfileName && !existingProfileIds.has(profile.id)
        );
        expect(newMachineProfiles, `New profile "${machineProfileName}" must be visible in the managed library`)
          .toHaveLength(1);
        expect(newManagedProfileIds).toEqual(newMachineProfiles.map((profile) => profile.id));
      } else {
        expect(newManagedProfileIds, 'A pre-existing profile import must not create managed rows')
          .toEqual([]);
      }

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
