import { expect, test, type Page } from '@playwright/test';

async function mountFixture(page: Page) {
  // The fixture does not need Vite HMR, whose separate port is not published by WSLC.
  await page.routeWebSocket(url => ['localhost', '127.0.0.1'].includes(url.hostname) && url.port === '3001',
    socket => socket.send(JSON.stringify({ type: 'connected' })));
  await page.route(url => url.pathname.startsWith('/api/')
    || !['localhost', '127.0.0.1'].includes(url.hostname),
  route => route.fulfill({ status: 503, json: { message: 'No live API in card density fixtures' } }));
  await page.route('**/__card_density_fixture', route => route.fulfill({
    contentType: 'text/html',
    body: '<!doctype html><html data-theme="dark"><head><title>Printer card density</title></head><body><div id="fixture-root"></div></body></html>',
  }));
  await page.goto('/__card_density_fixture');
  await page.addScriptTag({ type: 'module', content: `
    import RefreshRuntime from '/@react-refresh';
    RefreshRuntime.injectIntoGlobalHook(window);
    window.$RefreshReg$ = () => {};
    window.$RefreshSig$ = () => type => type;
    window.__vite_plugin_react_preamble_installed__ = true;
    const fixture = await import('/src/test/fixtures/printer-card-density.tsx');
    fixture.mountPrinterCards(document.getElementById('fixture-root'));
  ` });
  await expect(page.getByRole('button', { name: 'Detailed', exact: true })).toBeVisible();
}

async function inspectCards(page: Page, mode: 'detailed' | 'compact', columns: number) {
  const cards = page.getByTestId(`${mode}-printer-image`);
  await expect(cards.first()).toBeVisible();
  await expect.poll(async () => cards.evaluateAll(images => {
    const firstTop = images[0].closest('article')!.getBoundingClientRect().top;
    return images.filter(image => Math.abs(image.closest('article')!.getBoundingClientRect().top - firstTop) < 1).length;
  })).toBe(columns);
  const overflow = await page.locator('main[data-main-content]').evaluate(main => main.scrollWidth - main.clientWidth);
  expect(overflow, 'no horizontal page overflow').toBeLessThanOrEqual(1);
  const cardOverflow = await cards.evaluateAll(images => images.map(image => {
    const card = image.closest('article') ?? image.closest('[data-tour]') ?? image.parentElement!.parentElement!;
    const bounds = card.getBoundingClientRect();
    const escaped = Array.from(card.querySelectorAll('button, input')).filter(control => {
      const rect = control.getBoundingClientRect();
      return rect.width > 0 && (rect.right > bounds.right + 1 || rect.left < bounds.left - 1);
    });
    return escaped.length;
  }));
  expect(cardOverflow, 'all controls fit within their cards').toEqual(cardOverflow.map(() => 0));
  if (mode === 'compact') {
    const rows = await cards.evaluateAll(images => images.map(image => {
      const progress = image.nextElementSibling!;
      return {
        progress: progress.getBoundingClientRect().height,
        eta: progress.nextElementSibling!.getBoundingClientRect().height,
      };
    }));
    expect(new Set(rows.map(row => row.progress)).size, 'offline temperature rows retain their height').toBe(1);
    expect(new Set(rows.map(row => row.eta)).size, 'idle and active ETA rows retain their height').toBe(1);
  }
}

test('printer cards fill the grid with the sidebar open and closed, including virtualized rows', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.setViewportSize({ width: 1712, height: 1100 });
  await mountFixture(page);

  for (const virtualized of [false, true]) {
    if (virtualized) await page.getByRole('button', { name: 'Toggle virtualization' }).click();
    for (const mode of ['detailed', 'compact'] as const) {
      await page.getByRole('button', { name: mode === 'detailed' ? 'Detailed' : 'Compact', exact: true }).click();
      await inspectCards(page, mode, mode === 'detailed' ? 3 : 5);
      await page.screenshot({ path: test.info().outputPath(`${mode}-${virtualized ? 'virtual' : 'small'}-sidebar.png`), fullPage: false });
      await page.getByRole('button', { name: 'Toggle sidebar' }).click();
      await inspectCards(page, mode, mode === 'detailed' ? 4 : 5);
      await page.getByRole('button', { name: 'Toggle sidebar' }).click();
    }
  }
  expect(errors).toEqual([]);
});

test('detailed controls fit at the three-column boundary and preserve a tight ETA slot', async ({ page }) => {
  await page.setViewportSize({ width: 1472, height: 1100 });
  await mountFixture(page);
  await inspectCards(page, 'detailed', 3);
  const cards = page.locator('.pf-detailed-printer-card');
  const slots = await cards.evaluateAll(elements => elements.slice(0, 3).map(card => {
    const progress = card.querySelector('[role="progressbar"]')!;
    const eta = card.querySelector('[data-testid="estimated-completion-placeholder"]')
      ?? Array.from(card.children).find(child => child.textContent?.includes('done ~'))!;
    return {
      height: eta.getBoundingClientRect().height,
      gap: eta.getBoundingClientRect().bottom - progress.getBoundingClientRect().bottom,
    };
  }));
  expect(slots[0].height).toBe(slots[1].height);
  expect(slots[0].height).toBe(slots[2].height);
  expect(slots.every(slot => slot.gap <= 28)).toBe(true);
  await expect(cards.first().getByTitle('A deliberately long printer model designation')).toBeVisible();
  await page.screenshot({ path: test.info().outputPath('detailed-minimum-width.png'), fullPage: false });
});

test('cards stay within a narrow single-column viewport', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 1000 });
  await mountFixture(page);
  for (const mode of ['detailed', 'compact'] as const) {
    await page.getByRole('button', { name: mode === 'detailed' ? 'Detailed' : 'Compact', exact: true }).click();
    await inspectCards(page, mode, 1);
  }
});
