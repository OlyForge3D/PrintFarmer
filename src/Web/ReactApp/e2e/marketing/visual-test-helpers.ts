import AxeBuilder from '@axe-core/playwright';
import { expect, type Browser, type BrowserContext, type Page, type TestInfo } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import type { VisualScene } from './visual-scenes';
import { captureOrigin, captureTime, isolateCapture } from './fixtures';

const siteOrigin = 'http://127.0.0.1:4179';
const catalog = JSON.parse(await readFile(new URL('../ui-coverage/catalog.json', import.meta.url), 'utf8'));
const lockFile = JSON.parse(await readFile(new URL('../../package-lock.json', import.meta.url), 'utf8'));
const expectedFamilies = [
  ['IBM Plex Sans', 400],
  ['Space Grotesk', 500],
  ['JetBrains Mono', 400],
  ['Inter', 400],
  ['Nunito Sans', 400],
  ['DM Mono', 400],
  ['Rajdhani', 500],
  ['Chakra Petch', 400],
  ['Merriweather Sans', 400],
] as const;

async function loadPinnedFonts(page: Page) {
  await page.addScriptTag({ url: '/e2e/marketing/ui-coverage-fonts.ts', type: 'module' });
  await page.evaluate(() => document.fonts.ready);
}

export async function assertRenderingEnvironment(browser: Browser, page: Page, scene: VisualScene) {
  expect(process.platform, 'The reviewed renderer is pinned to Windows').toBe(catalog.renderProfile.os);
  expect(browser.browserType().name()).toBe(catalog.renderProfile.browser);
  expect(browser.version()).toBe(catalog.renderProfile.chromiumVersion);
  expect(lockFile.packages['node_modules/@playwright/test']?.version).toBe(catalog.renderProfile.playwright);
  expect(lockFile.packages['node_modules/@axe-core/playwright']?.version).toBe(catalog.renderProfile.accessibilityPackage['@axe-core/playwright']);
  for (const [packageName, version] of Object.entries(catalog.renderProfile.fontPackages)) {
    expect(lockFile.packages[`node_modules/${packageName}`]?.version, packageName).toBe(version);
  }
  const actual = await page.evaluate(() => ({
    deviceScaleFactor: devicePixelRatio,
    locale: navigator.language,
    timezoneId: Intl.DateTimeFormat().resolvedOptions().timeZone,
    viewport: { width: innerWidth, height: innerHeight },
    reducedMotion: matchMedia('(prefers-reduced-motion: reduce)').matches,
    colorScheme: matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light',
    prefersContrast: matchMedia('(prefers-contrast: more)').matches,
    printMedia: matchMedia('print').matches,
    coarsePointer: matchMedia('(pointer: coarse)').matches,
    theme: document.documentElement.dataset.theme,
    contrast: document.documentElement.dataset.contrast,
  }));
  expect(actual).toEqual({
    deviceScaleFactor: catalog.renderProfile.deviceScaleFactor,
    locale: catalog.renderProfile.locale,
    timezoneId: catalog.renderProfile.timezoneId,
    viewport: scene.viewport,
    reducedMotion: true,
    colorScheme: scene.colorScheme,
    prefersContrast: scene.systemContrast === 'more',
    printMedia: scene.media === 'print',
    coarsePointer: scene.coarsePointer ?? false,
    theme: scene.surface === 'app'
      ? scene.theme === 'system' ? scene.colorScheme : scene.theme
      : undefined,
    contrast: scene.surface === 'app' ? scene.contrast ?? (scene.systemContrast === 'more' ? 'high' : 'normal') : undefined,
  });
}

