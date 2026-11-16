import { test, expect } from '../fixtures/emulator-setup';
import type { Page } from '@playwright/test';

const WEBHOOK_URL = 'https://webhook-test.invalid/delivery';
const INITIAL_WEBHOOK_NAME = 'Fixture Webhook';
const EDITED_WEBHOOK_NAME = 'Edited Fixture Webhook';

type WebhookFixture = {
  id: string;
  name: string;
  url: string;
  hasSecret: boolean;
  eventTypes: string;
  isActive: boolean;
  consecutiveFailures: number;
  maxConsecutiveFailures: number;
  createdAt: string;
  lastDeliveryAt?: string;
  lastSuccessAt?: string;
};

type WebhookWrite = {
  name?: string;
  url?: string;
  secret?: string;
  eventTypes?: string;
  isActive?: boolean;
  maxConsecutiveFailures?: number;
};

type DeliveryFixture = {
  id: string;
  eventType: string;
  statusCode?: number;
  success: boolean;
  errorMessage?: string;
  attempt: number;
  durationMs?: number;
  createdAt: string;
};

type WebhookFixtureOptions = {
  deleteStatus?: number;
  testStatus?: number;
};

async function installWebhookFixture(
  page: Page,
  options: WebhookFixtureOptions = {},
) {
  let webhooks: WebhookFixture[] = [{
    id: 'fixture-webhook-1',
    name: INITIAL_WEBHOOK_NAME,
    url: WEBHOOK_URL,
    hasSecret: false,
    eventTypes: '*',
    isActive: true,
    consecutiveFailures: 0,
    maxConsecutiveFailures: 10,
    createdAt: '2026-01-01T00:00:00Z',
  }];
  let deliveries: DeliveryFixture[] = [];
  const requests: { method: string; path: string }[] = [];
  const createdWebhookIds: string[] = [];

  await page.route('**/api/webhooks**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    requests.push({ method: request.method(), path });

    if (request.method() === 'GET' && path.endsWith('/event-types')) {
      await route.fulfill({ json: ['printer.updated', 'print.completed'] });
      return;
    }

    if (request.method() === 'GET' && path.endsWith('/deliveries')) {
      await route.fulfill({ json: deliveries });
      return;
    }

    if (request.method() === 'GET' && path.endsWith('/webhooks')) {
      await route.fulfill({ json: webhooks });
      return;
    }

    if (request.method() === 'POST' && path.endsWith('/webhooks')) {
      const body = request.postDataJSON() as Required<Pick<WebhookWrite, 'name' | 'url'>> & WebhookWrite;
      const created: WebhookFixture = {
        id: 'fixture-webhook-created',
        name: body.name,
        url: body.url,
        hasSecret: Boolean(body.secret),
        eventTypes: body.eventTypes ?? '*',
        isActive: body.isActive ?? true,
        consecutiveFailures: 0,
        maxConsecutiveFailures: body.maxConsecutiveFailures ?? 10,
        createdAt: '2026-01-01T00:00:02Z',
      };
      webhooks = [...webhooks, created];
      createdWebhookIds.push(created.id);
      await route.fulfill({ status: 201, json: created });
      return;
    }

    const webhookId = path.match(/\/webhooks\/([^/]+)(?:\/test)?$/)?.[1];
    if (!webhookId) {
      await route.continue();
      return;
    }

    if (request.method() === 'POST' && path.endsWith('/test')) {
      if (options.testStatus && options.testStatus >= 400) {
        await route.fulfill({ status: options.testStatus, json: { message: 'Fixture delivery failed' } });
        return;
      }

      deliveries = [{
        id: 'fixture-delivery-1',
        eventType: 'test',
        statusCode: 202,
        success: true,
        attempt: 1,
        durationMs: 12,
        createdAt: '2026-01-01T00:00:01Z',
      }];
      await route.fulfill({ status: 200, json: { success: true } });
      return;
    }

    if (request.method() === 'PUT') {
      const body = request.postDataJSON() as WebhookWrite;
      webhooks = webhooks.map((webhook) => webhook.id === webhookId
        ? {
          ...webhook,
          ...body,
          hasSecret: body.secret ? true : webhook.hasSecret,
        }
        : webhook);
      await route.fulfill({ json: webhooks.find((webhook) => webhook.id === webhookId) });
      return;
    }

    if (request.method() === 'DELETE') {
      if (options.deleteStatus && options.deleteStatus >= 400) {
        await route.fulfill({ status: options.deleteStatus, json: { message: 'Fixture delete failed' } });
        return;
      }

      webhooks = webhooks.filter((webhook) => webhook.id !== webhookId);
      await route.fulfill({ status: 204 });
      return;
    }

    await route.continue();
  });

  return { requests, createdWebhookIds };
}

async function openWebhooksPage(page: Page) {
  await page.goto('/admin/settings?tab=integrations&sub=webhooks');
  await expect(page.getByRole('heading', { name: 'Webhooks', exact: true })).toBeVisible();
}

