import { test, expect } from '../fixtures/emulator-setup';
import type { Page, Route } from '@playwright/test';

const ROOT_LOCATION_ID = 'location-root';
const CHILD_LOCATION_ID = 'location-child';
const TARGET_LOCATION_ID = 'location-target';
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
  {
    id: TARGET_LOCATION_ID,
    name: 'Annex',
    description: 'Synthetic move target outside Warehouse',
    parentId: null,
    path: '/Annex',
    depth: 0,
    sortOrder: 1,
    printerCount: 0,
    totalPrinterCount: 0,
    children: [],
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
    id: TARGET_LOCATION_ID,
    name: 'Annex',
    description: 'Synthetic move target outside Warehouse',
    parentId: null,
    path: '/Annex',
    depth: 0,
    sortOrder: 1,
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
  await page.route(`**/api/locations/${TARGET_LOCATION_ID}/printers/subtree`, (route) =>
    fulfillJson(route, [])
  );
  await page.route(`**/api/locations/${ROOT_LOCATION_ID}/move`, async (route) => {
    moveRequests.push(JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>);
    await fulfillJson(route, {
      ...locations()[0],
      parentId: TARGET_LOCATION_ID,
      path: '/Annex/Warehouse',
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
    await expect(page.getByRole('treeitem', { name: /North Wing/ })).toHaveCount(0);
    await page.getByRole('treeitem', { name: /Annex/ }).click();
    await page.getByRole('button', { name: 'Move location' }).click();

    await expect.poll(() => moveRequests).toEqual([{ newParentId: TARGET_LOCATION_ID }]);

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
    const scheduledJobs: Array<Record<string, unknown>> = [];

    await page.route('**/api/job-scheduling/scheduled*', (route) => fulfillJson(route, scheduledJobs));
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
      const scheduledJob = {
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
      };
      scheduledJobs.push(scheduledJob);
      await fulfillJson(route, scheduledJob);
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
    await expect(page.getByText('Daily × 2')).toBeVisible();
  });

  test('keeps location move state and reports an intercepted mutation failure', async ({ page }) => {
    await page.route('**/api/locations/tree', (route) => fulfillJson(route, locationTree()));
    await page.route('**/api/locations', (route) =>
      route.request().method() === 'GET'
        ? fulfillJson(route, locations())
        : fulfillJson(route, {})
    );
    await page.route(`**/api/locations/${ROOT_LOCATION_ID}/printers/subtree`, (route) =>
      fulfillJson(route, [])
    );
    await page.route(`**/api/locations/${ROOT_LOCATION_ID}/move`, (route) =>
      fulfillJson(route, { message: 'Synthetic move rejected' }, 409)
    );

    await page.goto('/locations');
    await page.getByRole('button', { name: 'Manage' }).click();
    await page.getByRole('row', { name: /Warehouse/ }).getByRole('button', { name: 'Move' }).click();
    await page.getByRole('button', { name: /None \(top-level\)/ }).last().click();
    await page.getByRole('treeitem', { name: /Annex/ }).click();
    await page.getByRole('button', { name: 'Move location' }).click();

    await expect(page.getByRole('alert')).toContainText('Failed to move location');
    await expect(page.getByRole('dialog', { name: /Move Warehouse/ })).toBeVisible();
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
    await expect(completeRow).toHaveCount(0);
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

  test('retains failed tasks, allows one retry, and suppresses duplicate effects', async ({ page }) => {
    const tasks = [
      {
        id: 'task-failure',
        taskType: 'Custom',
        entityType: 'Farm',
        entityId: 'farm',
        title: 'Fail synthetic task',
        description: 'Terminal failure outcome',
        status: 'Pending',
        priority: 'Normal',
        createdAt: '2026-01-01T00:00:00Z',
        relatedEntityCount: 0,
        anchorKind: 'now',
        sourceKind: 'unspecified',
      },
      {
        id: 'task-retry',
        taskType: 'Custom',
        entityType: 'Farm',
        entityId: 'farm',
        title: 'Retry synthetic task',
        description: 'Transient failure outcome',
        status: 'Pending',
        priority: 'Normal',
        createdAt: '2026-01-01T00:00:00Z',
        relatedEntityCount: 0,
        anchorKind: 'now',
        sourceKind: 'unspecified',
      },
      {
        id: 'task-duplicate',
        taskType: 'Custom',
        entityType: 'Farm',
        entityId: 'farm',
        title: 'Duplicate synthetic task',
        description: 'Single-flight outcome',
        status: 'Pending',
        priority: 'Normal',
        createdAt: '2026-01-01T00:00:00Z',
        relatedEntityCount: 0,
        anchorKind: 'now',
        sourceKind: 'unspecified',
      },
    ];
    const actionRequests: string[] = [];
    const actionEffects: string[] = [];
    let releaseFailure: () => void = () => {};
    const failureResponse = new Promise<void>((resolve) => {
      releaseFailure = resolve;
    });
    let releaseDuplicate: () => void = () => {};
    const duplicateResponse = new Promise<void>((resolve) => {
      releaseDuplicate = resolve;
    });

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
      if (!taskId || !action) {
        return route.continue();
      }

      const requestKey = `${action}:${taskId}`;
      actionRequests.push(requestKey);

      if (taskId === 'task-failure') {
        await failureResponse;
        await fulfillJson(route, { message: 'Synthetic terminal failure' }, 500);
        return;
      }

      if (taskId === 'task-retry' && actionRequests.filter((request) => request === requestKey).length === 1) {
        await fulfillJson(route, { message: 'Synthetic transient failure' }, 409);
        return;
      }

      if (taskId === 'task-duplicate' && actionRequests.filter((request) => request === requestKey).length === 1) {
        await duplicateResponse;
      }

      const task = tasks.find((candidate) => candidate.id === taskId);
      if (task) {
        task.status = action === 'complete' ? 'Completed' : action === 'skip' ? 'Skipped' : 'Dismissed';
        actionEffects.push(requestKey);
      }
      await route.fulfill({ status: 204 });
    });

    await page.goto('/dashboard');
    await expect(page.getByText('Fail synthetic task')).toBeVisible();

    const failureRow = page.locator('[data-testid="tasks-widget-row"][data-task-id="task-failure"]');
    const retryRow = page.locator('[data-testid="tasks-widget-row"][data-task-id="task-retry"]');
    const duplicateRow = page.locator('[data-testid="tasks-widget-row"][data-task-id="task-duplicate"]');

    const failureComplete = failureRow.getByTestId('tasks-widget-complete');
    await failureComplete.click();
    await expect.poll(() => actionRequests).toEqual(['complete:task-failure']);
    await expect(failureComplete).toBeDisabled();
    releaseFailure();
    await expect(failureComplete).toBeEnabled();
    await expect(failureRow).toBeVisible();
    expect(actionEffects).toEqual([]);

    await retryRow.getByTestId('tasks-widget-complete').click();
    await expect.poll(() => actionRequests).toEqual([
      'complete:task-failure',
      'complete:task-retry',
    ]);
    await expect(retryRow).toBeVisible();
    expect(actionEffects).toEqual([]);

    await retryRow.getByTestId('tasks-widget-complete').click();
    await expect.poll(() => actionRequests).toEqual([
      'complete:task-failure',
      'complete:task-retry',
      'complete:task-retry',
    ]);
    await expect.poll(() => actionEffects).toEqual(['complete:task-retry']);
    await expect(retryRow).toHaveCount(0);

    const duplicateComplete = duplicateRow.getByTestId('tasks-widget-complete');
    await duplicateComplete.click();
    await expect.poll(() => actionRequests).toEqual([
      'complete:task-failure',
      'complete:task-retry',
      'complete:task-retry',
      'complete:task-duplicate',
    ]);
    await expect(duplicateComplete).toBeDisabled();
    await expect.poll(() => actionEffects).toEqual(['complete:task-retry']);
    releaseDuplicate();

    await expect.poll(() => actionEffects).toEqual([
      'complete:task-retry',
      'complete:task-duplicate',
    ]);
    await expect(duplicateRow).toHaveCount(0);
    expect(actionRequests).toEqual([
      'complete:task-failure',
      'complete:task-retry',
      'complete:task-retry',
      'complete:task-duplicate',
    ]);
    expect(actionEffects).toEqual([
      'complete:task-retry',
      'complete:task-duplicate',
    ]);
  });
});