export async function prepareVisualScene(context: BrowserContext, page: Page, scene: VisualScene) {
  const pageErrors: string[] = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  await page.setViewportSize(scene.viewport);
  await page.emulateMedia({
    colorScheme: scene.colorScheme,
    reducedMotion: 'reduce',
    contrast: scene.systemContrast === 'more' ? 'more' : 'no-preference',
    media: scene.media ?? 'screen',
  });

  if (scene.surface === 'app') {
    const violations = await isolateCapture(context, {
      theme: scene.theme,
      catalogFilaments: scene.catalogFilaments,
      dashboard: scene.dashboard,
      adminWorkers: scene.adminWorkers,
      profileImport: scene.profileImport,
      inactiveUser: scene.inactiveUser,
      printQueue: scene.printQueue,
      printerMaintenance: scene.printerMaintenance,
      printerGroupsPopulated: scene.printerGroupsPopulated,
      filesLibrary: scene.filesLibrary,
      filesLibraryPopulated: scene.filesLibraryPopulated,
      setupWizard: scene.setupWizard,
      slicerWorkers: scene.slicerWorkers,
      notificationDrawer: scene.overlay === 'notification-drawer',
      contrastPreference: scene.contrast,
      nfcPairing: scene.nfcPairing,
      slicerCapabilityState: scene.slicerCapabilityState,
      nfcDeviceDetail: scene.nfcDeviceDetail,
      filesLibraryGcode: scene.filesLibraryGcode,
      authenticated: scene.authenticated !== false,
      permissionFallback: scene.permissionFallback,
      routeChunkFailure: scene.routeChunkFailure,
      routeChunkLoading: scene.routeChunkLoading,
      flow: {
        setupCompletion: scene.setupCompletion,
        overlayMutation: scene.overlayMutation,
      },
    });
    await context.route(
      url => url.origin === 'https://fonts.googleapis.com' && url.pathname === '/css2',
      route => route.fulfill({ contentType: 'text/css', body: '/* Offline: pinned local fonts are loaded for visual coverage. */' }),
    );
    const releasePendingRequest = violations.releasePendingRequest;
    const networkViolations = violations.violations;
    await page.clock.setFixedTime(new Date(captureTime));
    const historyResponse = scene.overlay === 'harvest-job'
      ? page.waitForResponse(response => new URL(response.url()).pathname === '/api/job-queue-analytics/history')
      : undefined;
    await page.goto(scene.path);
    await loadPinnedFonts(page);
    if (historyResponse) {
      const response = await historyResponse;
      expect(response.status(), 'Synthetic queue-history response must succeed').toBe(200);
    }
    if (scene.setupCompletion) {
      await expect(page.getByRole('heading', { name: 'Welcome to PrintFarmer', exact: true })).toBeVisible();
      await page.locator('#firstName').fill('Synthetic');
      await page.locator('#lastName').fill('Operator');
      await page.locator('#username').fill('coverage-fixture');
      await page.locator('#email').fill('coverage-fixture@example.invalid');
      await page.locator('#password').fill('synthetic-fixture-password-123!');
      await page.locator('#confirmPassword').fill('synthetic-fixture-password-123!');
      await page.getByRole('button', { name: 'Create Admin & Continue', exact: true }).click();
      await expect(page.getByRole('heading', { name: /Network Discovery/ })).toBeVisible();
      await page.getByRole('button', { name: 'Next', exact: true }).click();
      await expect(page.getByRole('heading', { name: /Spoolman Integration/ })).toBeVisible();
      await page.getByRole('button', { name: 'Next', exact: true }).click();
      await expect(page.getByRole('heading', { name: /Summary/ })).toBeVisible();

      const settingsSave = page.waitForResponse(response =>
        response.request().method() === 'POST' &&
        new URL(response.url()).pathname === '/api/settings/NetworkDiscovery',
      );
      const reload = page.waitForNavigation();
      await page.getByRole('button', { name: 'Finish Setup', exact: true }).click();
      const saveResponse = await settingsSave;
      expect(saveResponse.status(), 'Only the synthetic setup settings write is accepted').toBe(200);
      expect(saveResponse.request().postDataJSON()).toEqual({
        rowVersion: 'synthetic-setup-version',
        enableDiscovery: false,
        discoverySubnets: [],
        ports: [],
        clientTimeoutMs: 5000,
        requestDelayMs: 100,
        maxConcurrentRequests: 5,
        maxRetries: 3,
      });
      await reload;
      await expect(page).toHaveURL(new URL('/', captureOrigin).toString());
      await expect(page.getByRole('heading', { name: 'Printer Dashboard', exact: true })).toBeVisible();
      await loadPinnedFonts(page);
    } else if (scene.heading && scene.overlay !== 'nfc-pairing' && scene.overlay !== 'nfc-link-error') {
      await expect(page.getByRole('heading', { name: scene.heading, exact: true })).toBeVisible();
    }
    if (scene.overlay === 'auth-redirect') {
      await expect(page).toHaveURL(/\/login$/);
    }
    if (scene.overlay === 'permission-fallback') {
      await expect(page.getByRole('heading', { name: 'Access Denied', exact: true })).toBeVisible();
      await expect(page.getByText("You don't have permission to perform this action.", { exact: true })).toBeVisible();
    }
    if (scene.overlay === 'route-chunk-error') {
      const boundary = page.getByTestId('route-error-boundary');
      await expect(boundary).toBeVisible();
      await expect(boundary.getByRole('heading', { name: 'Failed to load this page', exact: true })).toBeVisible();
      await expect(boundary.getByRole('button', { name: 'Reload page', exact: true })).toBeVisible();
    }
    if (scene.overlay === 'capability-loading') {
      await expect(page.getByRole('status', { name: 'Loading platform capabilities' })).toBeVisible();
    }
    if (scene.overlay === 'lazy-route-loading') {
      await expect(page.getByRole('status', { name: 'Loading' })).toBeVisible();
    }
    if (scene.overlay === 'notification-drawer') {
      await page.getByRole('button', { name: /Notifications/ }).click();
      await expect(page.getByRole('heading', { name: 'Notifications', exact: true })).toBeVisible();
      await expect(page.getByText('Synthetic printer needs attention', { exact: true })).toBeVisible();
    }
    if (scene.overlay === 'dashboard-tour' || scene.overlay === 'printers-tour' || scene.overlay === 'queue-tour' || scene.overlay === 'settings-tour') {
      const targetSelector = {
        'dashboard-tour': '[data-tour="stats-cards"]',
        'printers-tour': '[data-tour="printers-actions"]',
        'queue-tour': '[data-tour="queue-stats"]',
        'settings-tour': '[data-tour="settings-content"]',
      }[scene.overlay];
      const firstStepTitle = {
        'dashboard-tour': 'Farm Status at a Glance',
        'printers-tour': 'Add & Discover Printers',
        'queue-tour': 'Queue Overview',
        'settings-tour': 'Configuration Options',
      }[scene.overlay];
      const target = page.locator(targetSelector);
      await expect(target).toBeVisible();
      await page.getByRole('button', { name: 'Take a tour of this page' }).click();
      const popover = page.locator('.driver-popover');
      await expect(popover).toBeVisible();
      await expect(popover.getByText(firstStepTitle, { exact: true })).toBeVisible();
    }
    if (scene.overlay === 'command-palette') {
      if (scene.keyboardOnly) {
        await page.keyboard.press('Tab');
        await expect(page.locator(':focus')).toHaveCount(1);
      }
      await page.keyboard.press('Control+k');
      await expect(page.getByRole('dialog', { name: 'Command palette' })).toBeVisible();
      const search = page.getByRole('combobox', { name: 'Search settings command palette' });
      await expect(search).toBeFocused();
      if (scene.paletteSearch) {
        if (scene.keyboardOnly) await page.keyboard.type(scene.paletteSearch);
        else await search.fill(scene.paletteSearch);
        await expect(page.getByRole('listbox', { name: 'Command palette results' })).toBeVisible();
        const appearanceResult = page.getByRole('option', { name: /Appearance/ }).first();
        await expect(appearanceResult).toBeVisible();
        if (scene.paletteActivate) {
          const target = page.getByRole('option', { name: /Profile.*Appearance/ }).first();
          const targetId = await target.getAttribute('id');
          expect(targetId, 'Appearance settings destination is present in results').toBeTruthy();
          const optionCount = await page.getByRole('option').count();
          for (let index = 0; index < optionCount; index += 1) {
            if (await search.getAttribute('aria-activedescendant') === targetId) break;
            await search.press('ArrowDown');
          }
          expect(await search.getAttribute('aria-activedescendant')).toBe(targetId);
          await expect(target).toHaveAttribute('aria-selected', 'true');
          await search.press('Enter');
          await expect(page).toHaveURL(url =>
            url.pathname === '/settings' &&
            url.searchParams.get('scope') === 'user' &&
            url.searchParams.get('tab') === 'profile' &&
            url.searchParams.get('sub') === 'appearance',
          );
        }
      }
      if (scene.keyboardTraversal) {
        const skipLink = page.getByRole('link', { name: 'Skip to main content' });
        await page.keyboard.press('Tab');
        await expect(skipLink).toBeFocused();
        await page.keyboard.press('Enter');
        await expect(page.locator('#main-content')).toBeFocused();
        for (let index = 0; index < 8; index += 1) await page.keyboard.press('Tab');
        const focusedElement = page.locator(':focus');
        await expect(focusedElement).toBeVisible();
        expect(await focusedElement.evaluate(element => element.closest('main') !== null)).toBe(true);
      }
    }
    if (scene.overlay === 'edit-printer-group' || scene.overlay === 'delete-printer-group') {
      const group = page.getByRole('article', { name: 'Synthetic fixture group printer group' });
      if (scene.overlay === 'edit-printer-group') {
        await group.getByRole('button', { name: 'Edit group' }).click();
        const dialog = page.getByRole('dialog', { name: 'Edit Printer Group' });
        await expect(dialog.getByLabel('Name')).toHaveValue('Synthetic fixture group');
        await expect(dialog.getByLabel('Description')).toHaveValue('Fixture-only printer group.');
      } else {
        await group.getByRole('button', { name: 'Delete group' }).click();
        const dialog = page.getByRole('dialog', { name: 'Delete Printer Group' });
        await expect(dialog.getByText('Are you sure you want to delete "Synthetic fixture group"? This will not delete the printers, only unassign them from this group.', { exact: true })).toBeVisible();
        await expect(dialog.getByRole('button', { name: 'Delete', exact: true })).toBeVisible();
      }
    }
    if (scene.overlay === 'mobile-navigation') {
      await page.getByRole('button', { name: 'Open navigation menu' }).click();
      const drawer = page.getByRole('dialog', { name: 'Mobile navigation drawer' });
      await expect(drawer).toBeVisible();
      await expect(drawer.getByRole('navigation', { name: 'Main navigation' })).toBeVisible();
      await expect(drawer.getByRole('link', { name: 'Overview' })).toBeVisible();
    }
    if (scene.overlay === 'admin-pin-chooser') {
      await page.getByRole('button', { name: 'Pin admin links' }).click();
      await expect(page.getByRole('region', { name: 'Pin admin links' })).toBeVisible();
      await expect(page.getByText('Choose authorized admin destinations', { exact: false })).toBeVisible();
    }
    if (scene.overlay === 'new-bed-type') {
      await page.locator('main').getByRole('button', { name: 'Add Bed Type' }).first().click();
      await expect(page.getByRole('dialog', { name: 'New Bed Type' })).toBeVisible();
    }
    if (scene.overlay === 'new-role') {
      await page.locator('main').getByRole('button', { name: 'New role' }).first().click();
      const dialog = page.getByRole('dialog', { name: 'New role' });
      await expect(dialog).toBeVisible();
      if (scene.overlayMutation === 'role-create-error') {
        await dialog.getByRole('textbox', { name: 'Name', exact: true }).fill('coverage_role');
        await dialog.getByLabel('Display name').fill('Coverage role');
        const responsePromise = page.waitForResponse(response =>
          response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/admin/roles',
        );
        await dialog.getByRole('button', { name: 'Create role', exact: true }).click();
        const response = await responsePromise;
        expect(response.status()).toBe(503);
        expect(response.request().postDataJSON()).toEqual({ name: 'coverage_role', displayName: 'Coverage role' });
        await expect(dialog.getByRole('alert')).toHaveText('Synthetic role creation failure.');
        await expect(dialog.getByRole('textbox', { name: 'Name', exact: true })).toHaveValue('coverage_role');
      }
    }
    if (scene.overlay === 'new-tag') {
      await page.getByTestId('add-tag-action').click();
      const dialog = page.getByRole('dialog', { name: 'Create New Tag' });
      await expect(dialog).toBeVisible();
      if (scene.overlayMutation === 'tag-create-error') {
        await dialog.getByLabel('Tag Name').fill('Synthetic coverage tag');
        const responsePromise = page.waitForResponse(response =>
          response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/tags',
        );
        await dialog.getByRole('button', { name: 'Create tag', exact: true }).click();
        const response = await responsePromise;
        expect(response.status()).toBe(503);
        expect(response.request().postDataJSON()).toEqual({
          name: 'Synthetic coverage tag',
          color: '#d74242',
          description: '',
        });
        await expect(dialog.getByText("Couldn't create tag", { exact: true })).toBeVisible();
        await expect(dialog.getByLabel('Tag Name')).toHaveValue('Synthetic coverage tag');
      }
    }
    if (scene.overlay === 'new-custom-field') {
      await page.locator('main').getByRole('button', { name: 'Add Field' }).first().click();
      await expect(page.getByRole('dialog', { name: 'New Custom Field' })).toBeVisible();
    }
    if (scene.overlay === 'new-user') {
      await page.getByRole('button', { name: 'Add User', exact: true }).click();
      await expect(page.getByRole('dialog', { name: 'Create New User' })).toBeVisible();
    }
    if (scene.overlay === 'new-printer-group') {
      await page.getByTestId('create-printer-group-action').click();
      const dialog = page.getByRole('dialog', { name: 'Create Printer Group' });
      await expect(dialog).toBeVisible();
      if (scene.overlayMutation === 'printer-group-create-success' || scene.overlayMutation === 'printer-group-create-error') {
        await dialog.getByLabel('Name').fill('Synthetic coverage group');
        await dialog.getByLabel('Description').fill('Fixture-only printer group mutation.');
        const mutationResponse = page.waitForResponse(response =>
          response.request().method() === 'POST' &&
          new URL(response.url()).pathname === '/api/printer-groups',
        );
        await dialog.getByRole('button', { name: 'Create Group', exact: true }).click();
        const response = await mutationResponse;
        const isError = scene.overlayMutation === 'printer-group-create-error';
        expect(response.status()).toBe(isError ? 503 : 201);
        expect(response.request().postDataJSON()).toEqual({
          name: 'Synthetic coverage group',
          description: 'Fixture-only printer group mutation.',
        });
        if (isError) {
          await expect(page.getByText(/^Failed to create group:/)).toBeVisible();
          await expect(dialog).toBeVisible();
          await expect(dialog.getByLabel('Name')).toHaveValue('Synthetic coverage group');
        } else {
          await expect(page.getByText('Group "Synthetic coverage group" created', { exact: true })).toBeVisible();
          await expect(dialog).toBeHidden();
        }
      }
    }
    if (scene.overlay === 'new-location') {
      await page.getByRole('button', { name: 'Add location', exact: true }).click();
      await expect(page.getByRole('heading', { name: 'Create New Location', exact: true })).toBeVisible();
      if (scene.overlayMutation === 'location-create-success' || scene.overlayMutation === 'location-create-error') {
        const form = page.locator('form').filter({ has: page.getByLabel('Location Name') });
        await page.getByLabel('Location Name').fill('Synthetic coverage location');
        await page.getByLabel('Description').fill('Fixture-only location mutation.');
        const mutationResponse = page.waitForResponse(response =>
          response.request().method() === 'POST' &&
          new URL(response.url()).pathname === '/api/locations',
        );
        await form.getByRole('button', { name: 'Create', exact: true }).click();
        const response = await mutationResponse;
        const isError = scene.overlayMutation === 'location-create-error';
        expect(response.status()).toBe(isError ? 503 : 201);
        expect(response.request().postDataJSON()).toEqual({
          name: 'Synthetic coverage location',
          description: 'Fixture-only location mutation.',
          parentId: null,
        });
        if (isError) {
          await expect(page.getByRole('alert')).toBeVisible();
          await expect(page.getByRole('heading', { name: 'Create New Location', exact: true })).toBeVisible();
          await expect(page.getByLabel('Location Name')).toHaveValue('Synthetic coverage location');
        } else {
          await expect(page.getByRole('heading', { name: 'Create New Location', exact: true })).toBeHidden();
          await expect(page.locator('tbody').getByText('Synthetic coverage location', { exact: true })).toBeVisible();
        }
      }
    }
      if (scene.overlay === 'edit-location') {
        await page.getByRole('button', { name: 'Manage', exact: true }).click();
        const locationRow = page.getByRole('table').first().getByRole('row').filter({ hasText: 'Workshop' });
        await locationRow.getByRole('button', { name: 'Edit', exact: true }).click();
        await expect(page.getByRole('heading', { name: 'Edit Location', exact: true })).toBeVisible();
        await expect(page.getByLabel('Location Name')).toHaveValue('Workshop');
        await expect(page.getByLabel('Description')).toHaveValue('');
      }
      if (scene.overlay === 'delete-location') {
        await page.getByRole('button', { name: 'Manage', exact: true }).click();
        const locationRow = page.getByRole('table').first().getByRole('row').filter({ hasText: 'Workshop' });
        await locationRow.getByRole('button', { name: 'Delete', exact: true }).click();
        const dialog = page.getByRole('dialog', { name: 'Delete Location?' });
        await expect(dialog.getByText('Delete this location? Locations with children cannot be deleted.', { exact: true })).toBeVisible();
        await expect(dialog.getByRole('button', { name: 'Delete', exact: true })).toBeVisible();
      }
    if (scene.overlay === 'new-part') {
      await page.getByRole('button', { name: 'Add SKU', exact: true }).click();
      await expect(page.getByRole('dialog', { name: 'Add printed part SKU' })).toBeVisible();
    }
    if (scene.overlay === 'delete-file') {
      await page.getByRole('button', { name: 'Delete file', exact: true }).click();
      const dialog = page.getByRole('dialog', { name: 'Delete File' });
      await expect(dialog).toBeVisible();
      await expect(dialog.getByText('Are you sure you want to delete "Synthetic calibration bracket.stl"?', { exact: true })).toBeVisible();
    }
    if (scene.overlay === 'file-delete-partial') {
      await page.getByRole('checkbox', { name: 'Select Synthetic calibration bracket.stl', exact: true }).check();
      await page.getByRole('checkbox', { name: 'Select Synthetic active-job guard.stl', exact: true }).check();
      await page.getByRole('button', { name: 'Delete (2)', exact: true }).click();
      const dialog = page.getByRole('dialog', { name: 'Delete Selected Files' });
      await expect(dialog).toBeVisible();
      await dialog.getByRole('button', { name: 'Delete', exact: true }).click();
      await expect(page.getByText('Deleted 1 file, 1 in use by active jobs', { exact: true })).toBeVisible();
      await expect(page.getByRole('checkbox', { name: 'Select Synthetic active-job guard.stl', exact: true })).toBeChecked();
      await expect(page.getByRole('checkbox', { name: 'Select Synthetic calibration bracket.stl', exact: true })).toHaveCount(0);
    }
    if (scene.overlay === 'file-details') {
      await page.getByRole('button', { name: 'Preview of Synthetic calibration bracket.stl, show details', exact: true }).click();
      const panel = page.getByRole('complementary', { name: 'Synthetic calibration bracket.stl' });
      await expect(panel).toBeVisible();
      await expect(panel.getByText('Local library', { exact: true })).toBeVisible();
    }
    if (scene.overlay === 'queue-gcode') {
      await page.getByRole('button', { name: 'Queue for printing', exact: true }).click();
      const dialog = page.getByRole('dialog', { name: 'Queue G-code for Printing' });
      await expect(dialog).toBeVisible();
      await expect(dialog.getByText('Synthetic calibration bracket.gcode', { exact: true })).toBeVisible();
      await expect(dialog.getByRole('button', { name: 'Queue for Later', exact: true })).toBeVisible();
      await expect(dialog.getByRole('button', { name: 'Queue & Start Now', exact: true })).toBeVisible();
    }
    if (scene.overlay === 'harvest-job') {
      await page.getByRole('button', { name: 'Harvest Cable guide into printed-part stock', exact: true }).click();
      const dialog = page.getByRole('dialog', { name: 'Harvest — Cable guide' });
      await expect(dialog).toBeVisible();
      await expect(dialog.getByRole('button', { name: 'Confirm harvest', exact: true })).toBeVisible();
      if (scene.overlayMutation === 'harvest-success' || scene.overlayMutation === 'harvest-error') {
        const responsePromise = page.waitForResponse(response =>
          response.request().method() === 'POST' &&
          new URL(response.url()).pathname === '/api/job-queue/dashboard-history-1/harvest',
        );
        await dialog.getByRole('button', { name: 'Confirm harvest', exact: true }).click();
        const response = await responsePromise;
        const isError = scene.overlayMutation === 'harvest-error';
        expect(response.status()).toBe(isError ? 503 : 200);
        const submitted = response.request().postDataJSON();
        expect(Object.keys(submitted)).toEqual(['operationKey']);
        expect(typeof submitted.operationKey).toBe('string');
        expect(submitted.operationKey.length).toBeGreaterThan(0);
        if (isError) {
          const errorDialog = page.getByRole('dialog', { name: 'Harvest failed — Cable guide' });
          await expect(errorDialog.getByText('Synthetic harvest service failure.', { exact: true })).toBeVisible();
          await expect(errorDialog.getByRole('button', { name: 'Retry', exact: true })).toBeVisible();
        } else {
          const successDialog = page.getByRole('dialog', { name: 'Harvest complete — Cable guide' });
          await expect(successDialog.getByText('Harvest complete. Printed-part stock updated.', { exact: true })).toBeVisible();
          await expect(successDialog.getByRole('listitem').filter({ hasText: /BRACKET-001 → bin BIN-01/ })).toHaveCount(1);
          await expect(successDialog.getByRole('listitem').filter({ hasText: /BRACKET-001: \+2 \(new balance 7\).*BIN-01/ })).toHaveCount(1);
        }
      }
    }
    if (scene.overlay === 'schedule-job') {
      await page.getByRole('button', { name: 'Schedule Sensor mount on Birch Queued', exact: true }).click();
      const dialog = page.getByRole('dialog', { name: 'Schedule Print Job' });
      await expect(dialog).toBeVisible();
      await expect(dialog.getByLabel('Print Job')).toHaveValue('dashboard-job-2');
      await expect(dialog.getByLabel('Recurrence')).toHaveValue('once');
      await expect(dialog.getByRole('button', { name: 'Schedule Job', exact: true })).toBeDisabled();
    }
    if (scene.overlay === 'new-task') {
      await page.getByRole('button', { name: 'New', exact: true }).click();
      const dialog = page.getByRole('dialog', { name: 'Create Task' });
      await expect(dialog).toBeVisible();
      await expect(dialog.getByLabel('Title')).toBeVisible();
      await expect(dialog.getByLabel('Priority')).toHaveValue('Normal');
      if (scene.overlayMutation) {
        await dialog.getByLabel('Title').fill('Synthetic coverage task');
        await dialog.getByLabel('Description').fill('Fixture-only task mutation.');
        await dialog.getByLabel('Priority').selectOption('High');
        const mutationResponse = page.waitForResponse(response =>
          response.request().method() === 'POST' &&
          new URL(response.url()).pathname === '/api/tasks',
        );
        await dialog.getByRole('button', { name: 'Create', exact: true }).click();
        const response = await mutationResponse;
        const isError = scene.overlayMutation === 'task-create-error';
        expect(response.status()).toBe(isError ? 503 : 201);
        expect(response.request().postDataJSON()).toEqual({
          title: 'Synthetic coverage task',
          description: 'Fixture-only task mutation.',
          priority: 'High',
        });
        if (isError) {
          await expect(page.getByText('Failed to create task', { exact: true })).toBeVisible();
          await expect(dialog).toBeVisible();
          await expect(dialog.getByLabel('Title')).toHaveValue('Synthetic coverage task');
        } else {
          await expect(page.getByText('Task created', { exact: true })).toBeVisible();
          await expect(dialog).toBeHidden();
        }
      }
    }
    if (scene.overlay === 'slicer-import-profile') {
      await page.getByRole('button', { name: 'Import Profile', exact: true }).click();
      const dialog = page.getByRole('dialog', { name: 'Import Profile' });
      await expect(dialog).toBeVisible();
      await expect(dialog.getByLabel('Raw Profile JSON')).toBeVisible();
    }
    if (scene.overlay === 'new-spool') {
      const tab = page.getByRole('tab', { name: 'Spools', exact: true });
      await tab.click();
      await expect(tab).toHaveAttribute('aria-selected', 'true');
      await page.getByRole('button', { name: 'Add a new spool', exact: true }).click();
      await expect(page.getByRole('dialog', { name: 'Add Spool' })).toBeVisible();
    }
    if (scene.overlay === 'new-maintenance-task') {
      for (const tabId of ['library', 'tasks']) {
        const tab = page.locator(`#tab-${tabId}`);
        await tab.click();
        await expect(tab).toHaveAttribute('aria-selected', 'true');
      }
      await page.getByRole('button', { name: 'New Task', exact: true }).click();
      await expect(page.getByRole('dialog', { name: 'New Catalog Task' })).toBeVisible();
    }
    if (scene.overlay === 'register-account') {
      await page.getByRole('button', { name: 'Register', exact: true }).click();
      await expect(page.getByRole('heading', { name: 'Create Account', exact: true })).toBeVisible();
    }
    if (scene.overlay === 'new-project' || scene.overlay === 'project-create-error') {
      await page.getByRole('button', { name: 'New Project' }).click();
      await expect(page.getByRole('dialog', { name: 'Create New Project' })).toBeVisible();
    }
    if (scene.overlay === 'project-create-error') {
      const dialog = page.getByRole('dialog', { name: 'Create New Project' });
      await dialog.getByPlaceholder('e.g., Voron 2.4 Build Kit').fill('Synthetic coverage project');
      const responsePromise = page.waitForResponse(response =>
        response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/projects',
      );
      await dialog.getByRole('button', { name: 'Create Project', exact: true }).click();
      const response = await responsePromise;
      expect(response.status(), 'Only the synthetic project failure response is accepted').toBe(503);
      await expect(dialog.getByText('Failed to create project: Synthetic project creation failure.', { exact: true })).toBeVisible();
      await expect(dialog.getByPlaceholder('e.g., Voron 2.4 Build Kit')).toHaveValue('Synthetic coverage project');
    }
    if (scene.overlay === 'file-picker') {
      await page.getByRole('button', { name: 'New Project' }).click();
      await expect(page.getByRole('dialog', { name: 'Create New Project' })).toBeVisible();
      await page.getByRole('button', { name: 'Add Files' }).click();
      const picker = page.getByRole('dialog', { name: 'Add Files to Project' });
      await expect(picker).toBeVisible();
      await expect(picker.getByText('No G-code files available', { exact: true })).toBeVisible();
    }
    if (scene.overlay === 'nfc-pairing') {
      const dialog = page.getByRole('dialog', { name: 'Pair NFC Tag' });
      await expect(dialog).toBeVisible();
      await expect(dialog.getByText('FIXTURE-TAG-001', { exact: true })).toBeVisible();
      await expect(dialog.getByPlaceholder('Search by name, vendor, or material...')).toBeVisible();
      await expect(dialog.getByText('Ocean blue spool', { exact: true })).toBeVisible();
    }
    if (scene.overlay === 'nfc-link-error') {
      const dialog = page.getByRole('dialog', { name: 'Pair NFC Tag' });
      await dialog.getByRole('button').filter({ hasText: 'Ocean blue spool' }).click();
      await expect(dialog.getByText('Link this tag to the selected spool?', { exact: true })).toBeVisible();
      const responsePromise = page.waitForResponse(response =>
        response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/nfc/link',
      );
      await dialog.getByRole('button', { name: 'Link', exact: true }).click();
      const response = await responsePromise;
      expect(response.status(), 'Only the synthetic NFC-link failure response is accepted').toBe(503);
      const errorDialog = page.getByRole('dialog', { name: 'Link Failed' });
      await expect(errorDialog.getByText('Failed to link tag', { exact: true })).toBeVisible();
      await expect(errorDialog.getByRole('button', { name: 'Try Again', exact: true })).toBeVisible();
    }
    if (scene.overlay === 'nfc-device-detail') {
      await page.getByRole('button', { name: 'Details', exact: true }).click();
      const dialog = page.getByRole('dialog', { name: 'Demo NFC Reader' });
      await expect(dialog).toBeVisible();
      await expect(dialog.getByText('Scan History', { exact: true })).toBeVisible();
      await expect(dialog.getByText('Demo workshop PLA', { exact: true })).toBeVisible();
    }
    if (scene.overlay === 'new-webhook') {
      await page.getByTestId('add-webhook-action').click();
      const dialog = page.getByRole('dialog', { name: 'Create Webhook' });
      await expect(dialog).toBeVisible();
      if (scene.overlayMutation === 'webhook-create-error') {
        await dialog.getByLabel('Name').fill('Synthetic coverage webhook');
        await dialog.getByLabel('URL').fill('https://example.invalid/fixture-only');
        const responsePromise = page.waitForResponse(response =>
          response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/webhooks',
        );
        await dialog.getByRole('button', { name: 'Create', exact: true }).click();
        const response = await responsePromise;
        expect(response.status()).toBe(503);
        expect(response.request().postDataJSON()).toEqual({
          name: 'Synthetic coverage webhook',
          url: 'https://example.invalid/fixture-only',
          eventTypes: '*',
          maxConsecutiveFailures: 10,
        });
        await expect(dialog.getByRole('alert')).toHaveText('Failed to save webhook');
        await expect(dialog.getByLabel('Name')).toHaveValue('Synthetic coverage webhook');
      }
    }
    if (scene.overlay === 'new-nozzle-material') {
      for (const tabId of ['nozzles', 'materials']) {
        const tab = page.locator(`#tab-${tabId}`);
        await tab.click();
        await expect(tab).toHaveAttribute('aria-selected', 'true');
      }
      await page.getByRole('button', { name: 'Add', exact: true }).click();
      await expect(page.getByRole('dialog', { name: 'Add Nozzle Material' })).toBeVisible();
    }
    if (scene.overlay === 'new-nozzle') {
      const nozzlesTab = page.locator('#tab-nozzles');
      await nozzlesTab.click();
      await expect(nozzlesTab).toHaveAttribute('aria-selected', 'true');
      await page.getByRole('button', { name: 'Add', exact: true }).click();
      await expect(page.getByRole('dialog', { name: 'Add Nozzle' })).toBeVisible();
    }
    if (scene.overlay === 'delete-nozzle') {
      const nozzlesTab = page.locator('#tab-nozzles');
      await nozzlesTab.click();
      await expect(nozzlesTab).toHaveAttribute('aria-selected', 'true');
      await page.getByRole('button', { name: 'Delete Workshop nozzle', exact: true }).click();
      const dialog = page.getByRole('dialog', { name: 'Delete Nozzle' });
      await expect(dialog).toBeVisible();
      await expect(dialog.getByText('Workshop nozzle', { exact: true })).toBeVisible();
    }
    if (scene.overlay === 'new-nozzle-manufacturer') {
      const nozzlesTab = page.locator('#tab-nozzles');
      await nozzlesTab.click();
      await expect(nozzlesTab).toHaveAttribute('aria-selected', 'true');
      await page.getByRole('button', { name: 'Add', exact: true }).click();
      await expect(page.getByRole('dialog', { name: 'Add Nozzle' })).toBeVisible();
      await page.getByRole('combobox', { name: 'Manufacturer' }).selectOption('__add_new__');
      await expect(page.getByRole('dialog', { name: 'Add New Manufacturer' })).toBeVisible();
    }
    if (scene.tabId) {
      const tab = page.locator(`#tab-${scene.tabId}`);
      await tab.click();
      await expect(tab).toHaveAttribute('aria-selected', 'true');
    }
    if (scene.subTabId) {
      const tab = page.locator(`#tab-${scene.subTabId}`);
      await tab.click();
      await expect(tab).toHaveAttribute('aria-selected', 'true');
    }
    if (scene.tab) {
      const tab = page.getByRole('tab', { name: scene.tab, exact: typeof scene.tab === 'string' });
      await tab.click();
      await expect(tab).toHaveAttribute('aria-selected', 'true');
    }
    if (scene.search) {
      await page.getByPlaceholder(scene.search.placeholder).fill(scene.search.value);
    }
    if (scene.marker) await expect(page.getByText(scene.marker, { exact: false }).first()).toBeVisible();
    for (const marker of scene.contentMarkers ?? []) {
      await expect(page.getByText(marker, { exact: false }).first()).toBeVisible();
    }
    if (scene.overlay !== 'capability-loading' && scene.overlay !== 'lazy-route-loading') {
      await expect(page.locator('main [aria-busy="true"]')).toHaveCount(0);
    }
    return { violations: networkViolations, pageErrors, releasePendingRequest };
  }

  const violations: string[] = [];
  await context.route('**/*', async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.origin !== siteOrigin || !url.pathname.startsWith('/PrintFarmer/') || request.method() !== 'GET') {
      violations.push(`Unexpected request: ${request.method()} ${url.origin}${url.pathname}`);
      await route.abort('blockedbyclient');
      return;
    }
    await route.continue();
  });
  await page.goto(new URL(scene.path, siteOrigin).toString());
  if (scene.heading) {
    await expect(page.getByRole('heading', { name: scene.heading }).first()).toBeVisible();
  }
  await page.locator('img').evaluateAll(images => Promise.all(images.map(image => {
    if (!(image instanceof HTMLImageElement)) throw new Error('Expected an HTML image');
    return image.decode();
  })));
  return { violations, pageErrors };
}

