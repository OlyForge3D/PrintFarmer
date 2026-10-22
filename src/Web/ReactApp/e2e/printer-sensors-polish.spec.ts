import { expect, test } from '@playwright/test';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { SELECTABLE_THEMES } from '../src/design-system/themes/registry';

test('whole sensor sections match approved hierarchy, visible glyphs, responsive type and one thermal grid at matched widths', async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 1200 });
  await page.route(url => /^https?:$/.test(url.protocol) && (url.pathname.startsWith('/api/') || !['localhost', '127.0.0.1'].includes(url.hostname)),
    route => route.fulfill({ status: 503, json: { message: 'Isolated fixture: hardware access blocked' } }));
  const comparisons: object[] = [];
  for (const contentWidth of [335, 560]) {
    await page.goto('/src/test/fixtures/printer-sensors.html?sidebar-online');
    const production = [];
    for (const surface of ['card', 'sidebar']) {
      const root = page.getByRole('region', { name: surface, exact: true });
      await expect(root.getByRole('spinbutton', { name: 'Hotend target temperature' })).toBeVisible();
      await root.evaluate((node, width) => {
        (node as HTMLElement).style.width = `${width + 100}px`;
        for (const section of node.querySelectorAll<HTMLElement>('[data-printer-sensor-section]')) {
          section.style.width = `${width + 34}px`;
          const body = section.querySelector<HTMLElement>('[data-sensor-body]')!;
          const style = getComputedStyle(body);
          section.style.width = `${width + parseFloat(style.paddingLeft) + parseFloat(style.paddingRight) + 2}px`;
        }
      }, contentWidth);
      const metrics = await root.evaluate(node => {
        const thermal = node.querySelector('[data-printer-sensor-section="Temperatures"]')!;
        const misc = node.querySelector('[data-printer-sensor-section="Miscellaneous"]')!;
        const table = thermal.querySelector('table')!;
        const rows = [...table.querySelectorAll('[data-thermal-row]')];
        const columns = rows.map(row => [...row.children].map(cell => cell.getBoundingClientRect().left));
        const font = (element: Element) => ({ size: getComputedStyle(element).fontSize, weight: getComputedStyle(element).fontWeight });
        const automatic = misc.querySelector('[data-testid="sensor-automatic"]')!;
        const headingButton = thermal.querySelector('h3 button')!;
        const headingBounds = headingButton.getBoundingClientRect();
        const headingIcons = headingButton.querySelectorAll('svg');
        const extraReading = thermal.querySelector('[aria-label*="Humidity:"]')!.parentElement!;
        const extraName = extraReading.closest('[data-sensor-name]')!;
        const binary = misc.querySelector('[role="switch"]')!;
        const track = binary.querySelector('[aria-hidden="true"]')!;
        const knob = track.firstElementChild!;
        return {
          width: table.getBoundingClientRect().width,
          tables: thermal.querySelectorAll('table').length,
          headings: [...table.querySelectorAll('thead th')].map(th => th.textContent),
          heading: font(thermal.querySelector('[data-sensor-heading]')!),
          miscHeading: font(misc.querySelector('[data-sensor-heading]')!),
          thermalLabel: font(rows[0].querySelector('th')!),
          thermalReading: font(rows[0].querySelector('td:nth-child(3)')!),
          fanLabel: font(automatic.querySelector('[data-sensor-name]')!),
          fanReading: font(automatic.querySelector('[aria-label*="Speed:"]')!),
          rpm: font(automatic.querySelector('[aria-label*="RPM:"]')!),
          thermalGlyphs: rows.map(row => row.querySelector('[data-sensor-glyph] svg')!.getBoundingClientRect().width),
          fanGlyphs: [...misc.querySelectorAll('[data-testid]')].filter(row => row.querySelector('[data-sensor-glyph="Fan"]'))
            .map(row => row.querySelector('[data-sensor-glyph] svg')!.getBoundingClientRect().width),
          headingGlyph: thermal.querySelector('h3 svg')!.getBoundingClientRect().width,
          headingHeight: headingBounds.height,
          headingInset: headingBounds.right - headingIcons[1].getBoundingClientRect().right,
          extraReadingIndent: extraReading.getBoundingClientRect().left - extraName.getBoundingClientRect().left,
          binaryTrack: { width: track.getBoundingClientRect().width, height: track.getBoundingClientRect().height },
          binaryKnob: { width: knob.getBoundingClientRect().width, height: knob.getBoundingClientRect().height },
          binaryHit: { width: binary.getBoundingClientRect().width, height: binary.getBoundingClientRect().height },
          stepFont: font(misc.querySelector('[aria-label^="Decrease"]')!),
          columnSpread: [0, 1, 2, 3].map(index => Math.max(...columns.map(row => row[index])) - Math.min(...columns.map(row => row[index]))),
        };
      });
      expect(metrics.tables).toBe(1);
      expect(metrics.headings).toEqual(['Name', 'State', 'Current', 'Target']);
      expect(metrics.width).toBeCloseTo(contentWidth);
      expect(metrics.thermalGlyphs.every(width => width === 18)).toBe(true);
      expect(metrics.fanGlyphs).toEqual([18, 18, 18, 18]);
      expect(metrics.columnSpread.every(spread => spread < 1)).toBe(true);
      expect(metrics.binaryHit.width).toBeGreaterThanOrEqual(44);
      expect(metrics.binaryHit.height).toBeGreaterThanOrEqual(44);
      await expect(root.getByRole('region', { name: 'Miscellaneous', exact: true })).toBeVisible();
      await expect(root.locator('[data-printer-sensor-section]').getByRole('img', { name: /Fan|temperature/i })).toHaveCount(0);
      for (const title of ['Temperatures', 'Miscellaneous']) {
        await root.getByRole('region', { name: title, exact: true }).screenshot({ path: test.info().outputPath(`${surface}-${title}-${contentWidth}px.png`) });
      }
      production.push({ surface, metrics });
    }
    await expect(page.locator('[data-fixture-command]')).toHaveCount(0);
    await page.goto(pathToFileURL(resolve(process.cwd(), '..', '..', '..', 'docs', 'design', 'printer-sensors-options.html')).href);
    await page.locator('.preview').evaluate((node, width) => {
      const element = node as HTMLElement;
      element.style.width = `${width + 34}px`;
      element.style.maxWidth = 'none';
      const style = getComputedStyle(node.querySelector('.panel-body')!);
      element.style.width = `${width + parseFloat(style.paddingLeft) + parseFloat(style.paddingRight) + 2}px`;
    }, contentWidth);
    const reference = await page.locator('#panels').evaluate(node => {
      const font = (element: Element) => ({ size: getComputedStyle(element).fontSize, weight: getComputedStyle(element).fontWeight });
      const thermal = node.querySelector('.panel')!;
      const misc = node.querySelectorAll('.panel')[1];
      const automatic = misc.querySelector('[data-sensor="hotend-fan"]')!;
      const headingButton = thermal.querySelector('.panel-heading button')!;
      const headingBounds = headingButton.getBoundingClientRect();
      const headingIcons = headingButton.querySelectorAll('svg');
      const extraReading = thermal.querySelector('.extra-reading')!;
      const track = misc.querySelector('.toggle-track')!;
      const knob = getComputedStyle(track, '::after');
      return {
        width: thermal.querySelector('table')!.getBoundingClientRect().width,
        heading: font(thermal.querySelector('.panel-heading button')!),
        miscHeading: font(misc.querySelector('.panel-heading button')!),
        thermalLabel: font(thermal.querySelector('tbody th')!),
        thermalReading: font(thermal.querySelector('.current-cell')!),
        fanLabel: font(automatic.querySelector('.output-name')!),
        fanReading: font(automatic.querySelector('.reading')!),
        rpm: font(automatic.querySelector('.rpm')!),
        headingGlyph: thermal.querySelector('.panel-heading svg')!.getBoundingClientRect().width,
        headingHeight: headingBounds.height,
        headingInset: headingBounds.right - headingIcons[1].getBoundingClientRect().right,
        thermalGlyph: thermal.querySelector('.sensor-name svg')!.getBoundingClientRect().width,
        fanGlyph: automatic.querySelector('.output-name svg')!.getBoundingClientRect().width,
        extraReadingIndent: extraReading.getBoundingClientRect().left - extraReading.closest('.sensor-name')!.getBoundingClientRect().left,
        binaryTrack: { width: track.getBoundingClientRect().width, height: track.getBoundingClientRect().height },
        binaryKnob: { width: parseFloat(knob.width), height: parseFloat(knob.height) },
        stepFont: font(misc.querySelector('.step-control')!),
      };
    });
    expect(reference.width).toBeCloseTo(contentWidth);
    expect(reference.thermalGlyph).toBe(18);
    expect(reference.fanGlyph).toBe(18);
    await expect(page.getByRole('button', { name: 'Miscellaneous', exact: true })).toBeVisible();
    for (const { metrics } of production) {
      expect(metrics.heading).toEqual(reference.heading);
      expect(metrics.miscHeading).toEqual(reference.miscHeading);
      expect(metrics.thermalLabel).toEqual(reference.thermalLabel);
      expect(metrics.thermalReading).toEqual(reference.thermalReading);
      expect(metrics.fanLabel).toEqual(reference.fanLabel);
      expect(metrics.fanReading).toEqual(reference.fanReading);
      expect(metrics.rpm).toEqual(reference.rpm);
      expect(metrics.headingGlyph).toEqual(reference.headingGlyph);
      expect(metrics.headingHeight).toBeCloseTo(reference.headingHeight);
      expect(metrics.headingInset).toBeCloseTo(reference.headingInset);
      expect(metrics.extraReadingIndent).toBeCloseTo(reference.extraReadingIndent);
      expect(metrics.binaryTrack).toEqual(reference.binaryTrack);
      expect(metrics.binaryKnob).toEqual(reference.binaryKnob);
      expect(metrics.stepFont).toEqual(reference.stepFont);
    }
    await page.locator('#panels').screenshot({ path: test.info().outputPath(`reference-${contentWidth}px.png`) });
    comparisons.push({ contentWidth, production, reference });
  }
  await test.info().attach('whole-section-parity.json', { body: JSON.stringify(comparisons, null, 2), contentType: 'application/json' });
});

