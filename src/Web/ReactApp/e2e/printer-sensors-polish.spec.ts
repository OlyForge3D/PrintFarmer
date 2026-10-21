import { expect, test } from '@playwright/test';
import { SELECTABLE_THEMES } from '../src/design-system/themes/registry';

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
    await expect(card.getByRole('region', { name: 'Fans & outputs', exact: true }).first()).toBeVisible();
    await expect(card.getByRole('slider', { name: 'Set Part Cooling Speed slider' })).toBeEnabled();
    await expect(sidebar.getByRole('slider', { name: 'Set Part Cooling Speed slider' })).toBeDisabled();
    await expect(card.getByRole('switch', { name: 'Set Case Light Power' })).toBeEnabled();
    await expect(card.getByTestId('sensor-filter')).not.toContainText('RPM');
    await expect(card.getByTestId('sensor-automatic')).toContainText('0 RPM');
    await expect(card.getByTestId('sensor-automatic').getByRole('slider')).toHaveCount(0);
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    const undersized = await card.locator('input[type="number"], input[type="range"], button[role="switch"], button[data-value-action]').evaluateAll(elements =>
      elements.filter(element => /^(Set |Hotend target|Bed target|Reset |Increase |Decrease )/.test(element.getAttribute('aria-label') ?? '')).filter(element => {
        const rect = element.getBoundingClientRect();
        return rect.height < 44 || rect.width < 44;
      }).map(element => element.getAttribute('aria-label')));
    expect(undersized).toEqual([]);
    const clippedCells = await card.locator('table td').evaluateAll(cells =>
      cells.filter(cell => cell.scrollWidth > cell.clientWidth + 1).map(cell => cell.textContent));
    expect(clippedCells).toEqual([]);
    const rowBorders = await card.locator('table tbody tr').evaluateAll(rows =>
      rows.map(row => getComputedStyle(row).borderBottomWidth));
    expect(rowBorders.every(border => border === '0px')).toBe(true);
    await page.screenshot({ path: test.info().outputPath(`sensors-${name}.png`), fullPage: true });
    await card.getByRole('spinbutton', { name: 'Set Part Cooling Speed' }).press('Escape');
    for (const [surface, scope] of [['card', card], ['sidebar', sidebar]] as const) {
      for (const section of ['Move', 'Temperatures', 'Fans & outputs']) {
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
