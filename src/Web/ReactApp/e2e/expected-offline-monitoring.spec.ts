import { expect, test } from '@playwright/test';
import { mountRoutedToolbarPage } from './fixtures/toolbar-routed';
import { toolbarAdminSeeds } from './fixtures/toolbar-routes-admin';

const since = '2026-10-30T12:00:00Z';
const printers = Array.from({ length: 4 }, (_, index) => ({
  id: `00000000-0000-0000-0000-${String(index).padStart(12, '0')}`,
  name: ['Fixture CoreXY', 'Fixture shutdown A', 'Fixture shutdown B', 'Fixture unexpected'][index],
  backend: 'Moonraker', isEnabled: true, isOnline: index === 0,
  state: index === 0 ? 'Idle' : 'Offline', rowVersion: 'Zml4dHVyZQ==',
  expectedOfflineSinceUtc: index === 1 || index === 2 ? since : null,
}));

const overview = {
  checkedAt: since, overallStatus: 'Degraded',
  subsystems: [
    { key: 'api', name: 'API', status: 'Healthy', detail: 'Responding' },
    { key: 'database', name: 'Database', status: 'Healthy', detail: 'SQLite' },
    { key: 'signalr', name: 'SignalR Hub', status: 'Healthy', detail: 'Available' },
    { key: 'backends', name: 'Printer Backends', status: 'Degraded', detail: '1 / 10 reachable; 2 expected offline' },
  ],
  attention: Array.from({ length: 9 }, (_, index) => ({
    key: `printer-00000000-0000-0000-0000-${String(index).padStart(12, '0')}-unreachable`,
    severity: 'Warning', title: `Fixture unexpected ${index + 1} is unreachable`,
    detail: 'Synthetic disconnected printer; check power and network.',
    actionLabel: 'Open printer', actionRoute: `/printers/${printers[3].id}`,
  })),
};

test('compact operational issues preserve counts, critical errors and keyboard disclosures', async ({ page }, testInfo) => {
  await page.setViewportSize(testInfo.project.name.startsWith('mobile')
    ? { width: 390, height: 844 } : { width: 1440, height: 1000 });
  const requests = await mountRoutedToolbarPage(page, '/admin', {
    ...toolbarAdminSeeds, '/api/admin/overview': overview,
  });
  await expect(page.getByRole('heading', { name: 'Operational issues' })).toBeVisible();
  await expect(page.getByTestId('admin-hub-attention-summary')).toContainText('9 operational issues: 9 Warnings');
  const group = page.getByTestId('admin-hub-issue-group');
  await expect(group).toHaveCount(1);
  const summary = group.locator('summary');
  const height = await group.evaluate(element => element.getBoundingClientRect().height);
  expect(height).toBeLessThan(110);
  await expect(page.getByText('1 / 10 reachable; 2 expected offline')).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath('admin-collapsed.png'), fullPage: true });

  await summary.focus();
  await page.keyboard.press('Enter');
  await expect(group).toHaveAttribute('open', '');
  await page.keyboard.press('Tab');
  await expect(page.getByRole('link', { name: /^Open printer\s*: Fixture unexpected 1 is unreachable$/ })).toBeFocused();
  await page.screenshot({ path: testInfo.outputPath('admin-keyboard-expanded.png'), fullPage: true });
  await summary.focus();
  await page.keyboard.press('Space');
  await expect(group).not.toHaveAttribute('open');
  await page.route('**/api/admin/overview', route => route.fulfill({ json: {
    ...overview, overallStatus: 'Unhealthy',
    subsystems: overview.subsystems.map(subsystem => subsystem.key === 'database'
      ? { ...subsystem, status: 'Unhealthy', detail: 'Synthetic database connection failure' } : subsystem),
    attention: [
      { key: 'health-database', severity: 'Error', title: 'Database is unavailable',
        detail: 'Synthetic service fault remains actionable.', actionLabel: 'Open system status', actionRoute: '/admin/status' },
      ...overview.attention,
    ],
  } }));
  await page.getByRole('button', { name: 'Refresh admin overview' }).click();
  await expect(page.getByTestId('admin-hub-attention-summary')).toContainText('10 operational issues: 1 Error, 9 Warnings');
  await expect(page.getByTestId('admin-hub-issue-group').filter({ hasText: 'Database is unavailable' })).toHaveAttribute('open', '');
  await expect(page.getByText('Synthetic service fault remains actionable.')).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath('admin-critical-visible.png'), fullPage: true });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  expect(requests.errors).toEqual([]);
  expect(requests.external).toEqual([]);
  expect(requests.writes).toEqual([]);
});