test('opened preset menu is bounded, viewport-safe and keyboard dismissible without applying a command', async ({ page }) => {
  await page.route(url => url.pathname.startsWith('/api/') || !['localhost', '127.0.0.1'].includes(url.hostname),
    route => route.fulfill({ status: 503, json: { message: 'Hardware access blocked' } }));
  const measurements: object[] = [];
  for (const [width, height] of [[1242, 801], [360, 640], [320, 480]]) {
    await page.setViewportSize({ width, height });
    await page.goto('/src/test/fixtures/printer-sensors.html?sidebar-online');
    for (const surface of ['card', 'sidebar']) {
      const scope = page.getByRole('region', { name: surface, exact: true });
      const trigger = scope.getByRole('button', { name: 'Apply temperature preset' });
      await trigger.click();
      const menu = page.getByRole('menu', { name: 'Temperature presets' });
      await expect(menu).toBeVisible();
      await expect(menu.getByRole('menuitem')).toHaveCount(15);
      const bounds = await menu.evaluate(node => ({
        x: node.getBoundingClientRect().x, y: node.getBoundingClientRect().y,
        width: node.getBoundingClientRect().width, height: node.getBoundingClientRect().height,
        client: node.clientHeight, scroll: node.scrollHeight,
        itemHeights: [...node.querySelectorAll('[role="menuitem"]')].map(item => item.getBoundingClientRect().height),
      }));
      expect(bounds.height).toBeLessThanOrEqual(240);
      expect(bounds.scroll).toBeGreaterThan(bounds.client);
      expect(bounds.y).toBeGreaterThanOrEqual(8);
      expect(bounds.y + bounds.height).toBeLessThanOrEqual(height - 8);
      expect(bounds.x).toBeGreaterThanOrEqual(8);
      expect(bounds.x + bounds.width).toBeLessThanOrEqual(width - 8);
      expect(bounds.itemHeights.every(value => value >= 44)).toBe(true);
      await page.keyboard.press('End');
      await expect(menu.getByRole('menuitem', { name: /^Bed Cooldown,/ })).toBeFocused();
      await page.keyboard.press('Home');
      await expect(menu.getByRole('menuitem', { name: /^Both heaters ABS,/ })).toBeFocused();
      await expect(page.locator('[data-fixture-command]')).toHaveCount(0);
      await menu.screenshot({ path: test.info().outputPath(`${surface}-presets-${width}px.png`) });
      await page.keyboard.press('Escape');
      await expect(menu).toHaveCount(0);
      await expect(trigger).toBeFocused();
      await trigger.press('ArrowUp');
      await expect(page.getByRole('menuitem', { name: /^Bed Cooldown,/ })).toBeFocused();
      await page.keyboard.press('Tab');
      await expect(menu).toHaveCount(0);
      await expect(page.locator('[data-fixture-command]')).toHaveCount(0);
      measurements.push({ width, height, surface, menu: bounds });
    }
  }
  await test.info().attach('opened-preset-geometry.json', { body: JSON.stringify(measurements, null, 2), contentType: 'application/json' });
});

