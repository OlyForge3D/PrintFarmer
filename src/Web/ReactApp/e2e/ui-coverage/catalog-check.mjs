import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { validateCatalog } from './policy.mjs';
import { loadInventorySources, validateInventoryCoverage } from './inventory-contract.mjs';

const catalog = JSON.parse(await readFile(new URL('./catalog.json', import.meta.url), 'utf8'));
const inventorySources = await loadInventorySources();

test('every inventory row has a stable execution or ownership disposition', () => {
  assert.deepEqual(validateCatalog(catalog), []);
  const iosEntries = catalog.entries.filter(entry => entry.platform === 'ios');
  assert.equal(iosEntries.length, 37);
  assert.ok(iosEntries.every(entry =>
    entry.disposition === 'unverified' &&
    entry.reason.startsWith('Owned by separate iOS track; excluded from web coverage totals and gates.'),
  ));
  assert.ok(catalog.entries.filter(entry => entry.disposition === 'candidate').every(entry =>
    entry.platform === 'web' || entry.platform === 'site',
  ));
  assert.ok(catalog.entries.some(entry => entry.platform === 'web' && entry.disposition === 'unverified'));
  assert.ok(catalog.entries.some(entry => entry.evidenceFlags.includes('I')));
  assert.equal(new Set(catalog.entries.map(entry => entry.id)).size, catalog.entries.length);
});

test('catalog inventory exactly covers source routes, settings, static pages, and curated surfaces', () => {
  assert.deepEqual(validateInventoryCoverage(catalog, inventorySources), []);
});

test('inventory contract rejects a removed catalog route and an unexpected well-formed row', () => {
  const missingRoute = structuredClone(catalog);
  missingRoute.entries = missingRoute.entries.filter(entry => entry.id !== 'web.login');
  assert.ok(validateInventoryCoverage(missingRoute, inventorySources).some(error =>
    error === 'React route catalog entries is missing web.login',
  ));

  const unexpectedEntry = structuredClone(catalog);
  unexpectedEntry.entries.push({
    ...unexpectedEntry.entries.find(entry => entry.kind === 'route'),
    id: 'web.unexpected-route',
    inventoryRow: '/unexpected-route',
  });
  assert.ok(validateInventoryCoverage(unexpectedEntry, inventorySources).some(error =>
    error === 'React route catalog entries has unexpected web.unexpected-route',
  ));
});

test('inventory contract rejects a source route added without a catalog authority entry', () => {
  const changedSources = {
    ...inventorySources,
    app: inventorySources.app.replace(
      '<Route path="dashboard" element={lazyRoute(<LazyPrinterDashboard />)} />',
      '<Route path="dashboard" element={lazyRoute(<LazyPrinterDashboard />)} />\n        <Route path="new-page" element={<NotFoundPage />} />',
    ),
  };
  assert.ok(validateInventoryCoverage(catalog, changedSources).some(error =>
    error === 'React source routes has unexpected /new-page',
  ));
});

test('inventory contract rejects a route catalog row mapped to the wrong source path', () => {
  const mismatchedRoute = structuredClone(catalog);
  const route = mismatchedRoute.entries.find(entry => entry.id === 'web.dashboard');
  route.inventoryRow = '/printers';
  assert.ok(validateInventoryCoverage(mismatchedRoute, inventorySources).some(error =>
    error === 'React route catalog entry web.dashboard has unexpected inventory row /printers',
  ));
});

test('catalog mutations that silently drop coverage rows are rejected', () => {
  const mutated = structuredClone(catalog);
  mutated.entries[0] = { ...mutated.entries[0], disposition: 'candidate', scenarioIds: undefined, reason: '' };
  assert.ok(validateCatalog(mutated).some(error => error.includes('candidate must name an executable scenario')));
  assert.ok(validateCatalog(mutated).some(error => error.includes('reason must explain')));
});
