import { expect, test } from '@playwright/test';
import { mountRoutedToolbarPage, toolbarRouteUser } from './fixtures/toolbar-routed';
import { toolbarAdminSeeds } from './fixtures/toolbar-routes-admin';

const seeds: Record<string, unknown> = {
  ...toolbarAdminSeeds,
  '/api/settings/metadata': [
    ...(toolbarAdminSeeds['/api/settings/metadata'] as object[]),
    {
      key: 'CostTracking', className: 'CostTrackingSettings', displayName: 'Cost tracking',
      group: 'Operations', properties: [
        { name: 'electricityRatePerKwh', type: 'Double', attributes: [], display: { name: 'Electricity rate per kWh', inputType: 'Number' } },
        { name: 'defaultMachineHourlyRate', type: 'Double', attributes: [], display: { name: 'Default machine hourly rate', inputType: 'Number' } },
        { name: 'averagePrinterWattage', type: 'Double', attributes: [], display: { name: 'Average printer wattage', inputType: 'Number' } },
      ],
    },
    {
      key: 'Obico', className: 'ObicoSettings', displayName: 'Obico',
      group: 'Monitoring', properties: [
        { name: 'enabled', type: 'Boolean', attributes: [], display: { name: 'Enable Obico detection', inputType: 'Checkbox' } },
      ],
    },
    {
      key: 'QueuePlanning', className: 'QueuePlanningSettings', displayName: 'Queue planning',
      group: 'Operations', properties: [
        { name: 'workdayStartHourUtc', type: 'Int32', attributes: [], display: { name: 'Workday start hour (UTC)', inputType: 'Number' } },
      ],
    },
    {
      key: 'OperatorFeatures', className: 'OperatorFeatureSettings', displayName: 'Operator features',
      group: 'Operations', properties: [
        { name: 'attentionEnabled', type: 'Boolean', attributes: [], display: { name: 'Attention feed', inputType: 'Checkbox' } },
      ],
    },
    {
      key: 'AutoTagging', className: 'AutoTaggingSettings', displayName: 'Auto-tagging rules',
      group: 'Operations', properties: [
        { name: 'enabled', type: 'Boolean', attributes: [], display: { name: 'Enable automatic tagging', inputType: 'Checkbox' } },
      ],
    },
    {
      key: 'MaintenanceAlerts', className: 'MaintenanceAlertSettings', displayName: 'Maintenance alerts',
      group: 'Maintenance', properties: [
        { name: 'enabled', type: 'Boolean', attributes: [], display: { name: 'Enable maintenance alerts', inputType: 'Checkbox' } },
      ],
    },
    {
      key: 'PrintStatisticsSync', className: 'PrintStatisticsSyncSettings', displayName: 'Print statistics synchronization',
      group: 'Maintenance', properties: [
        { name: 'enabled', type: 'Boolean', attributes: [], display: { name: 'Enable statistics synchronization', inputType: 'Checkbox' } },
      ],
    },
    {
      key: 'Slicing', className: 'SlicingSettings', displayName: 'Slicing defaults',
      description: 'Synthetic slicer defaults.', group: 'Slicing', order: 2,
      properties: [{
        name: 'enabled', type: 'Boolean', attributes: [],
        display: { name: 'Enable slicing', inputType: 'Checkbox' },
      }],
    },
  ],
  '/api/settings': {
    SystemLog: { enabled: true }, Slicing: { enabled: true },
    CostTracking: { electricityRatePerKwh: 0.15, defaultMachineHourlyRate: 1, averagePrinterWattage: 150 },
    Obico: { enabled: false }, QueuePlanning: { workdayStartHourUtc: 8 },
    OperatorFeatures: { attentionEnabled: true },
    AutoTagging: { enabled: false }, MaintenanceAlerts: { enabled: true },
    PrintStatisticsSync: { enabled: true },
  },
  '/api/settings/groups': [
    { key: 'System', displayName: 'System', order: 1 },
    { key: 'Operations', displayName: 'Operations', order: 3 },
    { key: 'Monitoring', displayName: 'Monitoring', order: 4 },
    { key: 'Slicing', displayName: 'Slicing', order: 2 },
  ],
  '/api/nfc-devices': [],
  '/api/nfc/bindings': [],
  '/api/spoolman/spools': [],
  '/api/obico-servers': [],
  '/api/custom-fields/definitions': [],
  '/api/maintenance/alerts': [],
  '/api/maintenance/upcoming': [],
  '/api/maintenance/tasks': [],
  '/api/failure-detection/status': {
    monitoringEnabled: false, confidenceThreshold: 0.8, scanIntervalSeconds: 30,
    configuredPrinterCount: 0, activelyMonitoredPrinterCount: 0,
    lastAnalyzedPrinterCount: 0, lastFailureCount: 0, printers: [],
  },
  '/api/users': [],
  '/api/users/roles': [],
  '/api/settings/security/password-policy': {
    minLength: 12, requireUppercase: true, requireLowercase: true,
    requireDigit: true, requireSymbol: true,
  },
  '/api/quotas': [],
  '/api/tags': [],
  '/api/3d-models': [],
  '/api/gcode-files/query': { files: [], totalCount: 0 },
  '/api/admin/integrations/home-assistant/settings': { enabled: false, baseUrl: '', tokenMasked: '' },
  '/api/admin/integrations/telegram/settings': {
    enabled: false, botTokenMasked: '', defaultChatId: '',
    notifyOnJobStarted: false, notifyOnJobCompleted: false,
    notifyOnJobFailed: false, notifyOnPrinterOffline: false,
  },
};