test('reference and production numeric fields match compact visual heights with full effective hit areas', async ({ browser, baseURL }) => {
  const measurements: object[] = [];
  for (const [width, coarse] of [[1100, false], [360, false], [320, false], [1100, true]] as const) {
    const context = await browser.newContext({ baseURL, viewport: { width, height: 1200 }, hasTouch: coarse, isMobile: coarse });
    try {
      const page = await context.newPage();
      await page.route(url => /^https?:$/.test(url.protocol) && (url.pathname.startsWith('/api/') || !['localhost', '127.0.0.1'].includes(url.hostname)),
        route => route.fulfill({ status: 503, json: { message: 'Isolated fixture: hardware access blocked' } }));
      await page.goto('/src/test/fixtures/printer-sensors.html');
      expect(await page.evaluate(() => matchMedia('(pointer: coarse)').matches)).toBe(coarse);
      const input = page.getByRole('region', { name: 'card', exact: true }).getByRole('spinbutton', { name: 'Hotend target temperature' });
      await expect(input).toBeVisible();
      const production = await input.evaluate(node => ({
        visible: node.getBoundingClientRect().height,
        hit: node.closest('[data-value-hit-area]')!.getBoundingClientRect().height,
        font: getComputedStyle(node).fontSize,
        padding: getComputedStyle(node).paddingTop,
      }));
      const area = input.locator('..');
      await area.scrollIntoViewIfNeeded();
      const bounds = await area.boundingBox();
      if (!bounds) throw new Error('Numeric hit area is not visible');
      await page.mouse.click(bounds.x + bounds.width / 2, bounds.y + 1);
      await expect(input).toBeFocused();
      await input.press('Escape');
      await input.press('Tab');
      await expect(input).not.toBeFocused();
      await page.goto(pathToFileURL(resolve(process.cwd(), '..', '..', '..', 'docs', 'design', 'printer-sensors-options.html')).href);
      const reference = await page.locator('.number-field').first().evaluate(node => ({
        visible: node.getBoundingClientRect().height,
        hit: node.closest('[data-value-hit-area]')!.getBoundingClientRect().height,
        font: getComputedStyle(node.querySelector('input')!).fontSize,
        padding: getComputedStyle(node.querySelector('input')!).paddingTop,
      }));
      expect(production.visible).toBe(32);
      expect(reference.visible).toBe(production.visible);
      expect(production.hit).toBeGreaterThanOrEqual(44);
      expect(reference.hit).toBeGreaterThanOrEqual(44);
      expect(production.font).toBe('14px');
      expect(reference.font).toBe(production.font);
      expect(production.padding).toBe('0px');
      expect(reference.padding).toBe(production.padding);
      expect(await page.locator('.number-field').evaluateAll(nodes => nodes.every(node => node.getBoundingClientRect().height === 32))).toBe(true);
      const referenceInput = page.locator('input[type="number"]').first();
      const referenceArea = referenceInput.locator('xpath=ancestor::label');
      await referenceArea.scrollIntoViewIfNeeded();
      const referenceBounds = await referenceArea.boundingBox();
      if (!referenceBounds) throw new Error('Reference numeric hit area is not visible');
      await page.mouse.click(referenceBounds.x + referenceBounds.width / 2, referenceBounds.y + 1);
      await expect(referenceInput).toBeFocused();
      measurements.push({ width, coarse, production, reference });
    } finally { await context.close(); }
  }
  await test.info().attach('numeric-geometry.json', { body: JSON.stringify(measurements, null, 2), contentType: 'application/json' });
});

