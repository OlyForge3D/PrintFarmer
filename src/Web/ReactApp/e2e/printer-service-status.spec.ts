import { expect, test } from '@playwright/test';
import { mountRoutedToolbarPage, toolbarRouteUser } from './fixtures/toolbar-routed';
import { toolbarAdminSeeds } from './fixtures/toolbar-routes-admin';

const since = '2026-10-30T12:00:00Z';
const printers = Array.from({ length: 4 }, (_, index) => ({
  id: `00000000-0000-0000-0000-${String(index).padStart(12, '0')}`,
  name: ['Fixture CoreXY', 'Fixture out of service A', 'Fixture out of service B', 'Fixture unexpected'][index],
  backend: 'Moonraker', isEnabled: index !== 2, isOnline: index === 0,
  state: index === 0 ? 'Idle' : 'Offline', rowVersion: 'Zml4dHVyZQ==',
  manufacturerName: 'Fixture manufacturer with a long display name',
  modelName: 'Fixture CoreXY model',
  ipAddress: index === 0 ? '192.0.2.10' : '2001:db8:1234:5678:90ab:cdef:1234:5678',
  frontendUrl: 'http://192.0.2.10',
  cameraStreamUrl: '/fixture-camera.jpg',
}));

const printerSeeds = {
  ...toolbarAdminSeeds,
  '/api/printers': printers,
  '/api/printers/camera-urls': [],
  '/api/printers/backend-capabilities': [],
  '/api/printers/summary': printers,
  [`/api/printers/${printers[3].id}`]: printers[3],
  [`/api/printers/${printers[3].id}/details`]: printers[3],
  '/api/auto-dispatch/status': [],
  '/api/locations': [],
  '/api/tags': [],
  '/api/settings/OperatorFeatures': { enabled: false },
};

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

test('active-job review issue navigates to its printer', async ({ page }, testInfo) => {
  await page.setViewportSize(testInfo.project.name.startsWith('mobile')
    ? { width: 390, height: 844 } : { width: 1440, height: 1000 });
  const printerId = '00000000-0000-0000-0000-000000000099';
  const jobId = '00000000-0000-0000-0000-000000000199';
  const reviewIssue = {
    key: `printer-${printerId}-job-${jobId}-review`,
    severity: 'Warning',
    title: "Review active job 'Fixture active print'",
    detail: 'This active job needs operator review after a service-status conflict. The printer may still be In Service; no pause or cancellation was requested. Review and resolve the job before changing service status. This issue clears when the job is no longer active.',
    actionLabel: 'Review printer',
    actionRoute: `/printers/${printerId}`,
  };
  const requests = await mountRoutedToolbarPage(page, '/admin', {
    ...toolbarAdminSeeds,
    '/api/admin/overview': { ...overview, attention: [reviewIssue] },
  });
  const group = page.getByTestId('admin-hub-issue-group');
  await expect(group).toHaveCount(1);
  await expect(group).toContainText('Review active job');
  await group.locator('summary').click();
  const reviewLink = page.getByRole('link', { name: /Review printer.*Review active job 'Fixture active print'/ });
  await expect(reviewLink).toBeVisible();
  await expect(reviewLink).toHaveAttribute('href', `/printers/${printerId}`);
  await page.screenshot({ path: testInfo.outputPath('admin-active-job-review.png'), fullPage: true });
  expect(requests.errors).toEqual([]);
  expect(requests.external).toEqual([]);
  expect(requests.writes).toEqual([]);
});

