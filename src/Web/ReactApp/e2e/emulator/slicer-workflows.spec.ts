import { randomUUID } from 'node:crypto';
import { test, expect, API_BASE_URL, BASE_URL, getStoredAuthToken } from '../fixtures/emulator-setup';

interface PrinterSummary {
  id: string;
  name: string;
  isOnline?: boolean;
}

interface ModelUploadResponse {
  id: string;
  url: string;
}

function cubeStl(modelKey: string): Buffer {
  const faces = [
    [0, 2, 1], [0, 3, 2], [4, 5, 6], [4, 6, 7],
    [0, 1, 5], [0, 5, 4], [1, 2, 6], [1, 6, 5],
    [2, 3, 7], [2, 7, 6], [3, 0, 4], [3, 4, 7],
  ];
  const vertices = [
    [0, 0, 0], [20, 0, 0], [20, 20, 0], [0, 20, 0],
    [0, 0, 20], [20, 0, 20], [20, 20, 20], [0, 20, 20],
  ];
  const triangles = faces.map((face) =>
    `facet normal 0 0 0\nouter loop\n${face.map((index) => `vertex ${vertices[index].join(' ')}`).join('\n')}\nendloop\nendfacet`
  );
  return Buffer.from(`solid e2e_cube_${modelKey}\n${triangles.join('\n')}\nendsolid e2e_cube_${modelKey}\n`);
}