test('both surfaces reject over-limit presets atomically and retain valid, unknown-limit and cooldown actions', async ({ page }) => {
  await page.route(url => url.pathname.startsWith('/api/') || !['localhost', '127.0.0.1'].includes(url.hostname),
    route => route.fulfill({ status: 503, json: { message: 'Isolated fixture: hardware access blocked' } }));
  for (const unknown of [false, true]) {
    await page.goto(`/src/test/fixtures/printer-sensors.html?sidebar-online${unknown ? '&unknown-limits' : ''}`);
    const commands = page.locator('[data-fixture-command]');
    let count = 0;
    for (const surface of ['card', 'sidebar']) {
      const scope = page.getByRole('region', { name: surface, exact: true });
      const presets = scope.getByRole('button', { name: 'Apply temperature preset' });
      const choosePreset = async (value: string) => {
        await presets.click();
        await page.locator(`[role="menuitem"][data-temperature-preset="${value}"]`).click();
      };
      if (!unknown) {
        for (const preset of ['PC', 'hotend:PC', 'bed:PC']) {
          await choosePreset(preset);
          await expect(commands).toHaveCount(count);
        }
        await expect(page.getByText(/exceeds maximum.*Nothing sent/).first()).toBeVisible();
      }
      for (const [preset, targets] of unknown ? [
        ['PC', { hotend: 280, bed: 110 }], ['hotend:PC', { hotend: 280 }], ['bed:PC', { bed: 110 }],
      ] as const : [
        ['PLA', { hotend: 210, bed: 60 }], ['hotend:PLA', { hotend: 210 }], ['bed:PLA', { bed: 60 }],
      ] as const) {
        await choosePreset(preset);
        await expect(commands).toHaveCount(++count);
        await expect(commands.last()).toHaveText(JSON.stringify({ printerId: `sensor-poc-${surface}`, ...targets }));
      }
      await scope.getByRole('button', { name: 'Cooldown' }).click();
      await expect(commands).toHaveCount(++count);
      await expect(commands.last()).toHaveText(JSON.stringify({ printerId: `sensor-poc-${surface}`, hotend: 0, bed: 0 }));
    }
  }
});

