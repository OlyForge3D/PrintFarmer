import { test, expect } from '../fixtures/emulator-setup';
import type { Page, Route } from '@playwright/test';

const ROOT_LOCATION_ID = 'location-root';
const CHILD_LOCATION_ID = 'location-child';
const PRINTER_ID = 'printer-synthetic';
const JOB_ID = 'job-synthetic';

const locationTree = () => [
  {
    id: ROOT_LOCATION_ID,
    name: 'Warehouse',
    description: 'Synthetic root location',
    parentId: null,
    path: '/Warehouse',
    depth: 0,
    sortOrder: 0,
    printerCount: 0,
    totalPrinterCount: 0,
    children: [
      {
        id: CHILD_LOCATION_ID,
        name: 'North Wing',
        description: 'Synthetic child location',
        parentId: ROOT_LOCATION_ID,
        path: '/Warehouse/North Wing',
        depth: 1,
        sortOrder: 0,
        printerCount: 0,
        totalPrinterCount: 0,
        children: [],
      },
    ],
  },
];

const locations = () => [
  {
    id: ROOT_LOCATION_ID,
    name: 'Warehouse',
    description: 'Synthetic root location',
    parentId: null,
    path: '/Warehouse',
    depth: 0,
    sortOrder: 0,
    printerCount: 0,
    totalPrinterCount: 0,
    createdAt: '2026-01-01T00:00:00Z',
    modifiedAt: '2026-01-01T00:00:00Z',
    isActive: true,
  },
  {
    id: CHILD_LOCATION_ID,
    name: 'North Wing',
    description: 'Synthetic child location',
    parentId: ROOT_LOCATION_ID,
    path: '/Warehouse/North Wing',
    depth: 1,
    sortOrder: 0,
    printerCount: 0,
    totalPrinterCount: 0,
    createdAt: '2026-01-01T00:00:00Z',
    modifiedAt: '2026-01-01T00:00:00Z',
    isActive: true,
  },
];

const printer = (locationId?: string) => ({
  id: PRINTER_ID,
  name: 'Synthetic Printer',
  backend: 0,
  backendUrl: 'http://synthetic.invalid',
  location: locationId ? { id: locationId, name: 'North Wing' } : null,
});