for (const view of ['table', 'collapsed', 'detailed'] as const) {
test(`printer availability uses the service status control in ${view} view`, async ({ page }, testInfo) => {
  await page.setViewportSize(testInfo.project.name.startsWith('mobile')
    ? { width: 390, height: 844 } : { width: 1440, height: 1000 });
  const rows = structuredClone(printers);
  rows[2].isEnabled = false;
  const requests = await mountRoutedToolbarPage(page, `/printers?view=${view}`, {
    ...printerSeeds,
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
    if (request.method() === 'GET') {
      await route.fulfill({ json: rows[3] });
      return;
    }
    mutations.push({
      body: request.postDataJSON(),
      revision: request.headers()['if-match'],
      method: request.method(),
    });
    await route.fulfill({ json: { ...rows[3], isEnabled: false } });
  });
  const markOutOfService = page.getByRole('button', { name: 'In Service: Mark Fixture unexpected Out of Service', exact: true });
  const markInService = page.getByRole('button', { name: 'Out of Service: Mark Fixture out of service B In Service', exact: true });
  await expect(markOutOfService).toBeVisible();
  await expect(markInService).toBeVisible();
  const helpName = view === 'table' ? 'Troubleshoot ' : 'Open troubleshooting for ';
  await expect(page.getByRole('button', { name: `${helpName}Fixture unexpected`, exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: `${helpName}Fixture out of service B`, exact: true })).toHaveCount(0);
  if (view === 'table') {
    await expect(page.getByRole('columnheader')).toHaveText([
      '', 'Printer', 'Status', 'Backend', 'Progress', 'Temperatures', 'Actions',
    ]);
    await expect(markOutOfService.locator('xpath=ancestor::td').locator('button').first())
      .toHaveAttribute('aria-label', 'In Service: Mark Fixture unexpected Out of Service');
    await expect(markOutOfService).toHaveAttribute('aria-pressed', 'true');
    await expect(markInService).toHaveAttribute('aria-pressed', 'false');
    await expect(markOutOfService.locator('svg')).toHaveClass(/text-pf-success/);
    await expect(markInService.locator('svg')).toHaveClass(/text-pf-text-secondary/);
    await expect(markOutOfService).not.toHaveText('In Service');
    await expect(page.getByRole('columnheader', { name: 'Last Updated' })).toHaveCount(0);
  } else {
    const identity = page.getByTestId(view === 'collapsed' ? 'compact-printer-image' : 'detailed-printer-image');
    await expect(identity.first().getByText('192.0.2.10', { exact: true })).toBeVisible();
    await expect(identity.nth(1).getByText('2001:db8:1234:5678:90ab:cdef:1234:5678', { exact: true })).toBeVisible();
    const toolbar = page.getByRole(view === 'collapsed' ? 'group' : 'toolbar', { name: 'Printer actions' }).first();
    await expect(toolbar.getByRole('button', { name: /^In Service: Mark/ })).toHaveText('');
    const card = toolbar.locator('xpath=ancestor::article');
    const browser = card.getByRole('link', { name: /in new tab$/ });
    await expect(browser).toHaveAttribute('href', 'http://192.0.2.10');
    await expect(browser).toHaveAttribute('target', '_blank');
    await expect(browser).toHaveAttribute('rel', 'noopener noreferrer');
    await expect(toolbar.getByRole('link')).toHaveCount(0);
    const cardBox = await card.boundingBox();
    const browserBox = await browser.boundingBox();
    const identityBox = await identity.first().boundingBox();
    const headerBox = await browser.locator('xpath=../../..').boundingBox();
    expect(headerBox!.height).toBe(52);
    expect(Math.abs(browserBox!.x + browserBox!.width - (cardBox!.x + cardBox!.width - 13))).toBeLessThan(2);
    expect(browserBox!.y + browserBox!.height).toBeLessThanOrEqual(identityBox!.y);
    if (view === 'collapsed') {
      const sidebar = card.getByRole('button', { name: 'Open details sidebar', exact: true });
      await expect(toolbar.getByRole('button', { name: 'Open details sidebar' })).toHaveCount(0);
      const sidebarBox = await sidebar.boundingBox();
      expect(sidebarBox!.width).toBe(44);
      expect(sidebarBox!.height).toBe(44);
      expect(sidebarBox!.y).toBe(browserBox!.y);
      expect(sidebarBox!.x + sidebarBox!.width).toBeLessThanOrEqual(browserBox!.x);
      await sidebar.focus();
      await page.keyboard.press('Tab');
      await expect(browser).toBeFocused();
    } else {
      await expect(card.getByRole('button', { name: 'Open details sidebar' })).toHaveCount(0);
    }
    await browser.focus();
    await expect(browser).toBeFocused();
    await expect(page.getByRole('button', { name: 'More options' })).toHaveCount(0);
    const actions = toolbar.locator(':scope > button, :scope > a, :scope > span');
    await expect(actions).toHaveCount(view === 'collapsed' ? 7 : 6);
    expect(await actions.evaluateAll(elements => elements.map(element => element.getAttribute('aria-label')))).toEqual([
      'In Service: Mark Fixture CoreXY Out of Service',
      'Show camera preview',
      'Toggle auto-dispatch for Fixture CoreXY',
      'View printer files',
      'View print history',
      view === 'collapsed' ? 'Edit Printer' : 'Edit details',
      ...(view === 'collapsed' ? ['Manage Tags'] : []),
    ]);
    const boxes = await actions.evaluateAll(elements => elements.map(element => {
      const { x, y, width, height } = element.getBoundingClientRect();
      return { x, y, width, height };
    }));
    const toolbarBox = await toolbar.boundingBox();
    expect(cardBox!.width).toBeGreaterThanOrEqual(320);
    expect(toolbarBox!.x).toBeGreaterThanOrEqual(cardBox!.x);
    expect(toolbarBox!.x + toolbarBox!.width).toBeLessThanOrEqual(cardBox!.x + cardBox!.width);
    for (const [index, box] of boxes.entries()) {
      expect(box.width).toBe(44);
      expect(box.height).toBe(44);
      expect(Math.abs(box.x - boxes[index % 2].x)).toBeLessThan(1);
      if (index % 2 === 1) {
        expect(box.x).toBeGreaterThanOrEqual(boxes[index - 1].x + boxes[index - 1].width);
        expect(Math.abs(box.y - boxes[index - 1].y)).toBeLessThan(1);
      }
      if (index >= 2) expect(box.y).toBeGreaterThanOrEqual(boxes[index - 2].y + boxes[index - 2].height);
    }
    const files = toolbar.getByRole('button', { name: 'View printer files' });
    await files.focus();
    await expect(files).toBeFocused();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  }
  await expect(page.getByText('Expected offline', { exact: true })).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath('printers-service-status.png'), fullPage: true });
  if (view !== 'table') {
    const cards = page.locator('article[data-pf-card]');
    const dimensions = await cards.evaluateAll(elements => elements.map(element => {
      const { width, height } = element.getBoundingClientRect();
      return { width, height };
    }));
    await testInfo.attach(`${view}-card-dimensions`, {
      body: JSON.stringify(dimensions), contentType: 'application/json',
    });
    await cards.first().screenshot({ path: testInfo.outputPath(`${view}-in-service-card.png`) });
    await cards.nth(2).screenshot({ path: testInfo.outputPath(`${view}-out-of-service-ipv6-card.png`) });
  }
  if (view === 'table') {
    await markOutOfService.scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath('printers-service-actions.png'), fullPage: true });
  }
  const geometry = await markOutOfService.boundingBox();
  expect(geometry?.height).toBeGreaterThanOrEqual(44);
  expect(geometry?.width).toBeGreaterThanOrEqual(44);
  expect(geometry!.x).toBeGreaterThanOrEqual(0);
  expect(geometry!.x + geometry!.width).toBeLessThanOrEqual(page.viewportSize()!.width);
  await markOutOfService.click();
  await expect(page.getByText('Fixture unexpected is now Out of Service')).toBeVisible();
  expect(mutations).toEqual([{
    body: { backend: rows[3].backend, isEnabled: false },
    revision: '"Zml4dHVyZQ=="',
    method: 'PUT',
  }]);
  expect(requests.errors).toEqual([]);
  expect(requests.external).toEqual([]);
  expect(requests.writes).toEqual([]);
});
}