export async function assertPinnedFonts(page: Page) {
  await page.waitForFunction(families => {
    const registeredFamilies = new Set([...document.fonts].map(fontFace => fontFace.family.replaceAll('"', '')));
    return families.every(([family]) => registeredFamilies.has(family));
  }, expectedFamilies);
  const loaded = await page.evaluate(async families => {
    await document.fonts.ready;
    const entries = await Promise.all(families.map(async ([family, weight]) => {
      const faces = await document.fonts.load(`${weight} 16px "${family}"`, 'PrintFarmer 0123456789');
      return { family, loadedFaces: faces.filter(face => face.status === 'loaded').length };
    }));
    return entries;
  }, expectedFamilies);
  expect(loaded.filter(font => font.loadedFaces === 0), 'Every theme font must load from the pinned local packages').toEqual([]);
}

export async function assertAccessibility(page: Page) {
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
    .analyze();
  const violations = results.violations.map(({ id, impact, nodes }) => ({
    id,
    impact,
    nodes: nodes.map(node => ({ target: node.target, html: node.html, summary: node.failureSummary })),
  }));
  expect(violations, 'Automated axe WCAG 2.2 A/AA scan; this is not a full WCAG certification').toEqual([]);
}

export async function saveCandidate(page: Page, testInfo: TestInfo, scenarioId: string) {
  const candidatePath = testInfo.outputPath(`${scenarioId.replaceAll('.', '-')}.png`);
  const image = await page.screenshot({ path: candidatePath, fullPage: true, animations: 'disabled', caret: 'hide' });
  await testInfo.attach(`${scenarioId} candidate (not approved)`, {
    path: candidatePath,
    contentType: 'image/png',
  });
  return image;
}

export function assertNoScenarioNetworkViolations(violations: string[], pageErrors: string[]) {
  expect(violations, 'Only local synthetic fixtures may satisfy test requests').toEqual([]);
  expect(pageErrors, 'The rendered scenario must not throw page errors').toEqual([]);
}
