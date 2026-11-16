import { expect, test, type Page } from '@playwright/test';

const operationKeyPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
type HarvestRequestBody = Record<string, unknown>;
type ApiRequestEvidence = { method: string; path: string };

const successResponse = (operationKey: string, alreadyHarvested = false) => ({
  printJobId: 'synthetic-job-1',
  harvestedAt: '2026-11-16T17:00:00Z',
  alreadyHarvested,
  adjustments: [],
  outputs: [],
  operationKey,
});

async function openSyntheticHarvest(page: Page) {
  const mutationRequests: ApiRequestEvidence[] = [];
  const unexpectedMutations: ApiRequestEvidence[] = [];

  page.on('request', (request) => {
    if (request.url().includes('/api/') && request.method() !== 'GET') {
      mutationRequests.push({
        method: request.method(),
        path: new URL(request.url()).pathname,
      });
    }
  });
  await page.route('**/*', async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname === '/') {
      await route.fulfill({
        contentType: 'text/html',
        body: `<!doctype html>
          <html><head><meta name="viewport" content="width=device-width, initial-scale=1"></head>
          <body><div id="harvest-dialog-root"></div><script type="module">
            import RefreshRuntime from '/@react-refresh';
            RefreshRuntime.injectIntoGlobalHook(window);
            window.$RefreshReg$ = () => {};
            window.$RefreshSig$ = () => type => type;
            window.__vite_plugin_react_preamble_installed__ = true;
            await import('/src/index.css');
            const { mountHarvestDialog } =
              await import('/src/test/fixtures/harvest-dialog-harness.tsx');
            mountHarvestDialog(document.getElementById('harvest-dialog-root'));
          </script></body></html>`,
      });
      return;
    }
    if (url.origin !== new URL(process.env.BASE_URL ?? 'http://localhost:3000').origin) {
      await route.abort();
      return;
    }
    await route.continue();
  });
  await page.route('**/api/**', async (route) => {
    const request = route.request();
    const evidence = {
      method: request.method(),
      path: new URL(request.url()).pathname,
    };
    if (request.method() !== 'GET') {
      if (
        request.method() !== 'POST' ||
        (evidence.path !== '/api/parts-inventory' &&
          !/^\/api\/job-queue\/[^/]+\/harvest$/.test(evidence.path))
      ) {
        unexpectedMutations.push(evidence);
        await route.abort('blockedbyclient');
        return;
      }
    }
    await route.continue();
  });
  await page.route('**/api/parts-inventory*', async (route) => {
    await route.fulfill({ json: [] });
  });

  await page.goto('/');
  await expect(page.getByRole('dialog')).toBeVisible();
  return { mutationRequests, unexpectedMutations };
}

async function interceptHarvest(
  page: Page,
  responses: Array<{ status?: number; json: unknown }>,
) {
  const requests: HarvestRequestBody[] = [];
  await page.route('**/api/job-queue/*/harvest', async (route) => {
    const body = route.request().postDataJSON() as HarvestRequestBody;
    requests.push(body);
    const response = responses[Math.min(requests.length - 1, responses.length - 1)];
    await route.fulfill({
      status: response.status ?? 200,
      contentType: response.status && response.status >= 400
        ? 'application/problem+json'
        : 'application/json',
      json: response.json,
    });
  });
  return requests;
}

