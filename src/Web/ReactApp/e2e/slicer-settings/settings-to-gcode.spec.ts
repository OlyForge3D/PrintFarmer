import { test, expect, type APIRequestContext, type Locator, type Page } from '@playwright/test';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import metadata from '../../src/features/slicer/generated/orcaSettingsMetadata.json' with { type: 'json' };
import { provisionAdminAndLogin } from '../fixtures/emulator-setup';
import {
  assertChangedConfig, assertCommandEvidence, assertComplete, canonical, canonicalConfig, definition, heaterTargets, inventory, parseConfig,
  type CoverageRow, type ProfileKind, type SettingDefinition,
} from './gcode-evidence';
import { assertRunSource, type RunContext } from './run-context';

const version = '2.4.2';
const apiUrl = 'http://127.0.0.1:15245';
const printerName = 'Settings regression - NEVER PRINT';

function featureModel(): Buffer {
  const triangles: string[] = [];
  const faces = [[0, 2, 1], [0, 3, 2], [4, 5, 6], [4, 6, 7], [0, 1, 5], [0, 5, 4],
    [1, 2, 6], [1, 6, 5], [2, 3, 7], [2, 7, 6], [3, 0, 4], [3, 4, 7]];
  for (const [x, y, z, w, d, h] of [
    [0, 0, 0, 16, 12, 1], [0, 0, 1, 3, 4, 5], [13, 0, 1, 3, 4, 5],
    [0, 0, 6, 16, 4, 1], [6, 8, 1, 0.8, 3, 4],
  ]) {
    const vertices = [[x, y, z], [x + w, y, z], [x + w, y + d, z], [x, y + d, z],
      [x, y, z + h], [x + w, y, z + h], [x + w, y + d, z + h], [x, y + d, z + h]];
    for (const face of faces) triangles.push(`facet normal 0 0 0\nouter loop\n${face.map(i => `vertex ${vertices[i].join(' ')}`).join('\n')}\nendloop\nendfacet`);
  }
  return Buffer.from(`solid settings_features\n${triangles.join('\n')}\nendsolid settings_features\n`);
}

async function json<T>(response: Awaited<ReturnType<APIRequestContext['get']>>): Promise<T> {
  expect(response.ok(), `${response.url()} returned HTTP ${response.status()}: ${await response.text()}`).toBeTruthy();
  return response.json() as Promise<T>;
}

async function chooseProcess(page: Page): Promise<void> {
  const select = page.locator('select').filter({ has: page.locator('option[value^="system:"]') });
  await expect(select).toHaveCount(1);
  const options = await select.locator('option[value^="system:"]').evaluateAll(nodes =>
    nodes.map(node => ({ value: (node as HTMLOptionElement).value, text: node.textContent ?? '' })));
  const option = options.find(item => /0\.20?mm.*standard/i.test(item.text)) ?? options[0];
  if (!option) throw new Error('No compatible system process profile for the isolated printer');
  await select.selectOption(option.value);
}

