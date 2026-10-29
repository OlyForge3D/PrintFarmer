import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';

async function mountSidebar(page: Page, scope: 'system' | 'user' = 'system') {
  await page.route('**/src/main.tsx', route => route.fulfill({
    contentType: 'application/javascript',
    body: "import '/src/index.css';",
  }));
  await page.route(url => url.pathname.startsWith('/api/'), route => route.abort());
  await page.goto('/');
  await page.evaluate(() => {
    document.body.innerHTML = '<div id="settings-sidebar-root" class="h-screen min-h-0 flex flex-col overflow-hidden p-4"></div>';
  });
  await page.addScriptTag({
    type: 'module',
    content: `
      import { mountSettingsSidebar } from '/e2e/fixtures/settings-sidebar-harness.mjs';
      mountSettingsSidebar('${scope}');
    `,
  });
}

for (const viewport of [{ width: 1440, height: 900 }, { width: 1024, height: 600 }]) {
  test(`admin sidebar scrolls to System Config without moving content at ${viewport.width}x${viewport.height}`, async ({ page }) => {
    await page.setViewportSize(viewport);
    await mountSidebar(page);
    const nav = page.getByRole('navigation', { name: 'Farm & Admin Settings categories' });
    await expect(nav.getByRole('button', { name: 'Farm Defaults', exact: true })).toBeVisible();
    const scroller = nav.locator(':scope > div');
    const dimensions = await scroller.evaluate(element => ({
      client: element.clientHeight,
      scroll: element.scrollHeight,
      bottom: element.getBoundingClientRect().bottom,
    }));
    expect(dimensions.scroll, 'the grouped list must actually overflow').toBeGreaterThan(dimensions.client);
    const frameBox = await page.getByTestId('settings-frame').boundingBox();
    expect(dimensions.bottom, 'sidebar scrollport stays inside the settings frame')
      .toBeLessThanOrEqual(frameBox!.y + frameBox!.height);

    await nav.hover();
    await page.mouse.wheel(0, dimensions.scroll);
    await expect.poll(() => scroller.evaluate(element => element.scrollTop)).toBeGreaterThan(0);
    const system = nav.getByRole('button', { name: 'System Config', exact: true });
    await expect(system).toBeInViewport();
    await system.click();
    await expect(page.getByRole('status', { name: 'Selected destination' }))
      .toHaveText('/admin/settings?tab=general&sub=system');
    expect(await page.locator('.pf-settings-scroll-pane').evaluate(element => element.scrollTop)).toBe(0);

    await scroller.evaluate(element => { element.scrollTop = 0; });
    await nav.getByRole('button', { name: 'Farm Defaults', exact: true }).focus();
    await page.keyboard.press('End');
    await expect(system).toBeFocused();
    await expect(system).toBeInViewport();
    await page.keyboard.press('Home');
    await expect(nav.getByRole('button', { name: 'Farm Defaults', exact: true })).toBeFocused();
    expect(await scroller.evaluate(element => element.scrollTop)).toBe(0);
    expect(await page.evaluate(() => document.documentElement.scrollHeight <= innerHeight)).toBe(true);
  });
}

test('a short personal navigation list keeps its content height instead of filling the frame', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await mountSidebar(page, 'user');
  const nav = page.getByRole('navigation', { name: 'User Settings categories' });
  await expect(nav.getByRole('button', { name: 'Profile', exact: true })).toBeVisible();
  const box = await nav.boundingBox();
  expect(box!.height).toBeLessThan(100);
  expect(await nav.locator(':scope > div').evaluate(element => element.scrollHeight === element.clientHeight)).toBe(true);
});

test('mobile navigation still scrolls to System Config and restores trigger focus', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 600 });
  await mountSidebar(page);
  const trigger = page.getByRole('button', { name: 'Settings section: Farm Defaults', exact: true });
  await trigger.click();
  const menu = page.getByRole('navigation', { name: 'Settings categories', exact: true });
  const system = menu.getByRole('button', { name: 'System Config', exact: true });
  await system.scrollIntoViewIfNeeded();
  await expect(system).toBeInViewport();
  await system.click();
  await expect(menu).not.toBeVisible();
  await expect(trigger).toBeFocused();
  await expect(page.getByRole('status', { name: 'Selected destination' }))
    .toHaveText('/admin/settings?tab=general&sub=system');
});

for (const width of [390, 1440]) {
  test(`inline search shortcut fits and works from the focused input at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await mountSidebar(page);
    const search = page.getByRole('combobox', { name: 'Search all settings' });
    await expect(search).toHaveAttribute('placeholder', /^Search all settings \((Ctrl K|⌘K)\)$/);
    const space = await search.evaluate((element: HTMLInputElement) => {
      const style = getComputedStyle(element);
      const context = document.createElement('canvas').getContext('2d')!;
      context.font = style.font;
      return {
        text: context.measureText(element.placeholder).width,
        available: element.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight),
      };
    });
    expect(space.text, 'the shortcut must not be clipped inside the search box').toBeLessThanOrEqual(space.available);
    await search.fill('printer file index');
    await search.press('Control+k');
    await expect(page.getByRole('status', { name: 'Selected destination' })).toHaveText('palette');
    await expect(search).toHaveValue('printer file index');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  });
}
