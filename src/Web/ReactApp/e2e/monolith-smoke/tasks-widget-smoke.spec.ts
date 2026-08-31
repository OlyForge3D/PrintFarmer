/**
 * Monolith smoke journey (issue #2286): task surface.
 *
 * `PrinterDashboard` only renders its `TasksWidget` (and every other
 * dashboard widget) once at least one printer exists in the DB
 * (`PrinterDashboard.tsx`, "No Printers Found" empty state otherwise). On a
 * fresh SQLite dev DB, `POST /api/printers` cannot currently create a
 * printer at all: `DatabaseInitializer.SeedAllAsync`'s YAML catalog seed
 * pipeline throws partway through (a pre-existing FK-constraint failure in
 * `SeedNozzlesAsync`, unrelated to this issue) and its single try/catch
 * swallows the exception without running the commented-out hardcoded
 * fallback, so `SeedPrinterModelsAsync` never runs and the DB ends up with
 * zero `PrinterModel` rows — the "Unknown" model `PrintersService` falls
 * back to for a new printer. That is a genuine, pre-existing seeding bug
 * (out of scope for #2286; see `fixtures/wire-contracts` non-goals), and it
 * makes the widget's "Create Task" UI unreachable in this environment.
 *
 * This journey therefore drives the task surface directly against the API
 * — as an authenticated `page.request` call sharing the page's auth token
 * (see `getStoredAuthToken`) — asserting both `POST /api/tasks` and
 * `GET /api/tasks` shapes against the canonical wire-contract corpus
 * (issue #2238). The companion `print-queue-smoke.spec.ts` in this suite
 * covers the printer/queue half of the "printer/queue/task surface"
 * acceptance criterion with a genuine UI-driven, network-intercepted
 * journey that needs no printer to exist.
 */
import { test, expect } from './fixtures/monolith-setup';
import { API_BASE_URL, getStoredAuthToken } from './fixtures/monolith-setup';
import { assertMatchesWireContractShape } from '../../src/test/wireContractShape';

const TASK_POPULATED_FIXTURE = 'api/tasks/tasks.populated.json';
const TASK_LIST_EMPTY_FIXTURE = 'api/tasks/tasks.empty-collection.json';

test.describe('monolith smoke: tasks', () => {
  test('POST then GET /api/tasks shapes match the wire-contract corpus', async ({ page }, testInfo) => {
    await page.goto('/');

    const token = await getStoredAuthToken(page);
    expect(token, 'Expected the monolithReady fixture to have stored an auth token').toBeTruthy();
    const authHeader = 'Bearer ' + token;
    const authHeaders = { Authorization: authHeader };

    // Before creating anything, this pristine seeded DB has zero tasks —
    // assert the real, live empty-list response against the corpus's
    // empty-collection fixture (rather than a hardcoded `[]` literal that
    // never touched the API), genuinely exercising the comparator's
    // "no template" branch for an empty corpus array. Only on the first
    // attempt: this task's own creation below is never rolled back, so a
    // Playwright CI retry (see playwright.config.ts) re-runs against a DB
    // that already has this test's own task in it, and asserting strict
    // emptiness there would be a false failure, not a genuine regression.
    if (testInfo.retry === 0) {
      const initialListResponse = await page.request.get(`${API_BASE_URL}/api/tasks`, { headers: authHeaders });
      expect(initialListResponse.status(), 'GET /api/tasks (before creating any task) must succeed').toBe(200);
      const initialList = await initialListResponse.json();
      expect(initialList, 'expected a pristine seeded DB to have zero tasks').toEqual([]);
      assertMatchesWireContractShape(initialList, TASK_LIST_EMPTY_FIXTURE);
    }

    const title = `Monolith smoke task ${Date.now()}`;
    const createResponse = await page.request.post(`${API_BASE_URL}/api/tasks`, {
      headers: authHeaders,
      data: { title },
    });
    expect(createResponse.status(), 'POST /api/tasks must succeed').toBeLessThan(300);

    const created = await createResponse.json();
    assertMatchesWireContractShape(created, TASK_POPULATED_FIXTURE);
    expect(created.title).toBe(title);

    const listResponse = await page.request.get(`${API_BASE_URL}/api/tasks`, { headers: authHeaders });
    expect(listResponse.status(), 'GET /api/tasks must succeed').toBe(200);

    const list = await listResponse.json();
    expect(Array.isArray(list), 'GET /api/tasks must return an array').toBe(true);

    // Find the task this test actually created (by id), rather than
    // trusting list[0] — another task could already be first in the list
    // even if the POST above silently failed to persist.
    const createdInList = list.find((t: { id: unknown }) => t.id === created.id);
    expect(createdInList, 'expected the task just created to appear in GET /api/tasks by id').toBeTruthy();
    assertMatchesWireContractShape(createdInList, TASK_POPULATED_FIXTURE);
  });
});