async function openJob(page: Page, modelId: string): Promise<void> {
  await page.goto(`/slicer?modelId=${modelId}`);
  const sidebar = page.getByTestId('slicer-settings-sidebar');
  await expect(sidebar).toBeVisible({ timeout: 60_000 });
  const printer = sidebar.getByRole('button', { name: /Select.*printer/i });
  if (await printer.isVisible()) {
    await printer.click();
    await page.getByRole('dialog').getByText(printerName, { exact: true }).click();
  }
  await expect(sidebar.getByText(printerName, { exact: true })).toBeVisible();
  await sidebar.getByRole('button', { name: /^Machine profile:/ }).click();
  await page.getByRole('dialog').getByRole('region', { name: '0.4 mm machine profiles', exact: true })
    .getByRole('button', { name: 'Bambu Lab A1 mini 0.4mm', exact: true }).click();
  await selectFilament(page, 'Generic PLA @BBL A1M');
  await chooseProcess(page);
  if (await page.getByText('0 objects', { exact: true }).isVisible()) {
    await page.getByRole('button', { name: 'Add Model (Ctrl+O)', exact: true }).click();
    await page.getByRole('dialog').getByText('settings-features.stl', { exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Select', exact: true }).click();
  }
  await expect(page.getByRole('button', { name: 'Slice Plate 1 (1 model)', exact: true })).toBeEnabled({ timeout: 60_000 });
}

async function selectFilament(page: Page, name: string): Promise<void> {
  const field = page.getByRole('button', { name: 'Filament profile options menu', exact: true }).locator('../../..');
  await field.locator('button[aria-haspopup="dialog"]').click();
  const selector = page.getByRole('dialog', { name: 'Filament selector', exact: true });
  await selector.getByPlaceholder('Search filaments...').fill(name);
  await selector.getByRole('button', { name: name.startsWith('settings-regression-') ? `\u2605 ${name}` : name, exact: true }).click();
  await expect(selector).toBeHidden();
}

async function editControl(dialog: Locator, setting: SettingDefinition, baseline: string | undefined, kind: ProfileKind, baselineGcode: string): Promise<unknown> {
  const tabs = dialog.getByRole('tab');
  const profile = metadata[kind];
  const ownerTab = profile.tabs.find(tab => tab.sections.some(section => section.fields.some(field => field.key === setting.key)));
  const section = ownerTab?.sections.find(section => section.fields.some(field => field.key === setting.key));
  const field = section?.fields.find(field => field.key === setting.key);
  const index = section?.fields.findIndex(field => field.key === setting.key) ?? -1;
  let start = index;
  let end = index;
  while (start > 0 && section?.fields[start - 1].compound) start--;
  while (section?.fields[end + 1]?.compound) end++;
  const compound = field?.compound
    ? section!.fields[start].compound_label
      || section!.fields.slice(start, end + 1).map(field => definition(kind, field.key).label).join(' / ')
    : undefined;
  const names = [setting.label, ...(compound ? [`${compound} ${setting.label}`] : [])];
  const titles = [setting.label];
  if (/_temp(?:_initial_layer)?$/.test(setting.key)) {
    const plate = setting.key.replace(/_temp(?:_initial_layer)?$/, '').replaceAll('_', ' ')
      .replace(/\b\w/g, c => c.toUpperCase()).replace('Supertack', 'SuperTack')
      .replace('Eng', 'Engineering').replace('Hot', 'Smooth PEI / High Temp');
    titles.push(`${plate} ${setting.key.endsWith('_initial_layer') ? 'first layer' : 'other layers'}`);
  }
  let control: Locator | undefined;
  const tab = tabs.filter({ hasText: new RegExp(`^${(ownerTab?.name ?? 'Other Settings').replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`) });
  await expect(tab).toHaveCount(1);
  await tab.click();
  await expect(tab).toHaveAttribute('aria-selected', 'true');
  {
    if (setting.tooltip) {
      const label = dialog.locator('label[for]').and(dialog.getByTitle(setting.tooltip, { exact: true }));
      if (await label.count() === 1 && await label.isVisible()) {
        const id = await label.getAttribute('for');
        if (id) control = dialog.locator(`[id=${JSON.stringify(id)}]`);
      }
    }
    for (const name of names) {
      if (control) break;
      const candidates = dialog.getByLabel(name, { exact: true });
      if (await candidates.count() === 1 && await candidates.isVisible()) {
        control = candidates;
        break;
      }
    }
    for (const title of titles) {
      if (control) break;
      const titled = dialog.getByTitle(title, { exact: true });
      if (await titled.count() === 1 && await titled.isVisible()) {
        control = titled;
        break;
      }
    }
  }
  if (!control && section) {
    const container = dialog.getByRole('heading', { name: section.name, exact: true }).locator('../..');
    const candidate = container.getByLabel(setting.label, { exact: true });
    if (await candidate.count() === 1 && await candidate.isVisible()) control = candidate;
  }
  if (!control) throw new Error(`${setting.key}: no unique user-facing control (${setting.label}); needs a curated editor locator`);
  const tag = await control.evaluate(element => element.tagName.toLowerCase());
  const type = await control.getAttribute('type');
  if (type === 'checkbox') {
    const value = !(await control.isChecked());
    // The accessible checkbox is visually replaced by its label's SVG.
    // Space exercises the real keyboard handler without forced DOM clicks.
    await control.press('Space');
    await expect(control).toBeChecked({ checked: value });
    return value;
  }
  if (type === 'color') {
    const text = control.locator('..').getByPlaceholder('#000000', { exact: true });
    const sameBaseline = baseline !== undefined && canonical('#123456', setting) === canonicalConfig(baseline, setting);
    const value = sameBaseline || await text.inputValue() === '#123456' ? '#654321' : '#123456';
    await text.fill(value);
    await text.blur();
    await expect(text).toHaveValue(value);
    return value;
  }
  if (await control.getAttribute('role') === 'combobox') {
    const patterns = ['sparse_infill_pattern', 'top_surface_pattern', 'bottom_surface_pattern', 'internal_solid_infill_pattern'];
    if (!patterns.includes(setting.key)) throw new Error(`${setting.key}: needs a curated combobox value/label mapping`);
    const next = baseline === 'concentric' ? { value: 'rectilinear', label: 'Rectilinear' } : { value: 'concentric', label: 'Concentric' };
    await control.click();
    await dialog.getByRole('option', { name: next.label, exact: true }).click();
    await expect(control).toContainText(next.label);
    return next.value;
  }
  if (tag === 'select') {
    const options = await control.locator('option:not(:disabled)').evaluateAll(nodes =>
      nodes.map(node => (node as HTMLOptionElement).value));
    const current = await control.inputValue();
    const next = options.find(value => value !== current && value !== ''
      && (baseline === undefined || canonical(value, setting) !== canonicalConfig(baseline, setting)));
    if (next === undefined) throw new Error(`${setting.key}: no distinct valid enum alternative is offered by the editor`);
    await control.selectOption(next);
    return next;
  }
  if (type === 'number') {
    const current = Number(await control.inputValue());
    const step = setting.type === 'int' ? 1 : 0.01;
    // Orca caps bridge width at the nozzle diameter even though registry.max
    // also accommodates percent values; this number-only UI emits millimetres.
    let candidates = setting.key === 'bridge_line_width'
      ? [0.35, 0.36]
      : [current + step, current - step, current + 2 * step, setting.min ?? step, (setting.min ?? step) + step];
    if (['nozzle_temperature', 'nozzle_temperature_initial_layer'].includes(setting.key)) {
      const before = heaterTargets(baselineGcode);
      candidates = Array.from({ length: 20 }, (_, i) => current + i + 1).filter(value => !before.includes(value));
    }
    const next = candidates
      .map(value => Number(value.toFixed(5)))
      .find(value => Number.isFinite(value)
        && value >= (setting.min ?? 0) && value <= (setting.max ?? Infinity)
        && (baseline === undefined || canonical(String(value), setting) !== canonicalConfig(baseline, setting)));
    if (next === undefined) throw new Error(`${setting.key}: no bounded, distinct numeric alternative`);
    await control.fill(String(next));
    await control.blur();
    await expect(control).toHaveValue(String(next));
    return next;
  }
  if (type === 'color') {
    const next = (await control.inputValue()).toLowerCase() === '#247ac1' ? '#c17424' : '#247ac1';
    await control.fill(next);
    return next;
  }
  if (tag === 'textarea' && setting.key.endsWith('_gcode')) {
    const next = `${await control.inputValue()}\n; settings-regression-${setting.key}`;
    await control.fill(next);
    return next;
  }
  const textValues: Record<string, [string, string]> = {
    extra_solid_infills: ['5#1', '7#1'],
    filename_format: ['settings-proof-{input_filename_base}.gcode', 'settings-proof-alt-{input_filename_base}.gcode'],
    notes: ['Settings regression notes', 'Settings regression alternate notes'],
    filament_notes: ['Settings regression filament notes', 'Settings regression alternate filament notes'],
    filament_vendor: ['Settings Regression', 'Settings Regression Alternate'],
    solid_infill_rotate_template: ['0,90', '0,45'],
    sparse_infill_rotate_template: ['0,90', '0,45'],
    compatible_printers_condition: ['nozzle_diameter[0] == 0.4', 'nozzle_diameter[0] > 0'],
    compatible_prints_condition: ['layer_height < 0.3', 'layer_height < 0.4'],
    small_area_infill_flow_compensation_model: ['0,0;0.5,0.6;10,1;', '0,0;0.5,0.7;10,1;'],
  };
  if ((tag === 'textarea' || tag === 'input') && textValues[setting.key]) {
    const [first, second] = textValues[setting.key];
    const sameBaseline = baseline !== undefined && canonical(first, setting) === canonicalConfig(baseline, setting);
    const next = sameBaseline || await control.inputValue() === first ? second : first;
    await control.fill(next);
    await control.blur();
    return next;
  }
  if (setting.key === 'post_process') {
    const command = join(import.meta.dirname, 'post-process-proof.mjs');
    await control.fill(command);
    await control.blur();
    return command;
  }
  throw new Error(`${setting.key}: ${tag}/${type} requires a curated valid alternative, not arbitrary text`);
}

async function applySetting(page: Page, row: CoverageRow, baseline: string | undefined, api: APIRequestContext, baselineGcode: string, phase = 'changed'): Promise<unknown> {
  const process = row.profile === 'process';
  await page.getByRole('button', { name: process ? 'Process profile options menu' : 'Filament profile options menu', exact: true }).click();
  await page.getByRole('button', { name: process ? 'Edit in modal' : 'Edit settings', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('tablist')).toBeVisible();
  const toggle = dialog.getByRole('button', { name: /Switch to (Advanced|Simple) mode/ });
  await expect(toggle).toBeVisible();
  await expect(toggle).toBeEnabled();
  const advanced = dialog.getByRole('button', { name: 'Switch to Advanced mode', exact: true });
  if (await advanced.isVisible()) await advanced.click();
  await expect(dialog.getByRole('button', { name: 'Switch to Simple mode', exact: true })).toBeVisible();
  const requested = await editControl(dialog, definition(row.profile, row.key), baseline, row.profile, baselineGcode);
  if (process) {
    await dialog.getByRole('button', { name: 'Apply', exact: true }).click();
  } else {
    await dialog.getByRole('button', { name: 'Save as Custom Profile', exact: true }).click();
    const name = `settings-regression-${row.key}-${phase}`;
    await dialog.getByRole('textbox', { name: 'Custom Profile Name' }).fill(name);
    const saved = page.waitForResponse(response => response.url().endsWith('/api/slicer/profiles/upload') && response.request().method() === 'POST');
    await dialog.getByRole('button', { name: 'Save', exact: true }).click();
    const response = await saved;
    expect(response.ok(), `${row.key}: filament save failed`).toBeTruthy();
    const submitted = response.request().postDataJSON() as { rawJson: string };
    const raw: Record<string, unknown> = JSON.parse(submitted.rawJson);
    expect(canonical(raw[row.key], definition(row.profile, row.key)), `${row.key}: editor lost the value before save`)
      .toBe(canonical(requested, definition(row.profile, row.key)));
    const profile = await response.json() as { id: string };
    // The export route is process-only. Read the persisted filament through
    // the same owner-scoped collection used by custom-profile management.
    const savedProfiles = await json<{ profiles: Array<{ id: string; rawJson: string }> }>(await api.get('/api/slicer/profiles/custom'));
    const persisted = savedProfiles.profiles.find(item => item.id === profile.id);
    expect(persisted, `${row.key}: uploaded filament is missing from owned profiles`).toBeDefined();
    const stored: Record<string, unknown> = JSON.parse(persisted!.rawJson);
    expect(canonical(stored[row.key], definition(row.profile, row.key)), `${row.key}: saved profile lost the value`)
      .toBe(canonical(requested, definition(row.profile, row.key)));
    // Saving does not select a custom filament; use the actual picker.
    await selectFilament(page, name);
  }
  await expect(dialog).toBeHidden();
  return requested;
}

async function slice(page: Page, api: APIRequestContext, root: string, label: string, row?: CoverageRow): Promise<Map<string, string>> {
  await expect(page.getByRole('button', { name: /^Slice Plate \d+ \(\d+ models?\)$/ })).toBeEnabled();
  const submitted = page.waitForResponse(response => /\/api\/slice\/?$/.test(response.url()) && response.request().method() === 'POST');
  await page.getByRole('button', { name: /^Slice Plate \d+ \(\d+ models?\)$/ }).click();
  const response = await submitted;
  expect(response.ok(), `${label}: submission failed: ${await response.text()}`).toBeTruthy();
  const request = response.request().postDataJSON() as { slicerProfileJson: string };
  const job = await response.json() as { jobId: string };
  if (row) row.jobId = job.jobId;
  writeFileSync(join(root, `${label}-submitted-profile.json`), request.slicerProfileJson);
  if (row?.profile === 'process') {
    const profile: { overrides?: Record<string, unknown> } = JSON.parse(request.slicerProfileJson);
    expect(profile.overrides, `${row.key}: submission lost process overrides`).toHaveProperty(row.key);
    expect(canonical(profile.overrides?.[row.key], definition(row.profile, row.key)), `${row.key}: submission altered the requested value`)
      .toBe(canonical(row.requested, definition(row.profile, row.key)));
  }
  let status: { status: string; errorDetail?: string } = { status: 'Queued' };
  await expect(async () => {
    status = await json(await api.get(`/api/slice/${job.jobId}`));
    expect(['Completed', 'Failed', 'Cancelled']).toContain(status.status);
  }).toPass({ timeout: 180_000, intervals: [500, 1000, 2000] });
  expect(status.status, `${label}: ${status.errorDetail ?? 'inspect isolated worker logs'}`).toBe('Completed');
  const artifacts = await json<Array<{ id: string; fileName: string }>>(await api.get(`/api/artifacts/job/${job.jobId}`));
  const artifact = artifacts.find(item => item.fileName.endsWith('.gcode'));
  if (!artifact) throw new Error(`${label}: completed job has no G-code artifact`);
  const download = await api.get(`/api/artifacts/${artifact.id}`);
  expect(download.ok()).toBeTruthy();
  const gcode = await download.text();
  writeFileSync(join(root, `${label}.gcode`), gcode);
  if (row) row.gcodeGenerated = true;
  expect(gcode, `${label}: config comments alone are not a printable toolpath`).toMatch(/^G[123]\s+.*[XY].*E/m);
  return parseConfig(gcode, version);
}

test('every registry setting has explicit editor-to-new-G-code evidence', async ({ page, playwright }, testInfo) => {
  const root = process.env.PF_SETTINGS_RUN_ROOT;
  if (!root) throw new Error('Use playwright.slicer-settings.config.ts; shared/external stacks are not permitted');
  const context: RunContext = JSON.parse(readFileSync(join(root, 'run.json'), 'utf8'));
  assertRunSource(context);
  const isolation: unknown = JSON.parse(readFileSync(join(root, 'isolation.json'), 'utf8'));
  const enginePath = join(process.env.ORCA_TEST_ROOT!, process.platform === 'win32' ? 'orca-slicer.exe' : 'bin/orca-slicer');
  const engineSha256 = createHash('sha256').update(readFileSync(enginePath)).digest('hex');
  mkdirSync(root, { recursive: true });
  const rows = inventory();
  const diagnosticKeys = process.env.PF_SETTINGS_KEYS?.split(',').map(key => key.trim()).filter(Boolean);
  if ((diagnosticKeys && diagnosticKeys.length === 0)
    || diagnosticKeys?.some(key => !rows.some(row => `${row.profile}.${row.key}` === key))) {
    throw new Error('PF_SETTINGS_KEYS contains a key outside the current inventory');
  }
  const provenance = { runId: context.runId, source: context.source, engineSha256, isolation, diagnosticKeys };
  const first = ['layer_height', 'initial_layer_print_height', 'wall_loops', 'sparse_infill_density',
    'nozzle_temperature', 'nozzle_temperature_initial_layer'];
  rows.sort((a, b) => {
    const rank = (key: string) => first.includes(key) ? first.indexOf(key) : first.length;
    return rank(a.key) - rank(b.key);
  });
  for (const row of rows) {
    const setting = definition(row.profile, row.key);
    if (['adaptive_pressure_advance_overhangs', 'adaptive_pressure_advance_bridges', 'adaptive_pressure_advance_model'].includes(row.key)) {
      row.status = 'excluded';
      row.reason = 'MetadataSection unconditionally filters this exact key via CONDITIONAL_HIDDEN_KEYS; it has no rendered editor control. Not G-code verified.';
      continue;
    }
    if (row.profile === 'process' && ['compatible_printers', 'compatible_printers_condition'].includes(row.key)) {
      row.status = 'excluded';
      row.reason = 'ProcessOverridePolicy intentionally rejects compatibility-filter overrides; bypassing that policy to manufacture G-code evidence is not permitted.';
      continue;
    }
    const tabbed = metadata[row.profile].tabs.some(tab => tab.sections.some(section => section.fields.some(field => field.key === row.key)));
    if (setting.mode === 'developer' || (!tabbed && !setting.mode)) {
      row.status = 'excluded';
      row.reason = setting.mode === 'developer'
        ? 'MetadataProfileRenderer explicitly hides this developer-mode key; no user editor path exists.'
        : 'Internal orphan metadata: no tab field and no user-visible mode; MetadataProfileRenderer does not render this key.';
    }
  }
  let negativeControl = 'not run';
  let engineVerified = false;
  let sourceVerified = false;
  let blocker: string | undefined;
  let api: APIRequestContext | undefined;
  try {
    // Block accidental remote requests and every printer/queue dispatch route.
    await page.route('**/*', route => {
      const url = new URL(route.request().url());
      const dispatch = route.request().method() !== 'GET'
        && /send-to-printer|add-to-queue|\/printjob|\/job-queue/.test(url.pathname);
      const allowed = ['127.0.0.1', 'localhost'].includes(url.hostname) && !dispatch;
      return allowed || ['blob:', 'data:'].includes(url.protocol) ? route.continue() : route.abort('blockedbyclient');
    });
    const token = await provisionAdminAndLogin(page.request);
    if (!token) throw new Error('Isolated test administrator login failed');
    api = await playwright.request.newContext({ baseURL: apiUrl, extraHTTPHeaders: { Authorization: `Bearer ${token}` } });
    const manufacturer = await json<{ id: string }>(await api.post('/api/catalog/manufacturers', {
      data: { name: 'Settings regression fixtures' },
    }));
    const model = await json<{ id: string; manufacturerId: string }>(await api.post('/api/catalog/printer-models', {
      data: {
        name: 'Settings regression A1 mini', manufacturerId: manufacturer.id,
        maxX: 180, maxY: 180, maxZ: 180, hasHeatedBed: true,
        defaultBackend: 'Moonraker', supportedFilamentTypes: ['PLA'],
      },
    }));
    await json(await api.put(`/api/catalog/printer-models/${model.id}/aliases`, {
      data: { orcaSlicerNames: ['Bambu Lab A1 mini'], prusaSlicerNames: [] },
    }));
    await expect(async () => {
      const machines = await json<unknown[]>(await api!.get(`/api/slicer/profiles/machine/for-model/${model.id}?slicerEngineVersion=${version}`));
      expect(machines.length, 'Worker must be registered with live compatible profiles').toBeGreaterThan(0);
    }).toPass({ timeout: 150_000, intervals: [1000, 3000, 5000] });
    const connection = await json<{ success: boolean; message?: string }>(await api.post('/api/printers/test-connection', { data: {
      backend: 'Moonraker', serverUrl: 'http://127.0.0.1:17125', backendPort: 17125,
    } }));
    expect(connection.success, connection.message).toBeTruthy();
    const printer = await json<{ id: string }>(await api.post('/api/printers', { data: {
      name: printerName, backend: 'Moonraker', serverUrl: 'http://127.0.0.1:17125', ipAddress: '127.0.0.1', backendPort: 17125, frontendPort: 17125,
      manufacturerId: model.manufacturerId, modelId: model.id, nozzleDiameter: 0.4,
      isEnabled: true, isReachable: true, maxBuildVolumeX: 180, maxBuildVolumeY: 180, maxBuildVolumeZ: 180,
    } }));
    await expect(async () => {
      const current = await json<{ isOnline: boolean }>(await api!.get(`/api/printers/${printer.id}`));
      expect(current.isOnline, 'The isolated Moonraker fixture must actually be online').toBe(true);
      const listed = await json<Array<{ id: string; isOnline: boolean; state?: string }>>(await api!.get('/api/printers', {
        headers: { 'Cache-Control': 'no-cache' },
      }));
      expect(listed.find(item => item.id === printer.id)?.isOnline, 'The slicer printer listing must report the emulator online').toBe(true);
    }).toPass({ timeout: 90_000, intervals: [1000, 3000] });
    const uploaded = await json<{ id: string }>(await api.post('/api/3d-models/upload?virtualPath=%2F', {
      multipart: { modelFile: { name: 'settings-features.stl', mimeType: 'application/octet-stream', buffer: featureModel() } },
    }));
    expect(uploaded.id).toMatch(/^[a-f\d-]{36}$/i);
    await page.goto('/');
    await page.evaluate(value => {
      localStorage.setItem('auth-token', value);
      localStorage.setItem('pf-tour-seen-dashboard', 'true');
      localStorage.setItem('pf-tour-seen-printers', 'true');
    }, token);
    await openJob(page, uploaded.id);
    const baseline = await slice(page, api, root, 'baseline');
    const baselineGcode = readFileSync(join(root, 'baseline.gcode'), 'utf8');
    engineVerified = true;

    for (const row of rows) {
      const setting = definition(row.profile, row.key);
      if (row.status === 'excluded') continue;
      if (diagnosticKeys && !diagnosticKeys.includes(`${row.profile}.${row.key}`)) {
        row.reason = 'Not executed: diagnostic PF_SETTINGS_KEYS filter. This row remains unresolved and the suite must fail.';
        continue;
      }
      assertRunSource(context);
      row.attempted = true;
      row.baseline = baseline.get(row.key);
      try {
        let effectiveBaseline = baseline;
        if (!baseline.has(row.key)) {
          // Nullable/conditional options may be absent until explicitly set.
          // Establish a real per-key baseline through the same save/select path,
          // then require a second distinct value and a second fresh artifact.
          await openJob(page, uploaded.id);
          const first = await applySetting(page, row, undefined, api, baselineGcode, 'baseline');
          row.baselineRequested = first;
          const setupRow: CoverageRow = { ...row, requested: first };
          const label = `${row.profile}-${row.key}-baseline`;
          try {
            effectiveBaseline = await slice(page, api, root, label, setupRow);
          } finally {
            row.baselineJobId = setupRow.jobId;
            if (setupRow.gcodeGenerated) row.baselineArtifact = `${label}.gcode`;
          }
          const value = effectiveBaseline.get(row.key);
          if (value === undefined) {
            throw new Error(`${row.key}: neither the shared baseline nor an explicitly saved per-key baseline emits this key. Needs an audited engine/feature disposition; NOT verified.`);
          }
          expect(canonicalConfig(value, setting), `${row.key}: per-key baseline did not retain its UI value`).toBe(canonical(first, setting));
          row.baseline = value;
        }
        await openJob(page, uploaded.id);
        row.requested = await applySetting(page, row, row.baseline, api, baselineGcode);
        const actual = await slice(page, api, root, `${row.profile}-${row.key}`, row);
        row.emitted = actual.get(row.key);
        assertChangedConfig(setting, row.requested, effectiveBaseline, actual);
        row.behaviorVerified = assertCommandEvidence(row.key, row.requested,
          readFileSync(join(root, `${row.profile}-${row.key}.gcode`), 'utf8'), baselineGcode);
        if (negativeControl === 'not run') {
          const corrupted = new Map(actual);
          corrupted.set(row.key, row.baseline!);
          expect(() => assertChangedConfig(setting, row.requested, effectiveBaseline, corrupted)).toThrow();
          negativeControl = `${row.profile}.${row.key}: replacing the newly generated value with baseline was rejected`;
        }
        row.status = 'config-verified';
        row.reason = 'Changed through the editor and Apply/save-select path; new job CONFIG_BLOCK matches distinct request. Configuration retention only, not physical behavior proof.';
        if (row.key === 'post_process') {
          // Tagged Orca 2.4.2 OrcaSlicer.cpp comments out CLI execution of
          // run_post_process_scripts. Retention must not imply host execution.
          row.reason += ' Orca 2.4.2 CLI disables post-processing execution; behavior is NOT verified.';
        }
      } catch (error) {
        row.reason = error instanceof Error ? error.message : String(error);
      }
      writeFileSync(join(root, 'coverage.json'), JSON.stringify({ ...provenance, engine: version, engineVerified, sourceVerified, negativeControl, rows }, null, 2));
      process.stdout.write(`${row.profile}.${row.key}: ${row.status}${row.status === 'unresolved' ? ` (${row.reason.split('\n')[0]})` : ''}\n`);
    }
    assertRunSource(context);
    sourceVerified = true;
    assertComplete(rows);
    expect(negativeControl, 'At least one live changed value must exercise the negative control').not.toBe('not run');
    for (const key of ['layer_height', 'initial_layer_print_height', 'nozzle_temperature', 'nozzle_temperature_initial_layer']) {
      expect(rows.find(row => row.key === key)?.behaviorVerified, `${key}: required independent command evidence is missing`).toBe(true);
    }
  } catch (error) {
    blocker = error instanceof Error ? error.message : String(error);
    throw error;
  } finally {
    const summary = {
      registry: rows.length,
      eligible: rows.filter(row => row.status !== 'excluded').length,
      attempted: rows.filter(row => row.attempted).length,
      submitted: rows.filter(row => row.jobId).length,
      executed: rows.filter(row => row.gcodeGenerated).length,
      configVerified: rows.filter(row => row.status === 'config-verified').length,
      behaviorVerified: rows.filter(row => row.behaviorVerified).length,
      excluded: rows.filter(row => row.status === 'excluded').length,
      unresolved: rows.filter(row => row.status === 'unresolved').length,
    };
    const report = join(root, 'coverage.json');
    writeFileSync(report, JSON.stringify({ ...provenance, engineRequested: version, engineVerified, sourceVerified, summary, negativeControl, blocker, rows }, null, 2));
    await testInfo.attach('per-setting coverage (incomplete unless unresolved=0)', { path: report, contentType: 'application/json' });
    process.stdout.write(`Settings coverage: ${JSON.stringify(summary)}; report: ${report}\n`);
    await api?.dispose();
  }
});
