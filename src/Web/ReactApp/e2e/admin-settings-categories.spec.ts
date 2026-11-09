import { expect, test } from '@playwright/test';
import { mountRoutedToolbarPage, toolbarRouteUser } from './fixtures/toolbar-routed';
import { toolbarAdminSeeds } from './fixtures/toolbar-routes-admin';

const seeds: Record<string, unknown> = {
  ...toolbarAdminSeeds,
  '/api/settings/metadata': [
    ...(toolbarAdminSeeds['/api/settings/metadata'] as object[]),
    {
      key: 'Slicing', className: 'SlicingSettings', displayName: 'Slicing defaults',
      description: 'Synthetic slicer defaults.', group: 'Slicing', order: 2,
      properties: [{
        name: 'enabled', type: 'Boolean', attributes: [],
        display: { name: 'Enable slicing', inputType: 'Checkbox' },
      }],
    },
  ],
  '/api/settings': { General: { farmName: 'Fixture farm' }, Slicing: { enabled: true } },
  '/api/settings/groups': [
    { key: 'General', displayName: 'General', order: 1 },
    { key: 'Slicing', displayName: 'Slicing', order: 2 },
  ],
  '/api/nfc-devices': [],
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
  { id: 'general', title: 'General', sub: 'farm', ready: 'Electricity', tabs: 3 },
  { id: 'slicing', title: 'Slicing', sub: 'defaults', ready: 'Enable slicing', tabs: 3 },
  { id: 'hardware', title: 'Hardware', sub: 'nfc', ready: 'No NFC devices registered', tabs: 4 },
  { id: 'integrations', title: 'Integrations', sub: 'connections', ready: 'Home Assistant URL', tabs: 2 },
  { id: 'quotas', title: 'Quotas', ready: 'No quotas', tabs: 0 },
  { id: 'users', title: 'Users', sub: 'accounts', ready: 'No users', tabs: 2 },
  { id: 'data', title: 'Data', sub: 'tags', ready: 'No tags', tabs: 0 },
] as const;

for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
  test.describe(`${viewport.width}px focused admin categories`, () => {
    test.use({ viewport, reducedMotion: 'reduce' });

    test('Control Center exposes the seven existing category destinations', async ({ page }, testInfo) => {
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
      test(`${category.title} keeps only its existing sub-tabs`, async ({ page }, testInfo) => {
        const sub = 'sub' in category ? `&sub=${category.sub}` : '';
        const requests = await mountRoutedToolbarPage(
          page, `/admin/settings?scope=system&tab=${category.id}${sub}`, seeds,
        );
        await expect(page.getByRole('heading', { level: 1 })).toHaveText(category.title);
        if (category.id === 'slicing') {
          await page.getByRole('radio', { name: /Everything:/ }).click();
        }
        await expect(page.getByText(category.ready, { exact: false }).first()).toBeVisible();
        const tabs = page.getByRole('tablist', { name: `${category.title} settings` });
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
        expect(requests).toEqual({ unhandled: [], writes: [], external: [], errors: [] });
      });
    }

    test('system arrow navigation retains focus and workspace search', async ({ page }) => {
      const requests = await mountRoutedToolbarPage(
        page, '/admin/settings?scope=system&tab=general&sub=farm&q=default', seeds,
      );
      const farm = page.getByRole('tab', { name: 'Farm Defaults' });
      await farm.focus();
      await page.keyboard.press('ArrowRight');
      const system = page.getByRole('tab', { name: 'System Config' });
      await expect(system).toBeFocused();
      await expect(system).toHaveAttribute('aria-selected', 'true');
      await page.keyboard.press('ArrowRight');
      await expect(page.getByRole('tab', { name: 'Automation & Costs' })).toBeFocused();
      await expect(page).toHaveURL(/sub=automation/);
      await expect(page).toHaveURL(/q=default/);
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