test('printer monitoring remains neutral and supports reviewed reversible bulk actions', async ({ page }, testInfo) => {
  await page.setViewportSize(testInfo.project.name.startsWith('mobile')
    ? { width: 390, height: 844 } : { width: 1440, height: 1000 });
  const rows = structuredClone(printers);
  await page.addInitScript(() => localStorage.setItem('printerViewMode', 'table'));
  const requests = await mountRoutedToolbarPage(page, '/printers', {
    ...toolbarAdminSeeds,
    '/api/printers': rows,
    '/api/printers/camera-urls': [],
    '/api/printers/backend-capabilities': [],
    '/api/printers/summary': rows,
    '/api/auto-dispatch/status': [],
    '/api/locations': [],
    '/api/tags': [],
    '/api/settings/OperatorFeatures': { enabled: false },
  });
  const mutations: { body: unknown; revision: string | undefined }[] = [];
  await page.route('**/api/printers/*/expected-offline', async route => {
    const request = route.request();
    const id = new URL(request.url()).pathname.split('/')[3];
    const row = rows.find(printer => printer.id === id)!;
    mutations.push({ body: request.postDataJSON(), revision: request.headers()['if-match'] });
    row.expectedOfflineSinceUtc = request.postDataJSON() ? since : null;
    await route.fulfill({ json: { id, expectedOfflineSinceUtc: row.expectedOfflineSinceUtc, rowVersion: row.rowVersion } });
  });
  await expect(page.getByText('Expected offline', { exact: true })).toHaveCount(2);
  await expect(page.getByRole('button', { name: 'Mark expected offline', exact: true })).toHaveCount(1);
  await page.getByText('Expected offline', { exact: true }).first().scrollIntoViewIfNeeded();
  await page.screenshot({ path: testInfo.outputPath('printers-monitoring.png'), fullPage: true });
  const unexpected = page.getByRole('row').filter({ hasText: 'Fixture unexpected' });
  await unexpected.getByRole('button', { name: 'Mark expected offline', exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(unexpected.getByText('Expected offline', { exact: true })).toBeVisible();
  expect(mutations).toEqual([{ body: true, revision: '"Zml4dHVyZQ=="' }]);

  await page.getByRole('button', { name: 'Select Fixture shutdown A', exact: true }).click();
  await page.getByRole('button', { name: 'Select Fixture shutdown B', exact: true }).click();
  await page.getByRole('combobox', { name: 'Bulk action selector' }).selectOption('expected-online');
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(page.getByText('Expected offline', { exact: true })).toHaveCount(1);
  expect(mutations.slice(1)).toEqual([
    { body: false, revision: '"Zml4dHVyZQ=="' },
    { body: false, revision: '"Zml4dHVyZQ=="' },
  ]);
  await page.getByRole('button', { name: 'Collapsed Card View', exact: true }).click();
  await expect(page.getByText('Expected offline', { exact: true })).toHaveCount(1);
  await expect(page.locator('[data-sonner-toast]')).toHaveCount(0, { timeout: 15_000 });
  await page.getByText('Expected offline', { exact: true }).scrollIntoViewIfNeeded();
  await page.screenshot({ path: testInfo.outputPath('printers-compact-monitoring.png'), fullPage: true });
  expect(requests.errors).toEqual([]);
  expect(requests.external).toEqual([]);
  expect(requests.writes).toEqual([]);
});