test('unknown heater limits warn accessibly while allowing integer entry on both surfaces', async ({ page }) => {
  await page.route(url => url.pathname.startsWith('/api/') || !['localhost', '127.0.0.1'].includes(url.hostname),
    route => route.fulfill({ status: 503, json: { message: 'Isolated fixture: hardware access blocked' } }));
  await page.goto('/src/test/fixtures/printer-sensors.html?sidebar-online&unknown-limits');
  const commands = page.locator('[data-fixture-command]');
  let count = 0;
  for (const surface of ['card', 'sidebar']) {
    const scope = page.getByRole('region', { name: surface, exact: true });
    await expect(scope.getByRole('button', { name: 'Cooldown' })).toBeEnabled();
    for (const [name, heater] of [['Hotend', 'hotend'], ['Bed', 'bed']] as const) {
      const input = scope.getByRole('spinbutton', { name: `${name} target temperature` });
      await expect(input).toBeEnabled();
      await expect(input).not.toHaveAttribute('max');
      await expect(input).toHaveAccessibleDescription(new RegExp(`${name} maximum temperature unknown.*firmware limits.*does not enforce an upper bound`));
      await expect(scope.getByText(new RegExp(`${name} maximum temperature unknown`))).toBeVisible();
      await input.fill('65.5');
      await input.press('Enter');
      await input.press('Tab');
      await expect(commands).toHaveCount(count);
      await input.fill('65');
      await input.press('Enter');
      await input.press('Tab');
      await expect(commands).toHaveCount(++count);
      await expect(commands.last()).toHaveText(JSON.stringify({ printerId: `sensor-poc-${surface}`, [heater]: 65 }));
      await expect(input).toHaveAccessibleDescription(/maximum temperature unknown/);
    }
  }
  await page.setViewportSize({ width: 320, height: 1000 });
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('both production surfaces enforce persisted heater limits and integer commands without descriptors', async ({ page }) => {
  await page.route(url => url.pathname.startsWith('/api/') || !['localhost', '127.0.0.1'].includes(url.hostname),
    route => route.fulfill({ status: 503, json: { message: 'Isolated fixture: hardware access blocked' } }));
  await page.goto('/src/test/fixtures/printer-sensors.html?sidebar-online');
  const commands = page.locator('[data-fixture-command]');
  let count = 0;
  for (const surface of ['card', 'sidebar']) {
    const scope = page.getByRole('region', { name: surface, exact: true });
    for (const [name, maximum, fractional, integer, heater] of [
      ['Hotend', 250, '210.5', '220', 'hotend'],
      ['Bed', 100, '60.5', '65', 'bed'],
    ] as const) {
      const input = scope.getByRole('spinbutton', { name: `${name} target temperature` });
      await expect(input).toBeEnabled();
      await expect(input).toHaveAttribute('max', String(maximum));
      await expect(input).toHaveAttribute('step', '1');
      for (const invalid of [String(maximum + 1), '2100', fractional]) {
        await input.fill(invalid);
        await input.press('Enter');
        await input.press('Tab');
        await expect(input).toHaveAttribute('aria-invalid', 'true');
        await expect(commands).toHaveCount(count);
      }
      await input.fill(integer);
      await input.press('Enter');
      await input.press('Tab');
      await expect(commands).toHaveCount(++count);
      await expect(commands.last()).toHaveText(JSON.stringify({ printerId: `sensor-poc-${surface}`, [heater]: Number(integer) }));
    }
  }
});

test('sensor target text and boundaries remain legible in every shipping theme', async ({ page }) => {
  await page.route(url => url.pathname.startsWith('/api/') || !['localhost', '127.0.0.1'].includes(url.hostname),
    route => route.fulfill({ status: 503, json: { message: 'Isolated fixture: hardware access blocked' } }));
  await page.goto('/src/test/fixtures/printer-sensors.html');
  const input = page.getByRole('region', { name: 'card', exact: true }).getByRole('spinbutton', { name: 'Set Part Cooling Speed' });
  await expect(input).toBeVisible();
  await page.addStyleTag({ content: '* { transition: none !important; animation: none !important; }' });
  for (const theme of SELECTABLE_THEMES) {
    await page.evaluate(value => { document.documentElement.dataset.theme = value; }, theme);
    const contrast = await input.evaluate(element => {
      const context = document.createElement('canvas').getContext('2d');
      if (!context) throw new Error('Canvas color conversion unavailable');
      const luminance = (color: string) => {
        context.clearRect(0, 0, 1, 1);
        context.fillStyle = color;
        context.fillRect(0, 0, 1, 1);
        const [red, green, blue] = Array.from(context.getImageData(0, 0, 1, 1).data).slice(0, 3).map(channel => {
          const normalized = channel / 255;
          return normalized <= 0.04045 ? normalized / 12.92 : ((normalized + 0.055) / 1.055) ** 2.4;
        });
        return red * 0.2126 + green * 0.7152 + blue * 0.0722;
      };
      const ratio = (first: string, second: string) => {
        const values = [luminance(first), luminance(second)];
        return (Math.max(...values) + 0.05) / (Math.min(...values) + 0.05);
      };
      const style = getComputedStyle(element);
      return { text: ratio(style.color, style.backgroundColor), boundary: ratio(style.borderColor, style.backgroundColor) };
    });
    expect(contrast.text, `${theme} target text`).toBeGreaterThanOrEqual(4.5);
    expect(contrast.boundary, `${theme} target boundary`).toBeGreaterThanOrEqual(3);
  }
});

test('sensor controls fit card and sidebar widths on desktop and mobile without hardware access', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route(url => url.pathname.startsWith('/api/') || !['localhost', '127.0.0.1'].includes(url.hostname),
    route => route.fulfill({ status: 503, json: { message: 'Isolated sensor fixture: hardware access blocked' } }));
  await page.goto('/src/test/fixtures/printer-sensors.html');
  const card = page.getByRole('region', { name: 'card', exact: true });
  const sidebar = page.getByRole('region', { name: 'sidebar', exact: true });
  await expect(card.getByText('Second Nozzle', { exact: true })).toBeVisible();
  await expect(page.getByText('Primary Nozzle', { exact: true })).toHaveCount(0);
  await expect(card.getByRole('spinbutton', { name: 'Set Part Cooling Speed' })).toBeEnabled();
  await expect(sidebar.getByRole('spinbutton', { name: 'Set Part Cooling Speed' })).toBeDisabled();
  await expect(card.getByRole('spinbutton', { name: 'Set Controller Fan Speed' })).toBeDisabled();
  await expect(card.getByRole('spinbutton', { name: 'X absolute target' })).toBeEnabled();
  await expect(card.getByRole('spinbutton', { name: 'Z absolute target' })).toBeEnabled();
  await expect(sidebar.getByRole('spinbutton', { name: 'X absolute target' })).toBeDisabled();
  await expect(page.getByRole('button', { name: /GO/ })).toHaveCount(0);
  for (const [name, width, height] of [['desktop', 1100, 1300], ['mobile', 360, 1000], ['narrow', 320, 1000]] as const) {
    await page.setViewportSize({ width, height });
    await card.getByRole('spinbutton', { name: 'Set Part Cooling Speed' }).fill('65');
    await expect(card.getByRole('spinbutton', { name: 'Set Heater Box1 Target' })).toBeEnabled();
    await expect(card.getByRole('button', { name: /^Apply Set/ })).toHaveCount(0);
    await expect(card.getByRole('region', { name: 'Miscellaneous', exact: true }).first()).toBeVisible();
    await expect(card.getByRole('slider', { name: 'Set Part Cooling Speed slider' })).toBeEnabled();
    await expect(sidebar.getByRole('slider', { name: 'Set Part Cooling Speed slider' })).toBeDisabled();
    await expect(card.getByRole('switch', { name: 'Set Case Light Power' })).toBeEnabled();
    await expect(card.getByTestId('sensor-filter')).not.toContainText('RPM');
    await expect(card.getByTestId('sensor-automatic')).toContainText('0 RPM');
    await expect(card.getByTestId('sensor-automatic').getByRole('slider')).toHaveCount(0);
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    const undersized = await card.locator('input[type="number"], input[type="range"], button[role="switch"], button[data-value-action]').evaluateAll(elements =>
      elements.filter(element => /^(Set |Hotend target|Bed target|Reset |Increase |Decrease )/.test(element.getAttribute('aria-label') ?? '')).filter(element => {
        const rect = (element.closest('[data-value-hit-area]') ?? element).getBoundingClientRect();
        return rect.height < 44 || rect.width < 44;
      }).map(element => element.getAttribute('aria-label')));
    expect(undersized).toEqual([]);
    const numericHeights = await card.locator('[data-value-hit-area] input[type="number"]').evaluateAll(elements =>
      elements.map(element => element.getBoundingClientRect().height));
    expect(numericHeights.every(height => height === 32)).toBe(true);
    const clippedCells = await card.locator('table td').evaluateAll(cells =>
      cells.filter(cell => cell.scrollWidth > cell.clientWidth + 1).map(cell => cell.textContent));
    expect(clippedCells).toEqual([]);
    const rowBorders = await card.locator('table tbody tr').evaluateAll(rows =>
      rows.map(row => getComputedStyle(row).borderBottomWidth));
    expect(rowBorders.every(border => border === '0px')).toBe(true);
    await page.screenshot({ path: test.info().outputPath(`sensors-${name}.png`), fullPage: true });
    await card.getByRole('spinbutton', { name: 'Set Part Cooling Speed' }).press('Escape');
    for (const [surface, scope] of [['card', card], ['sidebar', sidebar]] as const) {
      for (const section of ['Move', 'Temperatures', 'Miscellaneous']) {
        const region = section === 'Move' ? scope.locator('[data-printer-coordinate-row]')
          : scope.getByRole('region', { name: section, exact: true }).first();
        await region.scrollIntoViewIfNeeded();
        await region.screenshot({ path: test.info().outputPath(`${surface}-${section.toLowerCase().replaceAll(' ', '-')}-${name}.png`) });
      }
    }
    await card.getByRole('button', { name: 'Relative', exact: true }).click();
    await expect(card.getByRole('spinbutton', { name: 'Z movement amount' })).toBeEnabled();
    await card.locator('[data-printer-coordinate-row]').screenshot({ path: test.info().outputPath(`card-relative-${name}.png`) });
    await card.getByRole('button', { name: 'Absolute', exact: true }).click();
  }
  expect(errors).toEqual([]);
});

test('real sensor controls submit direct actions once and never send stale same-row drafts', async ({ page }) => {
  await page.route(url => url.pathname.startsWith('/api/') || !['localhost', '127.0.0.1'].includes(url.hostname),
    route => route.fulfill({ status: 503, json: { message: 'Isolated fixture: hardware access blocked' } }));
  await page.goto('/src/test/fixtures/printer-sensors.html');
  const card = page.getByRole('region', { name: 'card', exact: true });
  const commands = page.locator('[data-fixture-command]');
  const input = card.getByRole('spinbutton', { name: 'Set Part Cooling Speed' });
  await input.fill('65');
  await card.getByRole('button', { name: 'Reset Part Cooling Speed to 0%' }).click();
  await expect(commands).toHaveCount(1);
  await expect(commands.last()).toContainText('"value":0');
  await input.fill('65');
  await card.getByRole('button', { name: 'Increase Part Cooling Speed' }).click();
  await expect(commands).toHaveCount(2);
  await expect(commands.last()).toContainText('"value":66');
  await input.fill('72');
  await input.press('Enter');
  await input.press('Tab');
  await expect(commands).toHaveCount(3);
  await expect(commands.last()).toContainText('"value":72');
  await input.fill('63');
  await input.press('Tab');
  await expect(commands).toHaveCount(4);
  await expect(commands.last()).toContainText('"value":63');

  await input.fill('95');
  const slider = card.getByRole('slider', { name: 'Set Part Cooling Speed slider' });
  await slider.scrollIntoViewIfNeeded();
  const bounds = await slider.boundingBox();
  expect(bounds).not.toBeNull();
  await page.mouse.move(bounds!.x + bounds!.width * 0.3, bounds!.y + bounds!.height / 2);
  await page.mouse.down();
  await page.mouse.move(bounds!.x + bounds!.width * 0.6, bounds!.y + bounds!.height / 2);
  await expect(commands).toHaveCount(4);
  const finalValue = Number(await slider.inputValue());
  await page.mouse.up();
  await expect(commands).toHaveCount(5);
  await expect(commands.last()).toContainText(`"value":${finalValue}`);
  expect(finalValue).not.toBe(95);
  await slider.focus();
  await slider.press('ArrowRight');
  await expect(commands).toHaveCount(6);

  const target = card.getByRole('spinbutton', { name: 'Hotend target temperature' });
  await target.fill('220');
  await target.press('Enter');
  await target.press('Tab');
  await expect(commands).toHaveCount(7);
  await expect(commands.last()).toHaveText('{"printerId":"sensor-poc-card","hotend":220}');
  await expect(card.getByText('205.0°C', { exact: true })).toBeVisible();
  await card.getByRole('switch', { name: 'Set Case Light Power' }).click();
  await expect(commands).toHaveCount(8);
  await expect(commands.last()).toContainText('"sensorId":"light","quantity":"Power","value":1');
});
