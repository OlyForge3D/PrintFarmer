import { expect, test } from '@playwright/test';
import { mountRoutedToolbarPage } from './fixtures/toolbar-routed';
import { toolbarAdminSeeds } from './fixtures/toolbar-routes-admin';

const since = '2026-10-30T12:00:00Z';
const printers = Array.from({ length: 4 }, (_, index) => ({
  id: `00000000-0000-0000-0000-${String(index).padStart(12, '0')}`,
  name: ['Fixture CoreXY', 'Fixture out of service A', 'Fixture out of service B', 'Fixture unexpected'][index],
  backend: 'Moonraker', isEnabled: index !== 2, isOnline: index === 0,
  state: index === 0 ? 'Idle' : 'Offline', rowVersion: 'Zml4dHVyZQ==',
}));

const overview = {
  checkedAt: since, overallStatus: 'Degraded',
  subsystems: [
    { key: 'api', name: 'API', status: 'Healthy', detail: 'Responding' },
    { key: 'database', name: 'Database', status: 'Healthy', detail: 'SQLite' },
    { key: 'signalr', name: 'SignalR Hub', status: 'Healthy', detail: 'Available' },
    { key: 'backends', name: 'Printer Backends', status: 'Degraded', detail: '1 / 10 in service reachable' },
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
  await expect(page.getByText('1 / 10 in service reachable')).toBeVisible();
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

test('printer availability uses the service status control', async ({ page }, testInfo) => {
  await page.setViewportSize(testInfo.project.name.startsWith('mobile')
    ? { width: 390, height: 844 } : { width: 1440, height: 1000 });
  const rows = structuredClone(printers);
  rows[2].isEnabled = false;
  await page.addInitScript(() => localStorage.setItem('printerViewMode', 'table'));
  const requests = await mountRoutedToolbarPage(page, '/printers', {
    ...toolbarAdminSeeds,
    '/api/printers': rows,
    '/api/printers/camera-urls': [],
    '/api/printers/backend-capabilities': [],
    '/api/printers/summary': rows,
    [`/api/printers/${rows[3].id}`]: rows[3],
    [`/api/printers/${rows[3].id}/details`]: rows[3],
    '/api/auto-dispatch/status': [],
    '/api/locations': [],
    '/api/tags': [],
    '/api/settings/OperatorFeatures': { enabled: false },
  });
  const mutations: { body: unknown; revision: string | undefined; method: string }[] = [];
  await page.route(`**/api/printers/${rows[3].id}`, async route => {
    const request = route.request();
    mutations.push({
      body: request.postDataJSON(),
      revision: request.headers()['if-match'],
      method: request.method(),
    });
    await route.fulfill({ json: { ...rows[3], isEnabled: false } });
  });
  const inService = page.getByRole('row').filter({ hasText: 'Fixture unexpected' });
  const outOfService = page.getByRole('row').filter({ hasText: 'Fixture out of service B' });
  await expect(inService.getByRole('button', { name: 'Mark Fixture unexpected Out of Service' })).toBeVisible();
  await expect(outOfService.getByRole('button', { name: 'Mark Fixture out of service B In Service' })).toBeVisible();
  await expect(page.getByText('Expected offline', { exact: true })).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath('printers-service-status.png'), fullPage: true });
  await inService.getByRole('button', { name: 'Mark Fixture unexpected Out of Service' }).click();
  await expect(page.getByText('Fixture unexpected is now Out of Service')).toBeVisible();
  expect(mutations).toEqual([{
    body: { isEnabled: false },
    revision: '"Zml4dHVyZQ=="',
    method: 'PUT',
  }]);
  expect(requests.errors).toEqual([]);
  expect(requests.external).toEqual([]);
  expect(requests.writes).toEqual([]);
});