async function getSeededPrinters(page: import('@playwright/test').Page): Promise<PrinterSummary[]> {
  const token = await getStoredAuthToken(page);
  expect(token, 'The authenticated emulator fixture must provide an API token').toBeTruthy();

  const response = await page.request.get(`${API_BASE_URL}/api/printers`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  expect(response.ok(), `GET /api/printers returned HTTP ${response.status()}`).toBeTruthy();
  return await response.json() as PrinterSummary[];
}

async function uploadModelFromFilesPage(page: import('@playwright/test').Page): Promise<ModelUploadResponse> {
  const modelKey = randomUUID();
  await page.goto('/files');
  await page.getByRole('button', { name: 'Upload Model', exact: true }).click();

  const dialog = page.getByRole('dialog', { name: 'Upload 3D Models' });
  await expect(dialog).toBeVisible();
  await dialog.locator('#model-file-upload').setInputFiles({
    name: `e2e-slicer-cube-${modelKey}.stl`,
    mimeType: 'model/stl',
    buffer: cubeStl(modelKey),
  });

  const uploadResponse = page.waitForResponse((response) =>
    response.request().method() === 'POST'
    && new URL(response.url()).pathname.endsWith('/api/3d-models/upload')
  );
  await dialog.getByRole('button', { name: 'Upload 1 File', exact: true }).click();
  const response = await uploadResponse;
  expect(response.ok(), `Model upload returned HTTP ${response.status()}`).toBeTruthy();

  const model = await response.json() as ModelUploadResponse;
  expect(model.id).toMatch(/^[a-f\d-]{36}$/i);
  await expect(dialog.getByText('✓ Done', { exact: true })).toBeVisible({ timeout: 30_000 });
  return model;
}

async function removeUploadedModel(
  page: import('@playwright/test').Page,
  modelId: string,
): Promise<void> {
  const token = await getStoredAuthToken(page);
  const response = await page.request.delete(`${BASE_URL}/api/3d-models/${modelId}`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  expect(response.status(), `Deleting uploaded test model returned HTTP ${response.status()}`).toBe(204);
}

async function setSlicerPrinter(
  page: import('@playwright/test').Page,
  printerId: string,
  url: string,
): Promise<import('@playwright/test').Locator> {
  await page.evaluate(({ id, storageKey }) => localStorage.setItem(storageKey, id), {
    id: printerId,
    storageKey: 'sliceJob.selectedPrinterId',
  });
  await page.goto(url);
  const sidebar = page.getByTestId('slicer-settings-sidebar');
  await expect(sidebar).toBeVisible({ timeout: 60_000 });
  return sidebar;
}

test.describe('Slicer workflows — deployed stack', () => {
  test('switches from the default printer without hanging the slicer UI', async ({ page }) => {
    test.setTimeout(60_000);
    const printers = await getSeededPrinters(page);
    await page.evaluate(() => localStorage.removeItem('sliceJob.selectedPrinterId'));
    await page.goto('/slicer');
    const sidebar = page.getByTestId('slicer-settings-sidebar');
    await expect(sidebar).toBeVisible({ timeout: 60_000 });
    const defaultPrinterButton = sidebar.getByRole('button').filter({ hasText: 'Change' }).first();
    const defaultPrinterText = await defaultPrinterButton.textContent() ?? '';
    const initial = printers.find((printer) => defaultPrinterText.includes(printer.name));
    expect(initial, 'The slicer UI must select one of the deterministic seeded printers by default').toBeDefined();
    const target = printers.find((printer) => printer.id !== initial!.id && printer.isOnline !== false);
    expect(target, 'A second online deterministic slicer printer must be seeded').toBeDefined();

    const currentPrinter = sidebar.getByRole('button').filter({ hasText: initial!.name }).first();
    await expect(currentPrinter).toBeVisible({ timeout: 15_000 });
    await currentPrinter.click({ timeout: 15_000 });

    const picker = page.getByRole('dialog', { name: 'Select Printer' });
    await expect(picker).toBeVisible({ timeout: 15_000 });
    await picker.getByRole('button').filter({ hasText: target!.name }).click({ timeout: 15_000 });

    await expect(
      sidebar.getByRole('button').filter({ hasText: target!.name }).first(),
      'The selected printer must update within the UI responsiveness bound',
    ).toBeVisible({ timeout: 15_000 });
    await expect(
      sidebar.getByRole('button', { name: /^Machine profile:/ }),
      'The machine-profile control must remain available after the printer switch',
    ).toBeEnabled({ timeout: 15_000 });
    const changedPrinter = sidebar.getByRole('button').filter({ hasText: target!.name }).first();
    await expect(changedPrinter, 'The selected printer control must remain interactive after switching')
      .toBeEnabled({ timeout: 15_000 });
    await changedPrinter.click({ timeout: 15_000 });
    const reopenedPicker = page.getByRole('dialog', { name: 'Select Printer' });
    await expect(reopenedPicker).toBeVisible({ timeout: 15_000 });
    await expect(reopenedPicker.getByPlaceholder('Search printers...')).toBeEnabled({ timeout: 15_000 });
  });

  test('uploads a model, selects slicer profiles, completes a real slice, and inspects its result', async ({ page }) => {
    test.setTimeout(240_000);
    const printers = await getSeededPrinters(page);
    const printer = printers.find((item) => item.name === 'Moonraker Ready');
    expect(printer, 'The deterministic slicer printer must be seeded').toBeDefined();

    const model = await uploadModelFromFilesPage(page);
    try {
      const sidebar = await setSlicerPrinter(page, printer!.id, `/slicer?modelId=${model.id}`);

      const machineProfileButton = sidebar.getByRole('button', { name: /^Machine profile:/ });
      await expect(machineProfileButton).toBeEnabled({ timeout: 60_000 });
      await machineProfileButton.click();
      const machineDialog = page.getByRole('dialog', { name: 'Select machine profile' });
      await expect(machineDialog).toBeVisible();
      const firstMachineGroup = machineDialog.getByRole('region').first();
      await expect(firstMachineGroup.getByRole('button').first()).toBeVisible();
      await firstMachineGroup.getByRole('button').first().click();

      const filamentOptions = sidebar.getByRole('button', { name: 'Filament profile options menu', exact: true });
      await expect(filamentOptions).toBeVisible({ timeout: 60_000 });
      const filamentField = filamentOptions.locator('../../..');
      await filamentField.locator('button[aria-haspopup="dialog"]').click();
      const filamentDialog = page.getByRole('dialog', { name: 'Filament selector' });
      await expect(filamentDialog).toBeVisible();
      await filamentDialog.getByPlaceholder('Search filaments...').fill('PLA');
      const filamentProfile = filamentDialog.locator(
        'button[title]:not([title="Configure visible manufacturers & materials"])'
      ).first();
      await expect(filamentProfile, 'The deployed OrcaSlicer worker must provide a PLA filament profile')
        .toBeVisible({ timeout: 30_000 });
      await filamentProfile.click();

      const processSelector = sidebar.locator('select:has(option[value^="system:"])');
      await expect(processSelector).toBeVisible({ timeout: 60_000 });
      const processOptions = await processSelector.locator('option[value^="system:"]').evaluateAll((options) =>
        options.map((option) => (option as HTMLOptionElement).value)
      );
      expect(processOptions.length, 'The deployed OrcaSlicer worker must provide a system process profile').toBeGreaterThan(0);
      await processSelector.selectOption(processOptions[0]);

      const sliceButton = page.getByRole('button', { name: /^Slice Plate 1 \(1 model\)$/ });
      await expect(sliceButton).toBeEnabled({ timeout: 60_000 });
      const submittedResponse = page.waitForResponse((response) =>
        response.request().method() === 'POST'
        && /\/api\/slice\/?$/.test(new URL(response.url()).pathname)
      );
      await sliceButton.click();
      const submission = await submittedResponse;
      expect(submission.ok(), `Slice submission returned HTTP ${submission.status()}`).toBeTruthy();
      const { jobId: submittedJobId } = await submission.json() as { jobId: string };

      const results = page.getByRole('region', { name: 'Slice results' });
      await expect(results, 'The real OrcaSlicer worker must complete the submitted job')
        .toBeVisible({ timeout: 180_000 });
      await expect(results.getByText('Done', { exact: true })).toBeVisible();
      await expect(results.getByRole('button', { name: 'Preview', exact: true })).toBeEnabled();
      await results.getByRole('button', { name: 'Preview', exact: true }).click();
      await expect(page.getByRole('dialog', { name: 'G-code Preview' })).toBeVisible({ timeout: 30_000 });
      await page.getByRole('dialog', { name: 'G-code Preview' })
        .getByRole('button', { name: 'Close modal' }).click();

      await page.goto('/admin/workers?workerTab=jobs');
      const jobs = page.getByRole('table', { name: 'Slice jobs' });
      await expect(jobs).toBeVisible({ timeout: 30_000 });
      const jobRow = jobs.getByRole('row').filter({ hasText: submittedJobId.slice(0, 8) });
      await expect(jobRow, 'The completed slice must be visible in worker job management').toBeVisible({ timeout: 30_000 });
      await expect(jobRow).toContainText('Completed');
    } finally {
      await removeUploadedModel(page, model.id);
    }
  });
});
