import { expect, test, type Page } from '@playwright/test';

// One batched inspection of the shared Option 4 filament module at card and sidebar width; never mounts the app.
async function mountFixture(page: Page) {
  await page.route(url => url.pathname.startsWith('/api/') || !['localhost', '127.0.0.1'].includes(url.hostname), route => route.fulfill({ status: 503, json: { message: 'No live API in material fixtures' } }));
  await page.route('**/__material_fixture', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><html data-theme="dark"><head><title>Material polish fixture</title></head><body><main id="material-root"></main></body></html>' }));
  await page.goto('/__material_fixture');
  await page.addScriptTag({ type: 'module', content: `
    import RefreshRuntime from '/@react-refresh';
    RefreshRuntime.injectIntoGlobalHook(window);
    window.$RefreshReg$ = () => {};
    window.$RefreshSig$ = () => type => type;
    window.__vite_plugin_react_preamble_installed__ = true;
    const fixture = await import('/src/test/fixtures/printer-material-ui.tsx');
    fixture.mountMaterials(document.getElementById('material-root'));
  ` });
}

const screenshotDir = process.env.PF_MATERIAL_SCREENSHOT_DIR;
async function capture(page: Page, name: string) {
  await page.screenshot({ path: screenshotDir ? `${screenshotDir}/${name}.png` : test.info().outputPath(`${name}.png`), fullPage: true });
}

test('filament module resting line, rail and shared slot panel', async ({ page }) => {
  await page.setViewportSize({ width: 760, height: 1400 });
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await mountFixture(page);
  const section = (surface: string, name: string) => page.getByRole('region', { name: `${surface} ${name}`, exact: true });

  for (const surface of ['card', 'sidebar']) {
    const qidi = section(surface, 'QidiBox');
    await expect(qidi.getByTestId('filament-summary-line')).toHaveText(/1A\s*·\s*PLA Black\s*·\s*340 g/);
    await expect(qidi.getByTestId('filament-empty-dot')).toBeVisible();
    await expect(qidi.getByTestId('mmu-rack')).toHaveCount(0);
    await expect(section(surface, 'QidiBox rack active, rack hidden').getByTestId('filament-summary-line')).toHaveText(/Rack\s*·\s*ASA Purple\s*·\s*250 g/);
    const single = section(surface, 'Single toolhead with spool');
    await expect(single).not.toContainText(/T0|Tool\b|No spool assigned|tracks inventory/);
    await expect(section(surface, 'Single toolhead without spool')).not.toContainText(/No spool assigned|T0/);
  }
  await expect(page.getByText(/does not load or unload filament/)).toHaveCount(0);
  await expect(section('sidebar', 'QidiBox').getByTestId('filament-summary')).toHaveAccessibleName(/Show 4 slots/);
  await capture(page, '01-resting');

  for (const summary of await page.getByTestId('filament-summary').all()) {
    if (await summary.getAttribute('aria-expanded') === 'false') await summary.click();
  }
  await capture(page, '02-rail-expanded');

  const panelTargets: Array<[string, string]> = [
    ['QidiBox', 'mmu-gate-0'], ['QidiBox rack active, rack hidden', 'mmu-gate-2'], ['U1', 'loadout-slot-2'], ['AD5X', 'loadout-slot-0'],
  ];
  for (const surface of ['card', 'sidebar']) {
    for (const [name, testId] of panelTargets) await section(surface, name).getByTestId(testId).click();
    for (const name of ['Single toolhead with spool', 'Single toolhead without spool']) {
      await section(surface, name).locator('[data-testid^="loadout-slot-"]').first().click();
    }
  }
  const panels = page.getByTestId('slot-panel');
  await expect(panels).toHaveCount(12);
  const fit = await panels.evaluateAll(elements => elements.map(panel => {
    const group = panel.querySelector('[role="group"]') as HTMLElement;
    const buttons = Array.from(group.querySelectorAll('button')).map(button => button.getBoundingClientRect());
    const style = getComputedStyle(panel);
    const inner = panel.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
    return { label: panel.getAttribute('aria-label'), inner, rowWidth: group.scrollWidth, oneRow: new Set(buttons.map(box => Math.round(box.top))).size === 1,
      minSize: Math.min(...buttons.map(box => Math.min(box.width, box.height))), count: buttons.length };
  }));
  await test.info().attach('slot-panel-fit', { body: JSON.stringify(fit, null, 2), contentType: 'application/json' });
  for (const panel of fit) {
    expect(panel.oneRow, `${panel.label} stays on one row`).toBe(true);
    expect(panel.rowWidth, `${panel.label} fits`).toBeLessThanOrEqual(panel.inner);
    expect(panel.minSize).toBeGreaterThanOrEqual(44);
  }
  const qidiSidebar = section('sidebar', 'QidiBox').getByTestId('slot-panel');
  await expect(qidiSidebar.getByTestId('slot-action-eject')).toBeVisible();
  await expect(section('sidebar', 'U1').getByTestId('slot-action-eject')).toHaveCount(0);
  await capture(page, '03-panels-open');

  const unload = qidiSidebar.getByTestId('slot-action-unload');
  await unload.focus();
  const tooltip = page.locator('[role="tooltip"]');
  await expect(tooltip).toBeVisible();
  const box = (await tooltip.boundingBox())!;
  expect(box.x).toBeGreaterThanOrEqual(0);
  await section('sidebar', 'QidiBox').screenshot({ path: screenshotDir ? `${screenshotDir}/04-qidibox-sidebar-tooltip.png` : test.info().outputPath('04.png') });
  await page.keyboard.press('Escape');
  await expect(tooltip).toHaveCount(0);
  await expect(unload).toBeFocused();
  expect(errors).toEqual([]);
});