test.describe('Admin Webhooks — deterministic emulator fixtures', () => {
  test('loads the webhook page and opens the create form', async ({ page }) => {
    await installWebhookFixture(page);
    await openWebhooksPage(page);

    await expect(page.getByRole('article', { name: `${INITIAL_WEBHOOK_NAME} webhook`, exact: true })).toBeVisible();
    await page.getByTestId('add-webhook-action').click();

    const dialog = page.getByRole('dialog', { name: 'Create Webhook', exact: true });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole('textbox', { name: 'Name', exact: true })).toBeVisible();
    await expect(dialog.getByRole('textbox', { name: 'URL', exact: true })).toBeVisible();
  });

  test('covers create success with the intercepted fixture', async ({ page }) => {
    const fixture = await installWebhookFixture(page);
    await openWebhooksPage(page);
    await page.getByTestId('add-webhook-action').click();

    const dialog = page.getByRole('dialog', { name: 'Create Webhook', exact: true });
    await dialog.getByRole('textbox', { name: 'Name', exact: true }).fill('Created Fixture Webhook');
    await dialog.getByRole('textbox', { name: 'URL', exact: true }).fill(WEBHOOK_URL);
    await dialog.getByRole('checkbox', { name: 'All events', exact: true }).check();
    await dialog.getByRole('button', { name: 'Create', exact: true }).click();

    await expect(dialog).toBeHidden();
    await expect(page.getByRole('article', { name: 'Created Fixture Webhook webhook', exact: true })).toBeVisible();
    expect(fixture.requests).toContainEqual({ method: 'POST', path: '/api/webhooks' });
    expect(fixture.createdWebhookIds).toEqual(['fixture-webhook-created']);
  });

  test('covers edit success and renders the updated webhook', async ({ page }) => {
    const fixture = await installWebhookFixture(page);
    await openWebhooksPage(page);

    const card = page.getByRole('article', { name: `${INITIAL_WEBHOOK_NAME} webhook`, exact: true });
    await card.getByRole('button', { name: 'Edit webhook', exact: true }).click();

    const dialog = page.getByRole('dialog', { name: 'Edit Webhook', exact: true });
    await expect(dialog.getByRole('textbox', { name: 'Name', exact: true })).toHaveValue(INITIAL_WEBHOOK_NAME);
    await dialog.getByRole('textbox', { name: 'Name', exact: true }).fill(EDITED_WEBHOOK_NAME);
    await dialog.getByRole('button', { name: 'Save', exact: true }).click();

    await expect(dialog).toBeHidden();
    await expect(page.getByRole('article', { name: `${EDITED_WEBHOOK_NAME} webhook`, exact: true })).toBeVisible();
    expect(fixture.requests).toContainEqual({ method: 'PUT', path: '/api/webhooks/fixture-webhook-1' });
  });

  test('covers delete confirmation, successful result, and removal', async ({ page }) => {
    const fixture = await installWebhookFixture(page);
    await openWebhooksPage(page);

    const card = page.getByRole('article', { name: `${INITIAL_WEBHOOK_NAME} webhook`, exact: true });
    await card.getByRole('button', { name: 'Delete webhook', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Delete Webhook', exact: true });
    await expect(dialog).toContainText('All delivery logs will be removed.');
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(dialog).toBeHidden();
    await expect(card).toBeVisible();

    await card.getByRole('button', { name: 'Delete webhook', exact: true }).click();
    await page.getByRole('dialog', { name: 'Delete Webhook', exact: true })
      .getByRole('button', { name: 'Delete', exact: true }).click();

    await expect(card).toBeHidden();
    await expect(page.getByText('Webhook deleted', { exact: true })).toBeVisible();
    expect(fixture.requests).toContainEqual({ method: 'DELETE', path: '/api/webhooks/fixture-webhook-1' });
  });

  test('recovers from delete failure without removing or persisting the fixture webhook', async ({ page }) => {
    const fixture = await installWebhookFixture(page, { deleteStatus: 500 });
    await openWebhooksPage(page);

    const card = page.getByRole('article', { name: `${INITIAL_WEBHOOK_NAME} webhook`, exact: true });
    await card.getByRole('button', { name: 'Delete webhook', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Delete Webhook', exact: true });
    await dialog.getByRole('button', { name: 'Delete', exact: true }).click();

    await expect(page.getByText('Failed to delete webhook', { exact: true })).toBeVisible();
    await expect(dialog).toBeVisible();
    await expect(card).toBeVisible();
    expect(fixture.requests.filter(({ method, path }) => method === 'DELETE' && path.endsWith('fixture-webhook-1'))).toHaveLength(1);
  });

  test('covers intercepted Test Delivery success and its delivery result', async ({ page }) => {
    const fixture = await installWebhookFixture(page);
    await openWebhooksPage(page);

    const card = page.getByRole('article', { name: `${INITIAL_WEBHOOK_NAME} webhook`, exact: true });
    await expect(card).toContainText(WEBHOOK_URL);
    await card.getByRole('button', { name: 'Send test event', exact: true }).click();

    await expect(page.getByText('Test event sent', { exact: true })).toBeVisible();
    await card.getByRole('button', { name: 'View deliveries', exact: true }).click();

    const dialog = page.getByRole('dialog', { name: 'Recent Deliveries', exact: true });
    await expect(dialog).toContainText('Success');
    await expect(dialog).toContainText('202');
    expect(fixture.requests).toContainEqual({ method: 'POST', path: '/api/webhooks/fixture-webhook-1/test' });
    expect(fixture.requests).toContainEqual({ method: 'GET', path: '/api/webhooks/fixture-webhook-1/deliveries' });
  });

  test('renders Test Delivery failure without outbound traffic or card removal', async ({ page }) => {
    const fixture = await installWebhookFixture(page, { testStatus: 502 });
    await openWebhooksPage(page);

    const card = page.getByRole('article', { name: `${INITIAL_WEBHOOK_NAME} webhook`, exact: true });
    await card.getByRole('button', { name: 'Send test event', exact: true }).click();

    await expect(page.getByText('Failed to send test event', { exact: true })).toBeVisible();
    await expect(card).toBeVisible();
    expect(fixture.requests).toContainEqual({ method: 'POST', path: '/api/webhooks/fixture-webhook-1/test' });
    expect(fixture.requests.some(({ path }) => path.includes('webhook-test.invalid'))).toBe(false);
  });
});
