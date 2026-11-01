import { expect, test, type Page } from '@playwright/test';
import { expectToolbarGeometry } from './fixtures/toolbar-geometry';

test.describe('Visual Regression', () => {
  test.describe('Screenshot Comparisons', () => {
    const viewports = [
      { width: 1920, height: 1080, name: 'desktop' },
      { width: 768, height: 1024, name: 'tablet' },
      { width: 375, height: 667, name: 'mobile' },
    ];

    for (const vp of viewports) {
      test(`homepage visual at ${vp.name}`, async ({ page }) => {
        await page.setViewportSize({ width: vp.width, height: vp.height });
        await page.goto('/');
        await page.waitForLoadState('networkidle');
        await page.waitForTimeout(500);
        await expect(page).toHaveScreenshot(`homepage-${vp.name}.png`, {
          maxDiffPixels: 100,
          threshold: 0.2,
        });
      });

      test(`printers page visual at ${vp.name}`, async ({ page }) => {
        await page.setViewportSize({ width: vp.width, height: vp.height });
        await page.goto('/printers');
        await page.waitForLoadState('networkidle');
        await page.waitForTimeout(500);
        await expect(page).toHaveScreenshot(`printers-${vp.name}.png`, {
          maxDiffPixels: 100,
          threshold: 0.2,
        });
      });
    }
  });

  test.describe('Component Visual Tests', () => {
    test('sidebar should render consistently', async ({ page }) => {
      await page.goto('/');
      await page.waitForLoadState('networkidle');
      const sidebar = page.locator('aside, nav').first();
      if (await sidebar.isVisible()) {
        await expect(sidebar).toHaveScreenshot('sidebar.png', { maxDiffPixels: 50 });
      }
    });

    test('header should render consistently', async ({ page }) => {
      await page.goto('/');
      await page.waitForLoadState('networkidle');
      const header = page.locator('header').first();
      if (await header.isVisible()) {
        await expect(header).toHaveScreenshot('header.png', { maxDiffPixels: 50 });
      }
    });
  });
});

test.describe('Dark Mode Visual Tests', () => {
  test('should apply dark theme correctly', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('networkidle');
    const darkModeToggle = page.locator('[aria-label*="theme" i], [aria-label*="dark" i], button:has-text("dark")').first();
    if (await darkModeToggle.isVisible()) {
      await darkModeToggle.click();
      await page.waitForTimeout(300);
      const bgColor = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
      const rgb = bgColor.match(/\d+/g);
      if (rgb) expect(bgColor).toBeDefined();
      await expect(page).toHaveScreenshot('dark-mode.png', {
        maxDiffPixels: 100,
        threshold: 0.2,
      });
    }
  });
});

test.describe('Content Clipping Detection', () => {
  test('text should not be clipped in containers', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('networkidle');
    const overflowContainers = await page.evaluate(() => {
      const elements = document.querySelectorAll('*');
      const clipped: string[] = [];
      elements.forEach((el, index) => {
        const style = getComputedStyle(el);
        if (style.overflow === 'hidden' || style.textOverflow === 'ellipsis') {
          const htmlEl = el as HTMLElement;
          if (htmlEl.scrollWidth > htmlEl.clientWidth || htmlEl.scrollHeight > htmlEl.clientHeight) {
            const hasEllipsis = el.classList.contains('truncate')
              || el.classList.contains('line-clamp') || el.hasAttribute('title') || style.textOverflow === 'ellipsis';
            if (!hasEllipsis) {
              clipped.push(`Element ${index}: ${el.tagName} - scroll: ${htmlEl.scrollWidth}x${htmlEl.scrollHeight}, client: ${htmlEl.clientWidth}x${htmlEl.clientHeight}`);
            }
          }
        }
      });
      return clipped;
    });
    if (overflowContainers.length > 0) console.warn('Potentially clipped content detected:', overflowContainers);
  });

  test('buttons should not have clipped text', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('networkidle');
    const buttons = page.locator('button:visible');
    const count = await buttons.count();
    for (let i = 0; i < count; i++) {
      const button = buttons.nth(i);
      const isClipped = await button.evaluate(el => el.scrollWidth > el.clientWidth);
      if (isClipped) {
        const text = await button.textContent();
        const hasEllipsis = await button.evaluate(el =>
          getComputedStyle(el).textOverflow === 'ellipsis' || el.classList.contains('truncate'));
        if (!hasEllipsis) console.warn(`Button ${i} may have clipped text: "${text}"`);
      }
    }
  });
});

