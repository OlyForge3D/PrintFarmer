/**
 * Monolith smoke journey (issue #2286): printer/queue surface.
 *
 * Navigates to the real Print Queue dashboard's "Timeline" tab
 * (`/printQueue/timeline`), which unconditionally fetches
 * `GET /api/job-queue` via `QueueTimelineTab`'s `getQueueOverview` query
 * (`api.ts`) regardless of whether any printers exist — unlike the main
 * dashboard's `TasksWidget`, this surface has no printer-gated empty state
 * (see `tasks-widget-smoke.spec.ts` for why that matters here). It
 * intercepts the real browser-originated response and asserts its shape
 * against the canonical wire-contract corpus (issue #2238).
 *
 * A pristine seeded DB has zero printers, so the response is the empty
 * array documented by `PrintQueueContractTests.GetQueue_NoPrinters_
 * ReturnsEmptyCollection` (see `fixtures/wire-contracts/manifest.json`) —
 * exactly the corpus fixture asserted here.
 */
import { test, expect } from './fixtures/monolith-setup';
import { assertMatchesWireContractShape } from '../../src/test/wireContractShape';

const QUEUE_EMPTY_FIXTURE = 'api/print-queue/queue.empty-collection.json';

test.describe('monolith smoke: print queue', () => {
  test('GET /api/job-queue (timeline tab) shape matches the wire-contract corpus', async ({ page }) => {
    const responsePromise = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname === '/api/job-queue' &&
        response.request().method() === 'GET'
    );

    await page.goto('/printQueue/timeline');

    const response = await responsePromise;
    expect(response.status(), 'GET /api/job-queue must succeed').toBe(200);

    const payload = await response.json();
    expect(Array.isArray(payload), 'GET /api/job-queue must return an array').toBe(true);
    assertMatchesWireContractShape(payload, QUEUE_EMPTY_FIXTURE);
  });
});