for (const view of ['collapsed', 'detailed'] as const) {
test(`auto-dispatch shares service hover in both states in ${view} view`, async ({ page }, testInfo) => {
  await page.setViewportSize(testInfo.project.name.startsWith('mobile')
    ? { width: 390, height: 844 } : { width: 1440, height: 1000 });
  let enabled = false;
  const status = () => ({
    globalEnabled: true,
    printers: [{
      printerId: printers[0].id, printerName: printers[0].name, enabled,
      state: enabled ? 'Idle' : 'Disabled', queueDepth: 0, isReady: true,
      readyGateChecks: [], dispatchStateETag: 'dispatch-fixture', printerETag: printers[0].rowVersion,
    }],
  });
  const requests = await mountRoutedToolbarPage(page, `/printers?view=${view}`, {
    ...printerSeeds,
    '/api/printers': [printers[0]],
    '/api/printers/summary': [printers[0]],
    '/api/auto-dispatch/status': status(),
  });
  const mutations: { body: unknown; revision: string | undefined; printerRevision: string | undefined }[] = [];
  let releaseMutation = () => {};
  await page.route('**/api/auto-dispatch/status', route => route.fulfill({ json: status() }));
  await page.route(`**/api/auto-dispatch/${printers[0].id}/enabled`, async route => {
    const request = route.request();
    mutations.push({
      body: request.postDataJSON(), revision: request.headers()['if-match'],
      printerRevision: request.headers()['x-printer-if-match'],
    });
    await new Promise<void>(resolve => { releaseMutation = resolve; });
    enabled = !enabled;
    await route.fulfill({ status: 204 });
  });
  const toolbar = page.getByRole(view === 'collapsed' ? 'group' : 'toolbar', { name: 'Printer actions' });
  const card = toolbar.locator('xpath=ancestor::article');
  const control = toolbar.getByRole('button', { name: 'Toggle auto-dispatch for Fixture CoreXY', exact: true });
  const service = toolbar.getByRole('button', { name: 'In Service: Mark Fixture CoreXY Out of Service', exact: true });
  for (const active of [false, true]) {
    await expect(control).toHaveAttribute('aria-pressed', String(active));
    await expect(control).toBeEnabled();
    await page.mouse.move(0, 0);
    await expect(control).toHaveCSS('background-color', 'rgba(0, 0, 0, 0)');
    await control.evaluate(element => Promise.all(element.getAnimations().map(animation => animation.finished)));
    const restBackground = await control.evaluate(element => getComputedStyle(element).backgroundColor);
    const restColor = await control.evaluate(element => getComputedStyle(element).color);
    const bolt = control.locator('svg');
    await expect(bolt).toHaveAttribute('fill', active ? 'currentColor' : 'none');
    await service.hover();
    await service.evaluate(element => Promise.all(element.getAnimations().map(animation => animation.finished)));
    const serviceBackground = await service.evaluate(element => getComputedStyle(element).backgroundColor);
    await control.hover();
    await expect.poll(() => control.evaluate(element => getComputedStyle(element).backgroundColor))
      .toBe(serviceBackground);
    expect(serviceBackground).not.toBe(restBackground);
    if (active) {
      expect(await control.evaluate(element => getComputedStyle(element).color)).toBe(restColor);
    }
    const box = await control.boundingBox();
    expect(box?.width).toBe(44);
    expect(box?.height).toBe(44);
    await card.screenshot({ path: testInfo.outputPath(`auto-dispatch-${active ? 'enabled' : 'disabled'}-hover.png`) });
    await control.focus();
    await expect(control).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(control).toBeDisabled();
    await expect.poll(() => mutations.length).toBe(active ? 2 : 1);
    releaseMutation();
    await expect(control).toHaveAttribute('aria-pressed', String(!active));
  }
  expect(mutations).toEqual([true, false].map(nextEnabled => ({
    body: { enabled: nextEnabled }, revision: '"dispatch-fixture"', printerRevision: '"Zml4dHVyZQ=="',
  })));
  expect(requests.errors).toEqual([]);
  expect(requests.external).toEqual([]);
  expect(requests.writes).toEqual([]);
});
}