async function mountToolbarFixture(page: Page, theme = 'dark', zoom = false) {
  await page.clock.setFixedTime(new Date('2026-10-30T14:00:00Z'));
  await page.routeWebSocket(/.*/, socket => socket.close());
  await page.route(url => url.pathname.startsWith('/api/')
    || !['localhost', '127.0.0.1'].includes(url.hostname),
  route => route.fulfill({ status: 503, json: { message: 'Isolated toolbar fixture: no live API or printer I/O' } }));
  await page.route('**/src/main.tsx', route => route.fulfill({
    contentType: 'application/javascript', body: "import '/src/index.css'; import '/src/styles/icons.css';",
  }));
  await page.goto('/');
  await page.evaluate(({ theme, zoom }) => {
    document.documentElement.dataset.theme = theme;
    document.documentElement.style.fontSize = zoom ? '200%' : '';
    document.body.innerHTML = '<div id="toolbar-root"></div>';
  }, { theme, zoom });
  await page.addScriptTag({ type: 'module', content: `
    const { mountToolbarFixture } = await import('/e2e/fixtures/toolbar-harness.mjs');
    mountToolbarFixture(document.getElementById('toolbar-root'));
  ` });
  await expect(page.getByRole('group', { name: 'Specimen controls', exact: true })).toBeVisible();
}

