import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';

function property(name: string, type: string, label: string) {
  return {
    name,
    type,
    attributes: [],
    display: {
      name: label,
    },
  };
}

async function mockSettingsShellApi(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('auth-token', 'e2e-token');
    localStorage.setItem('pf-tour-seen-settings', 'true');
  });

  await page.route('**/*', async (route: Route) => {
    const url = new URL(route.request().url());
    if (!url.pathname.startsWith('/api/')) {
      await route.fallback();
      return;
    }

    const fulfillJson = (body: unknown, status = 200) =>
      route.fulfill({
        status,
        contentType: 'application/json',
        body: JSON.stringify(body),
      });

    if (url.pathname === '/api/setup/status') {
      return fulfillJson({ needsSetup: false });
    }
    if (url.pathname === '/api/auth/me') {
      return fulfillJson({
        id: 'admin-1',
        username: 'admin',
        email: 'admin@test.local',
        isActive: true,
        emailConfirmed: true,
        createdAt: '2025-01-01T00:00:00Z',
        roles: ['farm_admin'],
        permissions: ['system_settings:admin'],
      });
    }
    if (url.pathname === '/api/settings/SignalR') {
      return fulfillJson({ enabled: false, autoReconnect: true, reconnectDelaysSeconds: [0, 2, 10] });
    }
    if (url.pathname === '/api/job-queue/subscription-resources') {
      return fulfillJson({ printerIds: [], jobIds: [], projectIds: [] });
    }
    if (url.pathname === '/api/printers' || url.pathname === '/api/workers/' || url.pathname === '/api/workers') {
      return fulfillJson([]);
    }
    if (url.pathname === '/api/system/info') {
      return fulfillJson({
        app: { version: 'test', hostname: 'localhost' },
        cpu: { usagePercent: 12, cores: 8 },
        memory: { usedBytes: 1, totalBytes: 2 },
        disk: { usedBytes: 1, totalBytes: 2 },
        services: [],
        database: { provider: 'sqlite', canConnect: true, pendingMigrations: 0 },
      });
    }
    if (url.pathname === '/api/notifications') {
      return fulfillJson([]);
    }
    if (url.pathname === '/api/notifications/unread/count') {
      return fulfillJson({ unreadCount: 0 });
    }
    if (url.pathname === '/api/tasks/count') {
      return fulfillJson({ count: 0 });
    }
    if (url.pathname === '/api/system/capabilities') {
      return fulfillJson({});
    }
    if (url.pathname === '/api/auto-dispatch/status') {
      return fulfillJson({
        isEnabled: false,
        activeJobCount: 0,
        enabledPrinterCount: 0,
        availableWorkerCount: 0,
        defaultsConfigured: false,
      });
    }
    if (url.pathname === '/api/settings/Slicer') {
      return fulfillJson({ defaultSlicer: 'OrcaSlicer' });
    }
    if (url.pathname === '/api/settings/metadata') {
      return fulfillJson([
        {
          key: 'SystemLog',
          className: 'SystemLogSettings',
          displayName: 'System Log',
          description: 'Log retention.',
          group: 'System',
          order: 1,
          properties: [
            property('enabled', 'Boolean', 'Log Enabled'),
            property('retentionDays', 'number', 'Retention Days'),
          ],
        },
        {
          key: 'NetworkDiscovery',
          className: 'NetworkDiscoverySettings',
          displayName: 'Network Discovery',
          description: 'Discovery cadence configuration.',
          group: 'System',
          order: 2,
          properties: [
            property('enableDiscovery', 'Boolean', 'Enable Discovery'),
            property('scanIntervalMinutes', 'number', 'Scan interval (minutes)'),
          ],
        },
      ]);
    }
    if (url.pathname === '/api/settings/groups') {
      return fulfillJson([{ key: 'System', displayName: 'System', order: 1 }]);
    }
    if (url.pathname === '/api/settings') {
      return fulfillJson({
        SystemLog: { enabled: true, retentionDays: 30 },
        NetworkDiscovery: { enableDiscovery: true, scanIntervalMinutes: 10 },
      });
    }

    return fulfillJson({});
  });
}

test.describe('settings exact-field focus repair (#2556)', () => {
  test('keeps the exact deep-link target focused after the page settles', async ({ page }) => {
    await mockSettingsShellApi(page);
    await page.goto('/admin/settings?scope=system&tab=general&sub=system&field=NetworkDiscovery.scanIntervalMinutes');

    const input = page.getByLabel('Scan interval (minutes)');
    await expect(input).toBeVisible();
    await expect
      .poll(async () => page.evaluate(() => {
        const inputElement = document.querySelector('[data-setting-property="NetworkDiscovery.scanIntervalMinutes"] input');
        return inputElement instanceof HTMLElement && document.activeElement === inputElement;
      }))
      .toBe(true);

    await page.waitForTimeout(1500);

    await expect
      .poll(async () => page.evaluate(() => {
        const inputElement = document.querySelector('[data-setting-property="NetworkDiscovery.scanIntervalMinutes"] input');
        return inputElement instanceof HTMLElement && document.activeElement === inputElement;
      }))
      .toBe(true);
  });
});