test('sidebar exposes service status beside identity and refreshes a stale revision without reporting success', async ({ page }, testInfo) => {
  await page.setViewportSize(testInfo.project.name.startsWith('mobile')
    ? { width: 390, height: 844 } : { width: 1440, height: 1000 });
  const requests = await mountRoutedToolbarPage(page, `/printers/${printers[3].id}?view=collapsed`, printerSeeds);
  const control = page.getByRole('complementary', { name: 'Fixture unexpected details', exact: true })
    .getByRole('button', { name: 'In Service: Mark Fixture unexpected Out of Service', exact: true });
  await expect(control).toBeVisible();
  let writes = 0;
  let refreshes = 0;
  await page.route('**/api/printers', async route => {
    refreshes++;
    await route.fulfill({ json: printers });
  });
  await page.route(`**/api/printers/${printers[3].id}`, async route => {
    if (route.request().method() === 'GET') {
      await route.fulfill({ json: printers[3] });
      return;
    }
    writes++;
    await route.fulfill({ status: 412, json: { message: 'Printer changed; refresh and review again.' } });
  });
  await page.screenshot({ path: testInfo.outputPath('sidebar-service-status.png'), fullPage: true });
  await control.click();
  await expect(page.getByText('This item changed after you reviewed it. Refresh and review before confirming again.')).toBeVisible();
  await expect(page.getByText('Fixture unexpected is now Out of Service')).toHaveCount(0);
  await expect.poll(() => refreshes).toBeGreaterThan(0);
  expect(writes).toBe(1);
  expect(requests.errors).toEqual([]);
  expect(requests.external).toEqual([]);
  expect(requests.writes).toEqual([]);
});