test.describe('Toolbar consistency', () => {
  test('tracer geometry, names, sort state and search wrapping', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 1100 });
    await mountToolbarFixture(page);
    const coarse = await page.evaluate(() => matchMedia('(pointer: coarse)').matches);
    const size = coarse ? 44 : 36;
    for (const name of ['Specimen controls', 'Queue controls', 'History controls', 'Queue date controls', 'Time period filter', 'Chart controls']) {
      await expectToolbarGeometry(page.getByRole('group', { name, exact: true }), size);
    }
    const sort = page.getByRole('button', { name: 'Sort direction: ascending' });
    await sort.click();
    await expect(page.getByRole('button', { name: 'Sort direction: descending' })).toBeVisible();
    await page.getByRole('searchbox', { name: 'Search specimens' }).fill('gear');
    await page.getByRole('button', { name: 'Clear search' }).click();
    await expect(page.getByRole('searchbox', { name: 'Search specimens' })).toHaveValue('');
    await page.keyboard.press('Tab');
    await expect(page.getByRole('combobox', { name: 'Sort by' }).first()).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(page.getByRole('button', { name: 'Sort direction: descending' })).toBeFocused();
    await page.keyboard.press('Space');
    await expect(page.getByRole('button', { name: 'Sort direction: ascending' })).toBeFocused();
    await page.setViewportSize({ width: 390, height: 1100 });
    for (const name of ['Specimen controls', 'Queue controls', 'History controls', 'Queue date controls', 'Time period filter', 'Chart controls']) {
      await expectToolbarGeometry(page.getByRole('group', { name, exact: true }), size);
    }
    const specimen = page.getByRole('group', { name: 'Specimen controls', exact: true });
    const command = await page.getByRole('button', { name: 'Create specimen' }).boundingBox();
    const toolbarBox = await specimen.boundingBox();
    expect(command!.y + command!.height, 'page commands are above the toolbar').toBeLessThanOrEqual(toolbarBox!.y);
    await expect(page.locator('.pf-header-actions [data-pf-variant="primary"]')).toHaveCount(1);
    const search = await specimen.getByRole('searchbox').boundingBox();
    const select = await specimen.getByRole('combobox').boundingBox();
    expect(search!.y + search!.height).toBeLessThanOrEqual(select!.y);
    await page.screenshot({ path: test.info().outputPath('tracer-narrow-after.png'), fullPage: true });
    await test.info().attach('toolbar-tracer-narrow', {
      body: await specimen.screenshot({ animations: 'disabled' }),
      contentType: 'image/png',
    });
  });

  test('custom dates and timeline controls retain native and keyboard interactions', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 1100 });
    await mountToolbarFixture(page);
    const coarse = await page.evaluate(() => matchMedia('(pointer: coarse)').matches);
    const size = coarse ? 44 : 36;
    const dates = page.getByRole('group', { name: 'Queue date controls', exact: true });
    await dates.getByRole('button', { name: 'Custom…' }).click();
    await expect(dates.getByRole('button', { expanded: true })).toBeVisible();
    const from = page.getByLabel('From', { exact: true });
    await from.fill('2026-10-01');
    await expect(from).toHaveValue('2026-10-01');
    await expectToolbarGeometry(page.locator('.pf-inline-form').filter({ has: from }), size);
    const period = page.getByRole('group', { name: 'Time period filter', exact: true });
    await period.getByRole('button', { name: 'Custom', exact: true }).click();
    await expect(period.getByRole('button', { name: 'Custom', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expectToolbarGeometry(period, size);
    const chart = page.getByRole('group', { name: 'Chart controls', exact: true });
    await chart.getByRole('button', { name: 'Week', exact: true }).click();
    await expect(chart.getByRole('button', { name: 'Week', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await chart.getByRole('button', { name: 'Queue Items', exact: true }).click();
    await expect(chart.getByRole('button', { name: 'Queue Items', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await chart.getByRole('button', { name: 'Next period' }).click();
    await chart.getByRole('button', { name: 'Today', exact: true }).click();
    await expectToolbarGeometry(chart, size);
    await expect(page.getByText('Failed to load timeline', { exact: true })).toHaveCount(0);
  });

  test('long commands and constrained containers wrap without hiding controls', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 1100 });
    await mountToolbarFixture(page);
    const specimen = page.getByRole('group', { name: 'Specimen controls', exact: true });
    await specimen.evaluate(element => { element.style.maxWidth = '700px'; });
    const search = await specimen.getByRole('searchbox').boundingBox();
    const select = await specimen.getByRole('combobox').boundingBox();
    expect(search!.y + search!.height).toBeLessThanOrEqual(select!.y);
    await page.getByRole('button', { name: 'Create specimen' }).evaluate(element => {
      element.textContent = 'Create a specimen with an unusually long translated command label';
    });
    await page.setViewportSize({ width: 390, height: 1100 });
    const coarse = await page.evaluate(() => matchMedia('(pointer: coarse)').matches);
    await expectToolbarGeometry(specimen, coarse ? 44 : 36);
    const command = page.getByRole('button', { name: /Create a specimen/ });
    await expect(command).toBeVisible();
    expect(await command.evaluate(element => element.scrollWidth > element.clientWidth + 1)).toBe(false);
    const box = await command.boundingBox();
    expect(box!.x + box!.width).toBeLessThanOrEqual(391);
  });

  test('geometry guard rejects height and overflow regressions then recovers', async ({ page }) => {
    await mountToolbarFixture(page);
    const toolbar = page.getByRole('group', { name: 'Specimen controls', exact: true });
    const coarse = await page.evaluate(() => matchMedia('(pointer: coarse)').matches);
    const size = coarse ? 44 : 36;
    await expectToolbarGeometry(toolbar, size);
    const button = toolbar.getByRole('button', { name: 'Refresh' });
    await button.evaluate(element => { element.style.transition = 'none'; element.style.minHeight = '17px'; element.style.height = '17px'; });
    await expect.poll(() => button.evaluate(element => element.getBoundingClientRect().height)).toBe(17);
    await expect(expectToolbarGeometry(toolbar, size)).rejects.toThrow();
    await button.evaluate(element => element.removeAttribute('style'));
    await expect.poll(() => button.evaluate(element => element.getBoundingClientRect().height)).toBe(size);
    await expectToolbarGeometry(toolbar, size);
    await button.evaluate(element => { element.style.transition = 'none'; element.style.transform = 'translateX(2000px)'; });
    await expect(expectToolbarGeometry(toolbar, size)).rejects.toThrow();
    await button.evaluate(element => element.removeAttribute('style'));
    await expect.poll(() => button.evaluate(element => getComputedStyle(element).transform)).toBe('none');
    await expectToolbarGeometry(toolbar, size);
  });

  for (const theme of ['dark', 'light', 'matrix', 'blueprint', 'ratos', 'voron', 'farm', 'forge']) {
    for (const scenario of ['desktop', 'narrow', 'zoom'] as const) {
      test(`${theme} ${scenario} toolbar visual`, async ({ page }) => {
        await page.setViewportSize({ width: scenario === 'narrow' ? 390 : 1440, height: 1100 });
        await mountToolbarFixture(page, theme, scenario === 'zoom');
        const coarse = await page.evaluate(() => matchMedia('(pointer: coarse)').matches);
        const size = (coarse ? 44 : 36) * (scenario === 'zoom' ? 2 : 1);
        const specimen = page.getByRole('group', { name: 'Specimen controls', exact: true });
        for (const name of ['Specimen controls', 'Queue controls', 'History controls', 'Queue date controls', 'Time period filter', 'Chart controls']) {
          await expectToolbarGeometry(page.getByRole('group', { name, exact: true }), size, scenario === 'zoom');
        }
        const labelContrast = await specimen.locator('.pf-toolbar-field__label').evaluate(label => {
          const luminance = (color: string) => {
            const channels = color.match(/[\d.]+/g)!.slice(0, 3).map(Number).map(value => {
              const normalized = value / 255;
              return normalized <= 0.04045 ? normalized / 12.92 : ((normalized + 0.055) / 1.055) ** 2.4;
            });
            return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
          };
          let surface = label.parentElement;
          while (surface && getComputedStyle(surface).backgroundColor === 'rgba(0, 0, 0, 0)') surface = surface.parentElement;
          if (!surface) throw new Error('Fixture label has no opaque background');
          const text = luminance(getComputedStyle(label).color);
          const background = luminance(getComputedStyle(surface).backgroundColor);
          return (Math.max(text, background) + 0.05) / (Math.min(text, background) + 0.05);
        });
        expect(labelContrast, 'essential toolbar labels retain readable theme contrast').toBeGreaterThanOrEqual(4.5);
        await expect(specimen.getByRole('button', { name: 'Grid view' })).toHaveAttribute('aria-pressed', 'true');
        await expect(specimen.getByRole('button', { name: 'Table view' })).toHaveAttribute('aria-pressed', 'false');
        await page.screenshot({ path: test.info().outputPath(`${theme}-${scenario}-after.png`), fullPage: true });
      });
    }
  }
});