test.describe('Synthetic harvest outcomes', () => {
  test('wrong-bin override sends only the exact override request', async ({ page }) => {
    const { mutationRequests, unexpectedMutations } = await openSyntheticHarvest(page);
    const bodies = await interceptHarvest(page, [
      {
        status: 409,
        json: {
          code: 'wrongBin',
          detail: 'Wrong bin scanned.',
          mismatches: [{ partSku: 'SKU-A', expectedBinCode: 'BIN-1', scannedBinCode: 'BIN-9' }],
        },
      },
      { json: successResponse('synthetic-operation-key') },
    ]);

    await page.getByRole('button', { name: /confirm harvest/i }).click();
    await expect(page.getByTestId('harvest-wrong-bin')).toBeVisible();
    await page.getByLabel(/override reason/i).fill('Bin relabeled after audit');
    await page.getByRole('button', { name: /override & harvest/i }).click();
    await expect(page.getByTestId('harvest-success')).toBeVisible();

    expect(bodies).toHaveLength(2);
    expect(bodies[0]).toEqual({ operationKey: expect.stringMatching(operationKeyPattern) });
    expect(bodies[1]).toEqual({
      operationKey: bodies[0].operationKey,
      allowWrongBin: true,
      overrideReason: 'Bin relabeled after audit',
    });
    expect(mutationRequests).toEqual([
      { method: 'POST', path: '/api/job-queue/synthetic-job-1/harvest' },
      { method: 'POST', path: '/api/job-queue/synthetic-job-1/harvest' },
    ]);
    expect(unexpectedMutations).toEqual([]);
  });

  test('manual mapping sends explicit outputs with one idempotency key', async ({ page }) => {
    const { mutationRequests, unexpectedMutations } = await openSyntheticHarvest(page);
    const bodies = await interceptHarvest(page, [
      {
        status: 409,
        json: {
          code: 'partMappingRequired',
          detail: 'Map the project output.',
          jobId: 'synthetic-job-1',
          projectFileId: null,
          gcodeFileId: null,
          guidance: 'Enter outputs manually.',
        },
      },
      { json: successResponse('synthetic-operation-key') },
    ]);

    await page.getByRole('button', { name: /confirm harvest/i }).click();
    await expect(page.getByTestId('harvest-mapping-required')).toBeVisible();
    await page.getByLabel('SKU #1').fill('SKU-MANUAL');
    await page.getByLabel(/audit reason for manual outputs/i).fill('Verified manual count');
    await page.getByRole('button', { name: /confirm manual harvest/i }).click();
    await expect(page.getByTestId('harvest-success')).toBeVisible();

    expect(bodies[1]).toEqual({
      operationKey: bodies[0].operationKey,
      outputs: [{ sku: 'SKU-MANUAL', quantity: 1 }],
      overrideReason: 'Verified manual count',
    });
    expect(mutationRequests).toEqual([
      { method: 'POST', path: '/api/job-queue/synthetic-job-1/harvest' },
      { method: 'POST', path: '/api/job-queue/synthetic-job-1/harvest' },
    ]);
    expect(unexpectedMutations).toEqual([]);
  });

  test('feature-disabled response performs no retry or ledger mutation', async ({ page }) => {
    const { mutationRequests, unexpectedMutations } = await openSyntheticHarvest(page);
    const bodies = await interceptHarvest(page, [{
      status: 404,
      json: { code: 'featureDisabled', detail: 'Printed-parts inventory is not enabled.' },
    }]);

    await page.getByRole('button', { name: /confirm harvest/i }).click();
    await expect(page.getByTestId('harvest-feature-disabled')).toBeVisible();
    await expect(page.getByRole('button', { name: /retry/i })).toHaveCount(0);
    expect(bodies).toEqual([{ operationKey: expect.stringMatching(operationKeyPattern) }]);
    expect(mutationRequests).toEqual([
      { method: 'POST', path: '/api/job-queue/synthetic-job-1/harvest' },
    ]);
    expect(unexpectedMutations).toEqual([]);
  });

  test('transient failure retries the exact same request', async ({ page }) => {
    const { mutationRequests, unexpectedMutations } = await openSyntheticHarvest(page);
    const bodies = await interceptHarvest(page, [
      { status: 503, json: { message: 'Synthetic transient failure.' } },
      { json: successResponse('synthetic-operation-key') },
    ]);

    await page.getByRole('button', { name: /confirm harvest/i }).click();
    await expect(page.getByTestId('harvest-error')).toBeVisible();
    await page.getByRole('button', { name: /retry/i }).click();
    await expect(page.getByTestId('harvest-success')).toBeVisible();
    expect(bodies).toHaveLength(2);
    expect(bodies[1]).toEqual(bodies[0]);
    expect(mutationRequests).toEqual([
      { method: 'POST', path: '/api/job-queue/synthetic-job-1/harvest' },
      { method: 'POST', path: '/api/job-queue/synthetic-job-1/harvest' },
    ]);
    expect(unexpectedMutations).toEqual([]);
  });

  test('idempotent replay returns already-harvested without duplicate adjustments', async ({ page }) => {
    const { mutationRequests, unexpectedMutations } = await openSyntheticHarvest(page);
    const bodies = await interceptHarvest(page, [
      { status: 503, json: { message: 'Synthetic response lost after commit.' } },
      { json: successResponse('synthetic-operation-key', true) },
    ]);

    await page.getByRole('button', { name: /confirm harvest/i }).click();
    await expect(page.getByTestId('harvest-error')).toBeVisible();
    await page.getByRole('button', { name: /retry/i }).click();
    await expect(page.getByTestId('harvest-success')).toBeVisible();
    await expect(
      page.getByTestId('harvest-success').getByText(/^This job was already harvested/),
    ).toBeVisible();

    expect(bodies).toHaveLength(2);
    expect(bodies[1]).toEqual(bodies[0]);
    expect(await page.getByText(/ledger adjustments/i).count()).toBe(0);
    expect(mutationRequests).toEqual([
      { method: 'POST', path: '/api/job-queue/synthetic-job-1/harvest' },
      { method: 'POST', path: '/api/job-queue/synthetic-job-1/harvest' },
    ]);
    expect(unexpectedMutations).toEqual([]);
  });
});