const categories = [
  { id: 'system', title: 'System', ready: 'Enable system logging', tabs: 0 },
  { id: 'failure-detection', title: 'Failure Detection', ready: 'Failure Detection Runtime', tabs: 0 },
  { id: 'costs', title: 'Costs & Energy', ready: 'Electricity rate per kWh', tabs: 0 },
  { id: 'planning', title: 'Planning', ready: 'Workday start hour', tabs: 0 },
  { id: 'operator-features', title: 'Operator Features', ready: 'Attention feed', tabs: 0 },
  { id: 'slicing', title: 'Slicing', sub: 'defaults', ready: 'Enable slicing', tabs: 3 },
  { id: 'hardware', title: 'Hardware', sub: 'nfc', ready: 'No NFC devices registered', tabs: 2 },
  { id: 'integrations', title: 'Integrations', sub: 'connections', ready: 'Home Assistant URL', tabs: 2 },
  { id: 'quotas', title: 'Quotas', ready: 'No quotas', tabs: 0 },
  { id: 'users', title: 'Users', sub: 'accounts', ready: 'No users', tabs: 2 },
  { id: 'data', title: 'Data', sub: 'tags', ready: 'No tags', tabs: 3 },
] as const;

for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
  test.describe(`${viewport.width}px focused admin categories`, () => {
    test.use({ viewport, reducedMotion: 'reduce' });

    test('Control Center exposes purpose-specific category destinations', async ({ page }, testInfo) => {
      const requests = await mountRoutedToolbarPage(page, '/admin', seeds);
      await expect(page.getByRole('heading', { level: 1, name: 'Admin Control Center' })).toBeVisible();
      for (const category of categories) {
        const link = page.locator(`main a[href*="tab=${category.id}"]`);
        await expect(link).toHaveCount(1);
        await expect(link).toContainText(category.title);
      }
      const screenshot = testInfo.outputPath('control-center.png');
      await page.screenshot({ path: screenshot, fullPage: true });
      await testInfo.attach('Control Center', { path: screenshot, contentType: 'image/png' });
      const cards = page.getByTestId('admin-hub-settings');
      await cards.scrollIntoViewIfNeeded();
      const cardsScreenshot = testInfo.outputPath('settings-cards.png');
      await cards.screenshot({ path: cardsScreenshot });
      await testInfo.attach('Settings category cards', { path: cardsScreenshot, contentType: 'image/png' });
      expect(requests).toEqual({ unhandled: [], writes: [], external: [], errors: [] });
    });

    for (const category of categories) {
      test(`${category.title} exposes only its own sub-tabs`, async ({ page }, testInfo) => {
        const sub = 'sub' in category ? `&sub=${category.sub}` : '';
        const requests = await mountRoutedToolbarPage(
          page, `/admin/settings?scope=system&tab=${category.id}${sub}`, seeds,
        );
        await expect(page.getByRole('heading', { level: 1 })).toHaveText(category.title);
        if (category.id === 'slicing') {
          await page.getByRole('radio', { name: /Everything:/ }).click();
        }
        await expect(page.getByText(category.ready, { exact: false }).first()).toBeVisible();
        const tabs = page.getByRole('tablist', { name: `${category.title} sub-pages` });
        if (category.tabs === 0) {
          await expect(tabs).toHaveCount(0);
        } else {
          await expect(tabs.getByRole('tab')).toHaveCount(category.tabs);
          for (const tab of await tabs.getByRole('tab').all()) {
            await expect(tab).toBeInViewport({ ratio: 1 });
          }
        }
        await expect(page.locator('main nav[aria-label="Settings categories"]')).toHaveCount(0);
        const back = page.getByRole('link', { name: /Admin Control Center/ }).first();
        await expect(back).toHaveAttribute('href', '/admin');
        await back.focus();
        await expect(back).toBeFocused();
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
        const screenshot = testInfo.outputPath(`${category.id}.png`);
        await page.screenshot({ path: screenshot, fullPage: true });
        await testInfo.attach(category.title, { path: screenshot, contentType: 'image/png' });
        if (category.id === 'hardware') {
          await page.getByText('No NFC tags have been bound yet.', { exact: false }).scrollIntoViewIfNeeded();
          const bindingsScreenshot = testInfo.outputPath('nfc-bindings.png');
          await page.screenshot({ path: bindingsScreenshot, fullPage: true });
          await testInfo.attach('NFC bindings on the same page', { path: bindingsScreenshot, contentType: 'image/png' });
        }
        expect(requests).toEqual({ unhandled: [], writes: [], external: [], errors: [] });
      });
    }

    test('data arrow navigation retains focus and workspace search', async ({ page }) => {
      const requests = await mountRoutedToolbarPage(
        page, '/admin/settings?scope=system&tab=data&sub=tags&q=tag', {
          ...seeds,
          '/api/custom-fields/definitions': [],
        },
      );
      const tags = page.getByRole('tab', { name: 'Tags', exact: true });
      await tags.focus();
      await page.keyboard.press('ArrowRight');
      const rules = page.getByRole('tab', { name: 'Auto-Tagging' });
      await expect(rules).toBeFocused();
      await expect(rules).toHaveAttribute('aria-selected', 'true');
      await page.keyboard.press('ArrowRight');
      await expect(page.getByRole('tab', { name: 'Custom Fields' })).toBeFocused();
      await expect(page).toHaveURL(/sub=custom-fields/);
      await expect(page).toHaveURL(/q=tag/);
      expect(requests).toEqual({ unhandled: [], writes: [], external: [], errors: [] });
    });

    for (const surface of [
      { id: 'auto-tagging', path: '/admin/settings?scope=system&tab=data&sub=auto-tagging', ready: 'Enable automatic tagging' },
      { id: 'custom-fields', path: '/admin/settings?scope=system&tab=data&sub=custom-fields', ready: 'No printer custom fields' },
      { id: 'maintenance', path: '/maintenance?tab=settings', ready: 'Enable maintenance alerts' },
      { id: 'power-monitors', path: '/admin/power-monitors', ready: 'Farm-Wide Fallback Electricity Rate' },
    ]) {
      test(`${surface.id} retains its dedicated editor`, async ({ page }, testInfo) => {
        const requests = await mountRoutedToolbarPage(page, surface.path, seeds);
        if (surface.id === 'maintenance') {
          await expect(page.getByRole('tab', { name: 'Settings', exact: true })).toBeInViewport({ ratio: 1 });
        }
        if (surface.id === 'auto-tagging' || surface.id === 'maintenance') {
          await page.getByRole('radio', { name: /Everything:/ }).click();
        }
        await expect(page.getByText(surface.ready, { exact: false }).first()).toBeVisible();
        if (surface.id === 'maintenance') {
          await expect(page.getByText('Enable statistics synchronization')).toBeVisible();
          await expect(page.getByText('Electricity rate per kWh')).toHaveCount(0);
        }
        if (surface.id === 'power-monitors') {
          await expect(page.getByRole('link', { name: /Costs & Energy/ })).toHaveAttribute('href', '/admin/settings?scope=system&tab=costs');
          await expect(page.getByRole('button', { name: /Save.*Rate/ })).toHaveCount(0);
        }
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
        const screenshot = testInfo.outputPath(`${surface.id}.png`);
        await page.screenshot({ path: screenshot, fullPage: true });
        await testInfo.attach(surface.id, { path: screenshot, contentType: 'image/png' });
        expect(requests).toEqual({ unhandled: [], writes: [], external: [], errors: [] });
      });
    }

    test('legacy pins resolve in the real navbar without duplicate destinations', async ({ page }, testInfo) => {
      await page.addInitScript(() => {
        localStorage.setItem('pf_nav_preferences_v1:toolbar-fixture-user', JSON.stringify({
          version: 1, orderedItemIds: [], hiddenItemIds: [], pinnedItemIds: [],
          adminPinnedItemIds: ['gen-farm', 'auto-costs', 'hw-nfc-bindings', 'hw-nfc'],
        }));
      });
      const requests = await mountRoutedToolbarPage(page, '/admin', seeds);
      await expect(page.getByRole('heading', { level: 1, name: 'Admin Control Center' })).toBeVisible();
      if (viewport.width === 390) {
        await page.getByRole('button', { name: 'Open navigation menu' }).click();
      }
      const admin = page.getByRole('region', { name: 'Admin', exact: true });
      await expect(admin.getByRole('link', { name: 'Costs & Energy', exact: true })).toHaveCount(1);
      await expect(admin.getByRole('link', { name: 'Costs & Energy', exact: true })).toHaveAttribute('href', '/admin/settings?tab=costs');
      await expect(admin.getByRole('link', { name: 'NFC', exact: true })).toHaveCount(1);
      await expect(admin.getByRole('link', { name: 'NFC', exact: true })).toHaveAttribute('href', '/admin/settings?tab=hardware&sub=nfc');
      await admin.scrollIntoViewIfNeeded();
      const screenshot = testInfo.outputPath('migrated-pins.png');
      await page.screenshot({ path: screenshot, fullPage: true });
      await testInfo.attach('Migrated navbar pins', { path: screenshot, contentType: 'image/png' });
      expect(requests).toEqual({ unhandled: [], writes: [], external: [], errors: [] });
    });

    test('a settings delegate can discover maintenance fields in workspace and palette', async ({ page }, testInfo) => {
      const operationalRequests: string[] = [];
      page.on('request', request => {
        if (new URL(request.url()).pathname.startsWith('/api/maintenance/')) operationalRequests.push(request.url());
      });
      const requests = await mountRoutedToolbarPage(page, '/admin', {
        ...seeds,
        '/api/auth/me': { ...toolbarRouteUser, roles: ['farm_user'], permissions: ['system_settings:admin'] },
      });
      await page.locator('main a[href="/maintenance"]').click();
      await expect(page).toHaveURL(/\/maintenance\?tab=settings/);
      await expect(page.getByRole('heading', { level: 2, name: 'Settings', exact: true })).toBeVisible();
      await expect(page.getByRole('tab', { name: /Dashboard/ })).toHaveCount(0);
      const settingsScreenshot = testInfo.outputPath('delegated-maintenance.png');
      await page.screenshot({ path: settingsScreenshot, fullPage: true });
      await testInfo.attach('Delegated maintenance settings only', { path: settingsScreenshot, contentType: 'image/png' });
      await page.goto('/admin/settings?scope=system&tab=system');
      await page.getByRole('combobox', { name: 'Search all settings' }).fill('Enable maintenance alerts');
      await expect(page.getByRole('option').filter({ hasText: 'Enable maintenance alerts' })).toBeVisible();
      const workspaceScreenshot = testInfo.outputPath('delegated-maintenance-search.png');
      await page.screenshot({ path: workspaceScreenshot, fullPage: true });
      await testInfo.attach('Delegated maintenance workspace search', { path: workspaceScreenshot, contentType: 'image/png' });
      await page.keyboard.press('Control+k');
      await page.getByLabel('Search settings command palette').fill('Enable statistics synchronization');
      const field = page.getByRole('option').filter({ hasText: 'Enable statistics synchronization' });
      await expect(field).toBeVisible();
      const paletteScreenshot = testInfo.outputPath('delegated-maintenance-palette.png');
      await page.screenshot({ path: paletteScreenshot, fullPage: true });
      await testInfo.attach('Delegated maintenance palette', { path: paletteScreenshot, contentType: 'image/png' });
      await field.click();
      await expect(page).toHaveURL(/\/maintenance\?tab=settings&field=PrintStatisticsSync.enabled/);
      await expect(page.getByText('Enable statistics synchronization')).toBeVisible();
      expect(operationalRequests).toEqual([]);
      expect(requests).toEqual({ unhandled: [], writes: [], external: [], errors: [] });
    });

    test('provider details appear once outside attention and active summaries', async ({ page }, testInfo) => {
      const requests = await mountRoutedToolbarPage(page, '/admin/settings?scope=system&tab=failure-detection', {
        ...seeds,
        '/api/failure-detection/status': {
          monitoringEnabled: true, confidenceThreshold: 0.8, scanIntervalSeconds: 30,
          configuredPrinterCount: 2, activelyMonitoredPrinterCount: 1,
          lastAnalyzedPrinterCount: 1, lastFailureCount: 0,
          printers: [
            {
              printerId: 'attention', printerName: 'Fixture attention printer', state: 'misconfigured',
              reason: 'Snapshot configuration needs attention.', isPrinting: false,
              detectionSource: 'global', lastOutcome: 'none',
              providers: [{ provider: 'PrintGuard', state: 'misconfigured', analysisMode: 'Shadow', reason: 'Provider camera not configured.', lastOutcome: 'none' }],
            },
            {
              printerId: 'healthy', printerName: 'Fixture active printer', state: 'monitoring',
              reason: 'Monitoring configured.', isPrinting: true,
              detectionSource: 'global', lastOutcome: 'healthy',
              providers: [{ provider: 'PrintGuard', state: 'monitoring', analysisMode: 'Shadow', reason: 'Provider analysis active.', lastOutcome: 'healthy' }],
            },
          ],
        },
      });
      const providers = page.getByRole('region', { name: 'Detection provider status' });
      await expect(providers.getByText('Fixture attention printer')).toBeVisible();
      await expect(providers.getByText('Fixture active printer')).toBeVisible();
      await expect(page.getByText('Provider camera not configured.', { exact: false })).toHaveCount(1);
      await expect(page.getByText('Provider analysis active.', { exact: false })).toHaveCount(1);
      await providers.scrollIntoViewIfNeeded();
      const screenshot = testInfo.outputPath('detection-providers.png');
      await page.screenshot({ path: screenshot, fullPage: true });
      await testInfo.attach('Populated provider runtime', { path: screenshot, contentType: 'image/png' });
      expect(requests).toEqual({ unhandled: [], writes: [], external: [], errors: [] });
    });

    test('a sole-permitted integration leaf has keyboard recovery from a denied link', async ({ page }, testInfo) => {
      const requests = await mountRoutedToolbarPage(
        page, '/admin/settings?scope=system&tab=integrations&sub=webhooks',
        { ...seeds, '/api/auth/me': { ...toolbarRouteUser, roles: ['farm_user'], permissions: ['telegram:admin'] } },
      );
      await expect(page.getByText(/don't have permission to view/)).toBeVisible();
      const recovery = page.getByRole('navigation', { name: 'Available settings sections' })
        .getByRole('button', { name: 'External Services' });
      await recovery.focus();
      await page.keyboard.press('Enter');
      await expect(page).toHaveURL(/sub=connections/);
      await expect(page.getByText(/don't have permission to view/)).toHaveCount(0);
      await expect(page.getByRole('heading', { level: 2, name: 'External Services' })).toBeFocused();
      await expect(page.getByRole('heading', { level: 2, name: 'Integrations' })).toHaveCount(0);
      await expect(page.getByRole('button', { name: 'Save Telegram Settings' })).toBeVisible();
      const screenshot = testInfo.outputPath('delegated-recovery.png');
      await page.screenshot({ path: screenshot, fullPage: true });
      await testInfo.attach('Delegated recovery', { path: screenshot, contentType: 'image/png' });
      expect(requests).toEqual({ unhandled: [], writes: [], external: [], errors: [] });
    });
  });
}
