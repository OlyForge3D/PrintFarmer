import { expect, test } from '@playwright/test';
import { isolateCapture } from './fixtures';

for (const viewport of [
  { width: 1440, height: 1000 },
  { width: 390, height: 844 },
]) {
  test(`Farm Identity settings and branding at ${viewport.width}px`, async ({ page, context }, testInfo) => {
    const violations = await isolateCapture(context);
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.setViewportSize(viewport);
    await page.clock.setFixedTime(new Date('2026-03-08T10:00:00Z'));
    let identity = { farmName: 'PrintFarmer', timeZone: 'UTC', rowVersion: 'absent' };
    const metadata = [{
      key: 'FarmIdentity', className: 'FarmIdentitySettings', displayName: 'Farm Identity',
      description: 'Name your farm and choose the timezone for the farm-local clock.',
      group: 'FarmIdentity', order: 0,
      properties: [
        { name: 'farmName', type: 'String', attributes: [], display: {
          name: 'Farm name', description: 'Shown in the desktop and mobile web app header. Maximum 80 characters.',
          inputType: 'Text', required: true, order: 0,
        } },
        { name: 'timeZone', type: 'String', attributes: [], display: {
          name: 'Farm timezone', description: 'IANA timezone for the farm-local clock. Does not change existing schedules, timestamps, or planning hours.',
          inputType: 'Text', required: true, order: 1,
        } },
      ],
    }];
    await context.route(/\/api\/settings(?:\/|$)/, async route => {
      const path = new URL(route.request().url()).pathname;
      if (path === '/api/settings/FarmIdentity') {
        if (route.request().method() === 'POST') {
          const draft = route.request().postDataJSON();
          expect(draft.rowVersion).toBe(identity.rowVersion);
          identity = { farmName: draft.farmName, timeZone: draft.timeZone, rowVersion: 'saved' };
        }
        await route.fulfill({ json: identity });
      } else if (path === '/api/settings/metadata') {
        await route.fulfill({ json: metadata });
      } else if (path === '/api/settings/groups') {
        await route.fulfill({ json: [{ key: 'FarmIdentity', displayName: 'Farm Identity', order: 0 }] });
      } else if (path === '/api/settings') {
        await route.fulfill({ json: { FarmIdentity: identity } });
      } else {
        await route.fallback();
      }
    });
    await context.route('**/api/admin/overview', route => route.fulfill({
      json: { generatedAt: '2026-03-08T10:00:00Z', subsystems: [], attention: [] },
    }));
    await page.goto('/admin/settings?tab=identity&sub=details');
    await expect(page.getByRole('heading', { name: 'Farm Identity', exact: true }).first()).toBeVisible();
    await page.getByLabel('Farm name', { exact: false }).fill('Workshop North');
    await page.getByRole('searchbox', { name: 'Filter timezones' }).fill('Los Angeles');
    const timezone = page.getByRole('combobox', { name: 'Farm timezone' });
    await timezone.focus();
    await timezone.press('End');
    await timezone.press('Tab');
    await expect(timezone).toHaveValue('America/Los_Angeles');
    await page.getByRole('button', { name: /save/i }).click();
    const header = page.locator('header:visible').first();
    await expect(header.getByText('Workshop North', { exact: true })).toBeVisible();
    await expect(header.getByLabel('Farm time: 03:00 GMT-7 (America/Los_Angeles)', { exact: true })).toHaveText('03:00 GMT-7');
    expect(await header.locator('time').evaluate(element => {
      const time = element.getBoundingClientRect();
      const container = element.parentElement!.getBoundingClientRect();
      return time.left >= container.left && time.right <= container.right;
    })).toBe(true);
    await expect(page.getByRole('button', { name: /save/i })).toHaveCount(0);
    await expect(page.getByText('Saved Farm Identity', { exact: true })).toBeHidden();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    expect(errors).toEqual([]);
    expect(violations).toEqual([]);
    await page.screenshot({ path: testInfo.outputPath(`farm-identity-${viewport.width}.png`), fullPage: true });

    await page.getByRole('button', { name: 'Enter timezone manually' }).click();
    await page.getByRole('textbox', { name: 'Farm timezone' }).fill('US/Pacific');
    await page.getByRole('button', { name: 'Choose from timezone list' }).click();
    await expect(timezone).toHaveValue('US/Pacific');
    await page.getByRole('button', { name: /save/i }).click();
    await expect(header.getByLabel('Farm time: 03:00 GMT-7 (US/Pacific)', { exact: true })).toBeVisible();
    await page.reload();
    await expect(page.getByRole('combobox', { name: 'Farm timezone' })).toHaveValue('US/Pacific');
    expect(identity.timeZone).toBe('US/Pacific');

    await page.goto('/admin');
    const identityCard = page.getByRole('link', { name: /Farm Identity/ }).first();
    await identityCard.scrollIntoViewIfNeeded();
    await expect(identityCard).toBeInViewport();
    await page.screenshot({ path: testInfo.outputPath(`farm-identity-card-${viewport.width}.png`), fullPage: true });
    expect(errors).toEqual([]);
    expect(violations).toEqual([]);
  });
}