async function fulfillJson(route: Route, body: unknown, status = 200): Promise<void> {
  await route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function interceptLocationWorkflow(page: Page): Promise<{
  moveRequests: Array<Record<string, unknown>>;
  assignmentRequests: Array<Record<string, unknown>>;
}> {
  const moveRequests: Array<Record<string, unknown>> = [];
  const assignmentRequests: Array<Record<string, unknown>> = [];

  await page.route('**/api/locations/tree', (route) => fulfillJson(route, locationTree()));
  await page.route('**/api/locations', (route) => {
    if (route.request().method() === 'GET') return fulfillJson(route, locations());
    return fulfillJson(route, {});
  });
  await page.route('**/api/printers', (route) => {
    if (route.request().method() === 'GET') return fulfillJson(route, [printer()]);
    return route.continue();
  });
  await page.route(`**/api/locations/${ROOT_LOCATION_ID}/printers/subtree`, (route) =>
    fulfillJson(route, [])
  );
  await page.route(`**/api/locations/${CHILD_LOCATION_ID}/printers/subtree`, (route) =>
    fulfillJson(route, [])
  );
  await page.route(`**/api/locations/${ROOT_LOCATION_ID}/move`, async (route) => {
    moveRequests.push(JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>);
    await fulfillJson(route, {
      ...locations()[0],
      parentId: CHILD_LOCATION_ID,
      path: '/North Wing/Warehouse',
    });
  });
  await page.route(`**/api/printers/${PRINTER_ID}/location`, async (route) => {
    assignmentRequests.push(JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>);
    await fulfillJson(route, printer(CHILD_LOCATION_ID));
  });

  return { moveRequests, assignmentRequests };
}

test.describe('Location and queue task workflows — Emulator', () => {
  test('moves a location, exercises tree-picker states, and assigns a printer without persistent writes', async ({
    page,
  }) => {
    const { moveRequests, assignmentRequests } = await interceptLocationWorkflow(page);

    await page.goto('/locations');
    await page.getByRole('button', { name: 'Manage' }).click();

    const warehouseRow = page.getByRole('row', { name: /Warehouse/ });
    await warehouseRow.getByRole('button', { name: 'Move' }).click();

    const picker = page.getByRole('button', { name: /None \(top-level\)/ }).last();
    await picker.click();
    const search = page.getByPlaceholder('Search locations...');
    await search.fill('does-not-exist');
    await expect(page.getByText('No locations found')).toBeVisible();
    await search.fill('');
    await page.getByRole('treeitem', { name: /North Wing/ }).click();
    await page.getByRole('button', { name: 'Move location' }).click();

    await expect.poll(() => moveRequests).toEqual([{ newParentId: CHILD_LOCATION_ID }]);

    await page.getByRole('tab', { name: 'Assignments' }).click();
    const syntheticPrinter = page.locator('[draggable="true"]').filter({ hasText: 'Synthetic Printer' });
    const northWingDropZone = page.locator('div').filter({
      has: page.getByRole('heading', { name: 'North Wing' }),
    }).filter({ hasText: 'Drag printers here' }).first();
    await syntheticPrinter.dragTo(northWingDropZone);

    await expect.poll(() => assignmentRequests).toEqual([{ locationId: CHILD_LOCATION_ID }]);
    await expect(northWingDropZone).toContainText('Synthetic Printer');
  });

  test('schedules a repeated queue job with deterministic recurrence payload', async ({ page }) => {
    const scheduleRequests: Array<Record<string, unknown>> = [];

    await page.route('**/api/job-scheduling/scheduled*', (route) => fulfillJson(route, []));
    await page.route('**/api/job-scheduling/timezones', (route) =>
      fulfillJson(route, [{ id: 'UTC', displayName: 'Coordinated Universal Time', offset: '+00:00' }])
    );
    await page.route('**/api/job-queue-analytics**', (route) =>
      fulfillJson(route, [{
        job: { id: JOB_ID, name: 'Synthetic queue job', status: 'Queued' },
        assignedPrinter: { id: PRINTER_ID, name: 'Synthetic Printer' },
        gcodeFile: { name: 'synthetic.gcode' },
      }])
    );
    await page.route(`**/api/job-scheduling/${JOB_ID}/schedule`, async (route) => {
      scheduleRequests.push(JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>);
      await fulfillJson(route, {
        id: 'schedule-synthetic',
        jobId: JOB_ID,
        jobName: 'Synthetic queue job',
        printerName: 'Synthetic Printer',
        printerId: PRINTER_ID,
        scheduledStartTimeUtc: '2026-12-01T10:00:00Z',
        scheduledLocalTime: '2026-12-01T10:00:00',
        timeZone: 'UTC',
        recurrencePattern: 'Daily',
        recurrenceInterval: 2,
        recurrenceEndTimeUtc: null,
        isActive: true,
        isPaused: false,
        requiresOperatorReauthorization: false,
        status: 'active',
      });
    });

    await page.goto('/scheduling');
    await page.getByRole('button', { name: 'Schedule Job' }).click();
    await page.getByLabel('Print Job').selectOption(JOB_ID);
    await page.getByLabel('Scheduled Date').fill('2026-12-01');
    await page.getByLabel('Scheduled Time').fill('10:00');
    await page.getByLabel('Timezone').selectOption('UTC');
    await page.getByLabel('Recurrence').selectOption('daily');
    await page.getByLabel('Recurrence Interval').fill('2');
    await page.getByRole('button', { name: 'Schedule Job' }).last().click();

    await expect.poll(() => scheduleRequests).toEqual([{
      scheduledLocalTime: '2026-12-01T10:00:00',
      timeZone: 'UTC',
      recurrencePattern: 'Daily',
      recurrenceInterval: 2,
    }]);
  });

  test('completes, skips, and dismisses synthetic tasks without mutating farm state', async ({ page }) => {
    const tasks = [
      {
        id: 'task-complete',
        taskType: 'Custom',
        entityType: 'Farm',
        entityId: 'farm',
        title: 'Complete synthetic task',
        description: 'Terminal completion outcome',
        status: 'Pending',
        priority: 'Normal',
        createdAt: '2026-01-01T00:00:00Z',
        relatedEntityCount: 0,
        anchorKind: 'now',
        sourceKind: 'unspecified',
      },
      {
        id: 'task-skip',
        taskType: 'Custom',
        entityType: 'Farm',
        entityId: 'farm',
        title: 'Skip synthetic task',
        status: 'Pending',
        priority: 'Low',
        createdAt: '2026-01-01T00:00:00Z',
        relatedEntityCount: 0,
        anchorKind: 'timeline',
        sourceKind: 'unspecified',
      },
      {
        id: 'task-dismiss',
        taskType: 'Custom',
        entityType: 'Farm',
        entityId: 'farm',
        title: 'Dismiss synthetic task',
        status: 'Pending',
        priority: 'High',
        createdAt: '2026-01-01T00:00:00Z',
        relatedEntityCount: 0,
        anchorKind: 'anytimeToday',
        sourceKind: 'unspecified',
      },
    ];
    const actionRequests: string[] = [];

    await page.route('**/api/tasks?view=shift', (route) =>
      fulfillJson(route, {
        generatedAt: '2026-01-01T00:00:00Z',
        groups: [{
          anchorKind: 'now',
          tasks: tasks.filter((task) => task.status === 'Pending'),
        }],
      })
    );
    await page.route('**/api/tasks/**', async (route) => {
      if (route.request().method() !== 'POST') {
        return route.continue();
      }
      const taskId = route.request().url().match(/tasks\/([^/]+)\//)?.[1];
      const action = route.request().url().match(/\/(complete|skip|dismiss)$/)?.[1];
      if (taskId && action) {
        actionRequests.push(`${action}:${taskId}`);
        const task = tasks.find((candidate) => candidate.id === taskId);
        if (task) task.status = action === 'complete' ? 'Completed' : action === 'skip' ? 'Skipped' : 'Dismissed';
      }
      await route.fulfill({ status: 204 });
    });

    await page.goto('/dashboard');
    await expect(page.getByText('Complete synthetic task')).toBeVisible();

    const completeRow = page.locator('[data-testid="tasks-widget-row"][data-task-id="task-complete"]');
    const skipRow = page.locator('[data-testid="tasks-widget-row"][data-task-id="task-skip"]');
    const dismissRow = page.locator('[data-testid="tasks-widget-row"][data-task-id="task-dismiss"]');

    await completeRow.getByTestId('tasks-widget-complete').click();
    await expect.poll(() => actionRequests).toContain('complete:task-complete');
    await skipRow.getByTestId('tasks-widget-skip').click();
    await expect.poll(() => actionRequests).toContain('skip:task-skip');
    await dismissRow.getByTestId('tasks-widget-dismiss').click();
    await expect.poll(() => actionRequests).toContain('dismiss:task-dismiss');

    await expect(page.locator('[data-testid="tasks-widget-row"]')).toHaveCount(0);
    expect(actionRequests).toEqual([
      'complete:task-complete',
      'skip:task-skip',
      'dismiss:task-dismiss',
    ]);
  });
});