for (const surface of ['detailed', 'sidebar'] as const) {
  for (const state of ['Idle', 'Shutdown'] as const) {
    test(`Out of Service ${surface} keeps emergency stop but not firmware restart in ${state}`, async ({ page }, testInfo) => {
      await page.setViewportSize(testInfo.project.name.startsWith('mobile')
        ? { width: 390, height: 844 } : { width: 1440, height: 1000 });
      const printer = { ...printers[3], isEnabled: false, isOnline: true, state };
      const requests = await mountRoutedToolbarPage(page,
        surface === 'sidebar' ? `/printers/${printer.id}?view=collapsed` : '/printers?view=detailed',
        {
          ...printerSeeds,
          '/api/printers': [printer],
          '/api/printers/summary': [printer],
          [`/api/printers/${printer.id}`]: printer,
          [`/api/printers/${printer.id}/details`]: printer,
        });
      await expect(page.getByRole('button', { name: 'Out of Service: Mark Fixture unexpected In Service', exact: true })
        .last()).toBeVisible();
      const action = page.getByRole('button', { name: state === 'Shutdown' ? 'Firmware Restart' : 'Emergency Stop', exact: true });
      await expect(action).toBeVisible();
      if (state === 'Shutdown') {
        await expect(action).toBeDisabled();
      } else {
        await expect(action).toBeEnabled();
      }
      await action.scrollIntoViewIfNeeded();
      await page.screenshot({ path: testInfo.outputPath('out-of-service-safety-control.png'), fullPage: true });
      expect(requests.errors).toEqual([]);
      expect(requests.external).toEqual([]);
      expect(requests.writes).toEqual([]);
    });
  }
}

for (const view of ['collapsed', 'table'] as const) {
test(`users without printer administration see service status but no service mutation in ${view}`, async ({ page }) => {
  const requests = await mountRoutedToolbarPage(page, `/printers?view=${view}`, {
    ...printerSeeds,
    '/api/auth/me': { ...toolbarRouteUser, roles: [], permissions: ['printers:read'] },
  });
  await expect(page.getByText('Fixture unexpected', { exact: true })).toBeVisible();
  await expect(page.getByRole('img', { name: 'In Service', exact: true }).first()).toBeVisible();
  await expect(page.getByRole('img', { name: 'Out of Service', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: /^(In Service|Out of Service): Mark .* (In|Out of) Service$/ })).toHaveCount(0);
  expect(requests.errors).toEqual([]);
  expect(requests.writes).toEqual([]);
});
}
