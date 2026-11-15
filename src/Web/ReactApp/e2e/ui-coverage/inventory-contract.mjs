import { readdir, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

export const REACT_ROUTE_INVENTORY = [
  { id: 'web.login', paths: ['/login'] },
  { id: 'web.forgot-password', paths: ['/forgot-password'] },
  { id: 'web.reset-password', paths: ['/reset-password'] },
  { id: 'web.confirm-email', paths: ['/confirm-email'] },
  { id: 'web.printables-oauth-callback', paths: ['/oauth/printables/callback'] },
  { id: 'web.registration-pending', paths: ['/registration-pending'] },
  { id: 'web.dashboard', paths: ['/', '/dashboard'] },
  { id: 'web.printers', paths: ['/printers', '/printers/:printerId'] },
  { id: 'web.printer-maintenance', paths: ['/printers/:printerId/maintenance'] },
  { id: 'web.cameras', paths: ['/cameras'] },
  { id: 'web.printer-groups', paths: ['/printer-groups'] },
  { id: 'web.print-queue', paths: ['/printQueue', '/printQueue/:tabId'] },
  { id: 'web.files', paths: ['/files/*'] },
  { id: 'web.projects', paths: ['/projects'] },
  { id: 'web.spools', paths: ['/spools', '/spools/:tabId'] },
  { id: 'web.nfc-bindings', paths: ['/nfc-bindings'] },
  { id: 'web.maintenance', paths: ['/maintenance'] },
  { id: 'web.parts-inventory', paths: ['/parts-inventory', '/parts-inventory/:tabId'] },
  { id: 'web.auto-dispatch', paths: ['/auto-dispatch'] },
  { id: 'web.analytics', paths: ['/analytics'] },
  { id: 'web.scheduling', paths: ['/scheduling'] },
  { id: 'web.locations', paths: ['/locations'] },
  { id: 'web.catalog', paths: ['/catalog'] },
  { id: 'web.settings', paths: ['/settings'] },
  { id: 'web.api-keys', paths: ['/profile/api-keys'] },
  { id: 'web.notification-preferences', paths: ['/profile/notifications'] },
  { id: 'web.passkeys', paths: ['/profile/passkeys'] },
  { id: 'web.admin-control-center', paths: ['/admin'] },
  { id: 'web.admin-settings', paths: ['/admin/settings'] },
  { id: 'web.system-status', paths: ['/admin/status'] },
  { id: 'web.worker-management', paths: ['/admin/workers'] },
  { id: 'web.login-audit', paths: ['/admin/login-audit'] },
  { id: 'web.data-management', paths: ['/admin/data-management'] },
  { id: 'web.power-monitors', paths: ['/admin/power-monitors'] },
  { id: 'web.slicer', paths: ['/slicer'] },
  { id: 'web.profile-import', paths: ['/profiles/import'] },
  { id: 'web.not-found', paths: ['*'] },
];

export const STATIC_PAGE_INVENTORY = [
  { id: 'site.landing', path: 'index.html' },
  { id: 'site.privacy', path: 'privacy/index.html' },
  { id: 'site.image-notices', path: 'image-notices.html' },
];

// These surfaces have no single source registry. Keep this explicit curated authority separate from catalog.json.
export const CURATED_INVENTORY_IDS = {
  'root-gate': ['web.startup-setup'],
  'embedded-panel': [
    'web.embedded.statistics-production', 'web.embedded.statistics-cost', 'web.embedded.statistics-fleet',
    'web.embedded.filaments', 'web.embedded.spools', 'web.embedded.material-clusters', 'web.embedded.parts',
    'web.embedded.bins', 'web.embedded.mappings', 'web.embedded.reorder', 'web.embedded.catalog-printer-models',
    'web.embedded.catalog-filaments', 'web.embedded.catalog-toolheads', 'web.embedded.catalog-extruders',
    'web.embedded.catalog-hotends', 'web.embedded.catalog-nozzles',
  ],
  'overlay-family': [
    'web.overlays.shared', 'web.overlays.global', 'web.overlays.notices-tours', 'web.overlays.auth',
    'web.overlays.user-role-tag', 'web.overlays.admin', 'web.overlays.catalog', 'web.overlays.locations',
    'web.overlays.filament', 'web.overlays.files', 'web.overlays.gcode-models', 'web.overlays.projects',
    'web.overlays.maintenance', 'web.overlays.parts-inventory', 'web.overlays.harvest', 'web.overlays.nfc',
    'web.overlays.printers', 'web.overlays.queue-schedule-tasks', 'web.overlays.slicer',
    'web.overlays.unproven-import', 'ios.native-alerts', 'ios.native-inline-panels',
  ],
  'shared-axis': [
    'web.shared-shell-states', 'web.theme-contrast-axis', 'web.viewport-input-axis', 'ios.navigation-tabs',
    'ios.appearance-axis', 'ios.viewport-accessibility-axis',
  ],
  'root-state': [
    'ios.launch-splash', 'ios.first-server', 'ios.local-network-permission', 'ios.backend-gate',
    'ios.snapshot-activation',
  ],
  screen: [
    'ios.onboarding', 'ios.login', 'ios.farm-dashboard', 'ios.printer-detail', 'ios.queue-job-list',
    'ios.job-detail', 'ios.filament-spool-inventory', 'ios.parts-inventory', 'ios.selected-part',
    'ios.notifications', 'ios.account', 'ios.settings', 'ios.servers', 'ios.offline-queue',
    'ios.scan-flow', 'ios.qr-scanner', 'ios.barcode-intake', 'ios.nfc-write', 'ios.bin-scan-result',
  ],
  sheet: [
    'ios.certificate-trust', 'ios.job-history', 'ios.add-spool', 'ios.part-lookup', 'ios.server-editor',
    'ios.server-switcher', 'ios.spool-picker', 'ios.harvest-sheet',
  ],
};

function getJsxTagName(node) {
  if (ts.isJsxElement(node)) return node.openingElement.tagName.getText();
  if (ts.isJsxSelfClosingElement(node)) return node.tagName.getText();
  return undefined;
}

function getJsxAttribute(node, name) {
  const attributes = ts.isJsxElement(node) ? node.openingElement.attributes : node.attributes;
  return attributes.properties.find(attribute =>
    ts.isJsxAttribute(attribute) && attribute.name.getText() === name,
  );
}

function getStringAttribute(node, name) {
  const attribute = getJsxAttribute(node, name);
  return attribute && ts.isStringLiteral(attribute.initializer)
    ? attribute.initializer.text
    : undefined;
}

function normalizeRoutePath(path) {
  if (!path) return '/';
  return path.startsWith('/') || path === '*' ? path : `/${path}`;
}

export function extractReactRoutePaths(appSource) {
  const sourceFile = ts.createSourceFile('App.tsx', appSource, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  const routesElement = [];

  function findRoutes(node) {
    if (getJsxTagName(node) === 'Routes') routesElement.push(node);
    ts.forEachChild(node, findRoutes);
  }
  findRoutes(sourceFile);

  const paths = [];
  function visitRoute(route, parentPath) {
    const rawPath = getStringAttribute(route, 'path');
    const isIndex = Boolean(getJsxAttribute(route, 'index'));
    const routePath = isIndex
      ? parentPath
      : rawPath?.startsWith('/')
        ? rawPath.slice(1)
        : [parentPath, rawPath].filter(Boolean).join('/');
    const childBase = rawPath === '/*' ? parentPath : routePath;
    const children = ts.isJsxElement(route)
      ? route.children.filter(child => getJsxTagName(child) === 'Route')
      : [];

    if (children.length > 0) {
      for (const child of children) visitRoute(child, childBase);
    } else if (rawPath !== undefined || isIndex) {
      paths.push(normalizeRoutePath(routePath));
    }
  }

  for (const routes of routesElement) {
    if (ts.isJsxElement(routes)) {
      for (const child of routes.children.filter(node => getJsxTagName(node) === 'Route')) {
        visitRoute(child, '');
      }
    }
  }

  return paths;
}

function getObjectProperty(object, name) {
  if (!ts.isObjectLiteralExpression(object)) return undefined;
  return object.properties.find(property =>
    ts.isPropertyAssignment(property) && property.name.getText() === name,
  )?.initializer;
}

function getObjectString(object, name) {
  const value = getObjectProperty(object, name);
  return value && ts.isStringLiteral(value) ? value.text : undefined;
}

export function extractSettingsLeafIds(settingsSource) {
  const sourceFile = ts.createSourceFile('types.ts', settingsSource, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
  let categories;

  function findCategories(node) {
    if (
      ts.isVariableDeclaration(node) &&
      node.name.getText() === 'SETTINGS_CATEGORIES' &&
      node.initializer &&
      ts.isArrayLiteralExpression(node.initializer)
    ) {
      categories = node.initializer;
    }
    ts.forEachChild(node, findCategories);
  }
  findCategories(sourceFile);
  if (!categories) return [];

  const ids = [];
  for (const category of categories.elements) {
    const categoryId = getObjectString(category, 'id');
    const subPages = getObjectProperty(category, 'subPages');
    if (!categoryId || !subPages || !ts.isArrayLiteralExpression(subPages)) continue;
    const subPageIds = subPages.elements.map(subPage => getObjectString(subPage, 'id')).filter(Boolean);
    if (subPageIds.length === 0) ids.push(`web.settings.${categoryId}`);
    else for (const subPageId of subPageIds) ids.push(`web.settings.${categoryId}-${subPageId}`);
  }
  return ids;
}

async function collectHtmlFiles(directory, prefix = '') {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const relativePath = prefix ? `${prefix}/${entry.name}` : entry.name;
    const absolutePath = `${directory}/${entry.name}`;
    if (entry.isDirectory()) files.push(...await collectHtmlFiles(absolutePath, relativePath));
    else if (entry.isFile() && entry.name.endsWith('.html')) files.push(relativePath);
  }
  return files.sort();
}

export async function loadInventorySources() {
  const appPath = new URL('../../src/App.tsx', import.meta.url);
  const settingsPath = new URL('../../src/features/settings/types.ts', import.meta.url);
  const siteRoot = fileURLToPath(new URL('../../../../../site/', import.meta.url));
  const [app, settings, siteHtmlFiles] = await Promise.all([
    readFile(appPath, 'utf8'),
    readFile(settingsPath, 'utf8'),
    collectHtmlFiles(siteRoot),
  ]);
  return { app, settings, siteHtmlFiles };
}

function compareIdSets(errors, label, expectedIds, actualIds) {
  const expected = new Set(expectedIds);
  const actual = new Set(actualIds);
  for (const id of expected) if (!actual.has(id)) errors.push(`${label} is missing ${id}`);
  for (const id of actual) if (!expected.has(id)) errors.push(`${label} has unexpected ${id}`);
}

export function validateInventoryCoverage(catalog, sources) {
  const errors = [];
  const routeOwners = new Map();
  for (const route of REACT_ROUTE_INVENTORY) {
    for (const path of route.paths) {
      if (routeOwners.has(path)) errors.push(`route contract duplicates ${path}`);
      routeOwners.set(path, route.id);
    }
  }

  const actualRoutePaths = extractReactRoutePaths(sources.app);
  compareIdSets(errors, 'React source routes', routeOwners.keys(), actualRoutePaths);
  compareIdSets(
    errors,
    'React route catalog entries',
    REACT_ROUTE_INVENTORY.map(route => route.id),
    catalog.entries.filter(entry => entry.kind === 'route').map(entry => entry.id),
  );
  const routeCatalogEntries = new Map(
    catalog.entries.filter(entry => entry.kind === 'route').map(entry => [entry.id, entry]),
  );
  for (const route of REACT_ROUTE_INVENTORY) {
    const entry = routeCatalogEntries.get(route.id);
    if (!entry) continue;
    const expectedInventoryRow = route.paths.includes('*')
      ? 'Unmatched route 404'
      : route.paths.join(' and ');
    if (entry.inventoryRow !== expectedInventoryRow) {
      errors.push(`React route catalog entry ${route.id} has unexpected inventory row ${entry.inventoryRow}`);
    }
  }
  compareIdSets(
    errors,
    'settings catalog entries',
    extractSettingsLeafIds(sources.settings),
    catalog.entries.filter(entry => entry.kind === 'settings-leaf').map(entry => entry.id),
  );

  for (const [kind, expectedIds] of Object.entries(CURATED_INVENTORY_IDS)) {
    compareIdSets(
      errors,
      `${kind} catalog entries`,
      expectedIds,
      catalog.entries.filter(entry => entry.kind === kind).map(entry => entry.id),
    );
  }

  const expectedSiteFiles = new Map(STATIC_PAGE_INVENTORY.map(page => [page.path, page.id]));
  compareIdSets(errors, 'static HTML source pages', expectedSiteFiles.keys(), sources.siteHtmlFiles);
  compareIdSets(
    errors,
    'static-page catalog entries',
    STATIC_PAGE_INVENTORY.map(page => page.id),
    catalog.entries.filter(entry => entry.kind === 'static-page').map(entry => entry.id),
  );
  for (const file of sources.siteHtmlFiles) {
    if (!expectedSiteFiles.has(file)) errors.push(`static HTML source page is not inventoried: ${file}`);
  }

  return errors;
}
