import { test, expect, API_BASE_URL, getStoredAuthToken } from './fixtures/monolith-setup';

test('filament deletion: real archived references and actual-app viewport evidence', async ({ page }, testInfo) => {
  const spoolmanUrl = process.env.SPOOLMAN_EVIDENCE_URL;
  test.skip(!spoolmanUrl, 'Requires the isolated real Spoolman started by monolith-smoke.yml.');
  test.setTimeout(180_000);
  const phase = process.env.FILAMENT_EVIDENCE_PHASE ?? 'after';
  expect(['before', 'after']).toContain(phase);
  const headers = { Authorization: `Bearer ${await getStoredAuthToken(page)}` };
  const configUrl = `${API_BASE_URL}/api/spoolman/config`;
  const previousConfig = await page.request.get(configUrl, { headers });
  expect(previousConfig.ok()).toBeTruthy();
  const originalConfig: unknown = previousConfig.status() === 204 ? { baseUrl: null } : await previousConfig.json();
  const configured = await page.request.post(configUrl, { headers, data: { baseUrl: spoolmanUrl } });
  expect(configured.ok()).toBeTruthy();
  const spools: number[] = [];
  const filaments: number[] = [];

  async function seed(name: string, archived: boolean | undefined) {
    const filamentResponse = await page.request.post(`${spoolmanUrl}/api/v1/filament`, {
      data: { name, material: 'PLA', diameter: 1.75, density: 1.24, weight: 1000, color_hex: '2a9d8f' },
    });
    expect(filamentResponse.ok()).toBeTruthy();
    const filament: { id: number } = await filamentResponse.json();
    filaments.push(filament.id);
    if (archived !== undefined) {
      const spoolResponse = await page.request.post(`${spoolmanUrl}/api/v1/spool`, {
        data: { filament_id: filament.id, remaining_weight: 450, archived },
      });
      expect(spoolResponse.ok()).toBeTruthy();
      const spool: { id: number } = await spoolResponse.json();
      spools.push(spool.id);
      return { filamentId: filament.id, spoolId: spool.id };
    }
    return { filamentId: filament.id };
  }

  async function capture(label: string) {
    const screenshot = await page.screenshot({ animations: 'disabled', fullPage: true });
    await testInfo.attach(`${phase}-${label}`, { body: screenshot, contentType: 'image/png' });
    await testInfo.attach(`${phase}-${label}-provenance`, {
      body: JSON.stringify({
        applicationSha: process.env.EVIDENCE_APP_SHA,
        evidenceDriverSha: process.env.EVIDENCE_DRIVER_SHA,
        phase, route: new URL(page.url()).pathname, viewport: page.viewportSize(),
        appearance: await page.locator('html').getAttribute('class'),
        state: label, spoolmanImage: 'ghcr.io/olyforge3d/spoolman@sha256:1770e5dd86300664b5848601d7a6b03748405728e0d8835a106a128c07abbfbf',
        limitation: 'Chromium mobile viewport, not a physical mobile device; seeded disposable data; no route mocks.',
      }, null, 2),
      contentType: 'application/json',
    });
  }

  try {
    await page.evaluate(() => localStorage.setItem('filaments-view-mode', 'cards'));
    for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
      await page.setViewportSize(viewport);
      const name = `Demo Archived PLA ${viewport.width}`;
      const archived = await seed(name, true);
      const filamentUrl = `${API_BASE_URL}/api/spoolman/filaments/${archived.filamentId}`;
      const visibleSpools = await page.request.get(`${spoolmanUrl}/api/v1/spool?filament.id=${archived.filamentId}`);
      expect(await visibleSpools.json(), 'Archived references are hidden from the normal spool list').toEqual([]);
      await page.goto('/spools/filaments');
      await page.getByRole('button', { name: `Delete ${name}`, exact: true }).click();
      const dialog = page.getByRole('dialog', { name: 'Delete Filament?', exact: true });
      await expect(dialog).toBeVisible();
      if (phase === 'before') {
        await capture(`archived-confirmation-${viewport.width}`);
        const deletion = page.waitForResponse(response =>
          new URL(response.url()).pathname === `/api/spoolman/filaments/${archived.filamentId}` &&
          response.request().method() === 'DELETE');
        await dialog.getByRole('button', { name: 'Delete', exact: true }).click();
        const response = await deletion;
        expect(response.status()).toBe(400);
        const error: unknown = await response.json();
        expect(JSON.stringify(error)).toContain('403');
        await expect(page.getByText('Failed to delete filament.', { exact: true })).toBeVisible();
        await capture(`archived-delete-error-${viewport.width}`);
        await testInfo.attach(`before-upstream-error-${viewport.width}`, {
          body: JSON.stringify(error), contentType: 'application/json',
        });
      } else {
        await expect(dialog).toContainText('1 archived spool');
        await expect(dialog).toContainText('permanently delete');
        await expect(dialog).toContainText('NFC tag bindings');
        await capture(`archived-confirmation-${viewport.width}`);
        await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
        expect((await page.request.get(`${spoolmanUrl}/api/v1/spool/${archived.spoolId}`)).status()).toBe(200);
        const unconfirmed = await page.request.delete(filamentUrl, { headers });
        expect(unconfirmed.status()).toBe(400);
        expect(await unconfirmed.text()).not.toContain('Response status code does not indicate success');
        const bulk = await page.request.delete(`${API_BASE_URL}/api/spoolman/filaments/bulk`, {
          headers, data: { filamentIds: [archived.filamentId] },
        });
        expect(bulk.ok()).toBeTruthy();
        const bulkResult: { errorCount: number; errors: string[] } = await bulk.json();
        expect(bulkResult.errorCount).toBe(1);
        expect(bulkResult.errors.join(' ')).toMatch(/archived/i);
        await page.getByRole('button', { name: `Delete ${name}`, exact: true }).click();
        const confirmed = page.waitForResponse(response =>
          new URL(response.url()).pathname === `/api/spoolman/filaments/${archived.filamentId}/delete-confirmed` &&
          response.request().method() === 'POST');
        await dialog.getByRole('button', { name: 'Delete filament and spools', exact: true }).click();
        const response = await confirmed;
        expect(response.status()).toBe(204);
        expect(response.request().postDataJSON()).toEqual({ archivedSpoolIds: [archived.spoolId] });
        expect((await page.request.get(`${spoolmanUrl}/api/v1/spool/${archived.spoolId}`)).status()).toBe(404);
        expect((await page.request.get(`${spoolmanUrl}/api/v1/filament/${archived.filamentId}`)).status()).toBe(404);

        const activeName = `Demo Active PLA ${viewport.width}`;
        const active = await seed(activeName, false);
        await page.reload();
        await page.getByRole('button', { name: `Delete ${activeName}`, exact: true }).click();
        await expect(dialog).toContainText('1 active spool');
        await expect(dialog.getByRole('button', { name: 'Delete', exact: true })).toBeDisabled();
        await capture(`active-spool-blocked-${viewport.width}`);
        await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
        expect((await page.request.get(`${spoolmanUrl}/api/v1/spool/${active.spoolId}`)).status()).toBe(200);

        const emptyName = `Demo Empty PLA ${viewport.width}`;
        const empty = await seed(emptyName, undefined);
        await page.reload();
        await page.getByRole('button', { name: `Delete ${emptyName}`, exact: true }).click();
        await expect(dialog).toContainText('This filament has no spools.');
        await capture(`empty-filament-${viewport.width}`);
        const emptyDeletion = page.waitForResponse(response =>
          new URL(response.url()).pathname === `/api/spoolman/filaments/${empty.filamentId}/delete-confirmed`);
        await dialog.getByRole('button', { name: 'Delete', exact: true }).click();
        expect((await emptyDeletion).status()).toBe(204);
        expect((await page.request.get(`${spoolmanUrl}/api/v1/filament/${empty.filamentId}`)).status()).toBe(404);
      }
    }
  } finally {
    for (const id of spools) {
      const deleted = await page.request.delete(`${spoolmanUrl}/api/v1/spool/${id}`);
      expect([200, 204, 404]).toContain(deleted.status());
    }
    for (const id of filaments) {
      const deleted = await page.request.delete(`${spoolmanUrl}/api/v1/filament/${id}`);
      expect([200, 204, 404]).toContain(deleted.status());
    }
    const restored = await page.request.post(configUrl, { headers, data: originalConfig ?? { baseUrl: null } });
    expect(restored.ok()).toBeTruthy();
  }
});
