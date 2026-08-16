import type { APIRequestContext, Locator, Page } from '@playwright/test';
import {
  test as emulatorTest,
  expect,
  API_BASE_URL,
  dismissTourIfVisible,
  getStoredAuthToken,
  getPrinterCards,
} from './emulator-setup';

/**
 * Deterministic Moonraker emulator seed contract (see `e2e/README.md`,
 * "Moonraker emulator contract", and `docs/MOONRAKER_EMULATOR_VALIDATION.md`
 * for the full plan, and `src/moonraker-emulator/Farm.Moonraker.Emulator` for
 * the emulator implementation this module was aligned against). Printer-facing
 * specs in `e2e/emulator/` assert against this contract instead of accepting
 * soft/empty fallbacks.
 *
 * TOPOLOGY: "Moonraker Ready/Printing/Paused/Shutdown" are each served by
 * their OWN **root** emulator instance (one Moonraker + control API listener
 * per instance) — there is no shared multi-printer instance and no
 * path-prefixed `/printers/{id}/...` addressing. Each scenario's control URL
 * is independently configurable (env var or map below), defaulting to
 * `ready`=17125, `printing`=17126, `paused`=17127, `shutdown`=17128 on
 * localhost. "Moonraker Offline" has no backing instance at all: it is
 * seeded in the PrintFarmer DB pointing at `http://moonraker-offline:7125`,
 * a hostname nothing listens on, so it is a real connection failure rather
 * than a simulated one and has no control surface.
 *
 * CONTROL API (base path `/__emulator` at that scenario's own root URL —
 * never a path-prefixed printer id — only mapped when
 * `Emulator:EnableControlApi=true`):
 *
 *   POST /__emulator/reset
 *     Resets this instance to its complete deterministic fixture baseline,
 *     including printer state, virtual files, history, Spoolman, and MMU data.
 *
 *   POST /__emulator/time/advance   body { seconds }
 *     Advances this instance's deterministic virtual clock and re-computes
 *     print progress from elapsed virtual seconds — there is no direct
 *     "set progress to X%" call. Progress = elapsedSeconds / 600, clamped
 *     to [0, 1], so `advancePrintProgress` below converts a target percent
 *     into the equivalent `seconds` value.
 *
 *   GET /__emulator/printers
 *     Authoritative current emulator state for this instance's printer(s).
 *
 * SEEDED DATA — confirmed from `PrinterRegistry.cs`/`PrinterAggregate.cs`:
 *   - Exactly one live virtual file per printer: `benchy.gcode`.
 *     `calibration_cube.gcode` is NOT a live file — it only appears as the
 *     filename of the one pre-seeded history entry (status `completed`).
 *     This differs from an earlier assumption that both files were live;
 *     tests below assert the confirmed, real shape.
 *   - History starts with exactly one `completed` job. There is no
 *     pre-seeded `cancelled` entry — a cancelled entry only appears once a
 *     print is actually started and cancelled, so `printer-history.spec.ts`
 *     drives that action itself rather than assuming it pre-exists.
 *   - The `Printing` scenario's print progress starts at exactly 0% after a
 *     reset (deterministic; `TimeScale` defaults to 0 so nothing auto-ticks)
 *     — "stable" in the task's seed contract means "does not drift on its
 *     own", not "non-zero".
 *
 * If the backend/emulator lane changes any of the above, update this module
 * (and `e2e/README.md`) to match — do not loosen the calling tests back to
 * soft fallbacks, and do not hardcode path-prefixed printer-id controls.
 */

/** Every seeded scenario that has its own backing root emulator instance + control API. */
export type MoonrakerControllableScenario = 'ready' | 'printing' | 'paused' | 'shutdown';

/** MMU/toolhead protocols exposed by the emulator's deterministic control surface. */
export type MoonrakerMmuMode = 'None' | 'HappyHare' | 'Afc' | 'Qidibox' | 'SnapmakerU1';

/**
 * Per-scenario control base URL, fully configurable via one env var per
 * scenario (or by editing this map) since each scenario is an independent
 * root emulator instance, not a path addressed off a shared host. Falls
 * back to `http://127.0.0.1:<default port>` when no override is set.
 */
const DEFAULT_EMULATOR_PORTS: Record<MoonrakerControllableScenario, number> = {
  ready: 17125,
  printing: 17126,
  paused: 17127,
  shutdown: 17128,
};

const EMULATOR_URL_ENV_VARS: Record<MoonrakerControllableScenario, string> = {
  ready: 'MOONRAKER_EMULATOR_URL_READY',
  printing: 'MOONRAKER_EMULATOR_URL_PRINTING',
  paused: 'MOONRAKER_EMULATOR_URL_PAUSED',
  shutdown: 'MOONRAKER_EMULATOR_URL_SHUTDOWN',
};

const MOONRAKER_EMULATOR_HOST = process.env.MOONRAKER_EMULATOR_HOST || '127.0.0.1';

/** Base URL for one seeded scenario's dedicated root emulator instance. */
export function getMoonrakerEmulatorUrl(scenario: MoonrakerControllableScenario): string {
  const override = process.env[EMULATOR_URL_ENV_VARS[scenario]];
  if (override) {
    return override.replace(/\/$/, '');
  }
  return `http://${MOONRAKER_EMULATOR_HOST}:${DEFAULT_EMULATOR_PORTS[scenario]}`;
}

export const MOONRAKER_BACKEND_LABEL = 'Moonraker';

/** Exact, deterministic printer names the emulator seed contract guarantees. */
export const MOONRAKER_PRINTERS = {
  ready: 'Moonraker Ready',
  printing: 'Moonraker Printing',
  paused: 'Moonraker Paused',
  shutdown: 'Moonraker Shutdown',
  offline: 'Moonraker Offline',
} as const;

export type MoonrakerScenario = keyof typeof MOONRAKER_PRINTERS;

export const ALL_MOONRAKER_PRINTER_NAMES: readonly string[] = Object.values(MOONRAKER_PRINTERS);

/**
 * Exact, deterministic file names the emulator seed contract guarantees.
 * `benchy` is a live virtual file on every scenario printer. `calibrationCube`
 * is confirmed to appear only as a history entry's filename, not a live
 * file — see the module doc comment above.
 */
export const MOONRAKER_FILES = {
  benchy: 'benchy.gcode',
  calibrationCube: 'calibration_cube.gcode',
} as const;

export const ALL_MOONRAKER_FILE_NAMES: readonly string[] = Object.values(MOONRAKER_FILES);

interface SeededPrinterSummary {
  id: string;
  name: string;
  backend?: string;
  isOnline?: boolean;
  state?: string | null;
}

// ---------------------------------------------------------------------------
// Fixture extension: verify the deterministic seed before any test runs.
// ---------------------------------------------------------------------------

type MoonrakerFixtures = {
  /**
   * Verifies (via the real PrintFarmer API, not the emulator directly) that
   * every printer in `MOONRAKER_PRINTERS` exists with `backend === "Moonraker"`
   * before the test body runs. Fails loudly and specifically — naming the
   * missing printer — rather than letting a later, unrelated UI assertion
   * fail with a confusing message.
   */
  moonrakerSeedReady: Map<string, SeededPrinterSummary>;
};

export const test = emulatorTest.extend<MoonrakerFixtures>({
  moonrakerSeedReady: [async ({ page, emulatorReady }, use) => {
    void emulatorReady;

    const token = await getStoredAuthToken(page);
    expect(token, 'Auth token missing after emulatorReady fixture ran').toBeTruthy();

    const response = await page.request.get(`${API_BASE_URL}/api/printers`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(
      response.ok(),
      `Failed to fetch ${API_BASE_URL}/api/printers while verifying the Moonraker seed (status ${response.status()})`
    ).toBeTruthy();

    const printers = (await response.json()) as SeededPrinterSummary[];
    const byName = new Map(printers.map((p) => [p.name, p]));

    for (const name of ALL_MOONRAKER_PRINTER_NAMES) {
      const printer = byName.get(name);
      expect(
        printer,
        `Expected seeded Moonraker printer "${name}" was not returned by GET /api/printers. ` +
          'The Moonraker emulator seeding step must create it (backend=Moonraker) before printer-facing ' +
          'E2E specs can run — see e2e/README.md "Moonraker emulator contract".'
      ).toBeTruthy();
      expect(
        printer!.backend,
        `Seeded printer "${name}" must be backed by the real Moonraker backend, got "${printer!.backend}"`
      ).toBe(MOONRAKER_BACKEND_LABEL);
    }

    await use(byName);
  }, { auto: true }],
});

// ---------------------------------------------------------------------------
// Control-API client (emulator-only, deterministic scenario control)
// ---------------------------------------------------------------------------

/** The virtual seconds-to-percent ratio used by `PrinterAggregate.Progress()` (`SimulatedPrintTotalSeconds`). */
const SIMULATED_PRINT_TOTAL_SECONDS = 600;

export interface MoonrakerControlClient {
  /** Reset one seeded printer scenario back to its complete deterministic fixture baseline. */
  reset(scenario: MoonrakerControllableScenario): Promise<void>;
  /** Reset every controllable seeded printer scenario. */
  resetAll(): Promise<void>;
  /**
   * Deterministically set a printer's active job progress by advancing its
   * virtual clock by the equivalent number of seconds — there is no direct
   * "set progress" call, only virtual time advancement.
   */
  advancePrintProgress(scenario: MoonrakerControllableScenario, percent: number): Promise<void>;
  /** Fetch the emulator's authoritative current state for every printer in this instance's registry. */
  getPrinters(scenario: MoonrakerControllableScenario): Promise<Array<Record<string, unknown>>>;
  /** Select the deterministic MMU/toolhead wire protocol exposed by one emulator instance. */
  setMmuMode(scenario: MoonrakerControllableScenario, mode: MoonrakerMmuMode): Promise<void>;
}

/**
 * Build a client for the Moonraker emulator's control API using Playwright's
 * built-in `request` fixture (a standalone `APIRequestContext`, independent
 * of the browser `page`). Each seeded scenario is served by its own root
 * emulator instance/URL (see the module doc comment above — never a
 * path-prefixed printer id), so every method takes an explicit `scenario`
 * to pick the right base URL. Every method fails the test explicitly — with
 * the exact missing route and status code — instead of silently no-op'ing
 * when the control surface is unavailable, per the "must fail, not skip"
 * requirement for deterministic job-lifecycle coverage.
 */
export function createMoonrakerControl(request: APIRequestContext): MoonrakerControlClient {
  const controllableScenarios: MoonrakerControllableScenario[] = ['ready', 'printing', 'paused', 'shutdown'];

  return {
    async reset(scenario) {
      // Root-level control call — each scenario is its own root emulator
      // instance, so there is no `/printers/{id}/...` path to address.
      const url = `${getMoonrakerEmulatorUrl(scenario)}/__emulator/reset`;
      const res = await request.post(url);
      expect(
        res.ok(),
        `Moonraker emulator control API reset failed for "${scenario}" at ${url} (status ${res.status()}). ` +
          'Confirm the emulator instance for this scenario is running with Emulator:EnableControlApi=true.'
      ).toBeTruthy();
    },
    async resetAll() {
      for (const scenario of controllableScenarios) {
        await this.reset(scenario);
      }
    },
    async advancePrintProgress(scenario, percent) {
      const seconds = (Math.max(0, Math.min(100, percent)) / 100) * SIMULATED_PRINT_TOTAL_SECONDS;
      const url = `${getMoonrakerEmulatorUrl(scenario)}/__emulator/time/advance`;
      const res = await request.post(url, { data: { seconds } });
      expect(
        res.ok(),
        `Moonraker emulator control API time/advance failed for "${scenario}" at ${url} ` +
          `(status ${res.status()}). This deterministic control endpoint is required for hard job-lifecycle assertions.`
      ).toBeTruthy();
    },
    async getPrinters(scenario) {
      const url = `${getMoonrakerEmulatorUrl(scenario)}/__emulator/printers`;
      const res = await request.get(url);
      expect(
        res.ok(),
        `Moonraker emulator control API printers query failed at ${url} (status ${res.status()})`
      ).toBeTruthy();
      return (await res.json()) as Array<Record<string, unknown>>;
    },
    async setMmuMode(scenario, mode) {
      const url = `${getMoonrakerEmulatorUrl(scenario)}/__emulator/printer/mmu`;
      const res = await request.post(url, { data: { mode } });
      expect(
        res.ok(),
        `Moonraker emulator MMU mode switch failed for "${scenario}" at ${url} ` +
          `(mode ${mode}, status ${res.status()})`
      ).toBeTruthy();
    },
  };
}


// ---------------------------------------------------------------------------
// Strict UI locator helpers (scoped — never page-wide "any button" fallbacks)
// ---------------------------------------------------------------------------

/** Locate a printer card by its exact, deterministic seeded name. */
export function getPrinterCardByName(page: Page, name: string): Locator {
  return getPrinterCards(page).filter({ hasText: name }).first();
}

/** Assert a card shows the exact status label text (e.g. "Idle", "Printing", "Offline"). */
export async function expectPrinterStatus(card: Locator, label: string): Promise<void> {
  await expect(card.getByText(label, { exact: true }).first()).toBeVisible({ timeout: 15_000 });
}

/** Return the detailed card that owns the printer's inline detail controls. */
export async function getInlinePrinterDetails(page: Page, name: string): Promise<Locator> {
  const card = getPrinterCardByName(page, name);
  await expect(card).toBeVisible({ timeout: 10_000 });
  await dismissTourIfVisible(page);
  await expect(card.getByRole('button', { name: 'Open details sidebar' })).toHaveCount(0);
  return card;
}

/** Select a printer view mode and wait for the page to render it. */
export async function setPrinterViewMode(
  page: Page,
  mode: 'detailed' | 'collapsed' | 'table'
): Promise<void> {
  await page.evaluate((viewMode) => localStorage.setItem('printerViewMode', viewMode), mode);
  await page.reload();
  await page.waitForLoadState('domcontentloaded');
  await dismissTourIfVisible(page);
}

/** Open a printer's detail sidebar from collapsed-card mode. */
export async function openCollapsedPrinterDetailsSidebar(page: Page, name: string): Promise<Locator> {
  await setPrinterViewMode(page, 'collapsed');
  const card = getPrinterCardByName(page, name);
  await expect(card).toBeVisible({ timeout: 10_000 });
  await card.getByRole('button', { name: 'Open details sidebar' }).click();

  const sidebar = page.getByRole('complementary', { name: `${name} details` });
  await expect(sidebar).toBeVisible({ timeout: 10_000 });
  return sidebar;
}

/** Open a printer's detail sidebar from table mode. */
export async function openTablePrinterDetailsSidebar(page: Page, name: string): Promise<Locator> {
  await setPrinterViewMode(page, 'table');
  const row = page.getByRole('row').filter({ hasText: name }).first();
  await expect(row).toBeVisible({ timeout: 10_000 });
  await row.getByRole('button', { name: `Open details for ${name}` }).click();

  const sidebar = page.getByRole('complementary', { name: `${name} details` });
  await expect(sidebar).toBeVisible({ timeout: 10_000 });
  return sidebar;
}

/** Open the printer files modal (dialog) for a card and return its locator. */
export async function openPrinterFiles(page: Page, name: string): Promise<Locator> {
  const card = getPrinterCardByName(page, name);
  await expect(card).toBeVisible({ timeout: 10_000 });
  await card.getByRole('button', { name: 'View printer files' }).click();

  const dialog = page.getByRole('dialog', { name: 'Printer Files' });
  await expect(dialog).toBeVisible({ timeout: 10_000 });
  return dialog;
}

/** Open the printer history modal (dialog) for a card and return its locator. */
export async function openPrinterHistory(page: Page, name: string): Promise<Locator> {
  const card = getPrinterCardByName(page, name);
  await expect(card).toBeVisible({ timeout: 10_000 });
  await card.getByRole('button', { name: 'View print history' }).click();

  const dialog = page.getByRole('dialog', { name: 'Print History' });
  await expect(dialog).toBeVisible({ timeout: 10_000 });
  return dialog;
}

/**
 * Locate a specific file row within the printer files dialog. Rows are the
 * only elements in that dialog carrying Tailwind's `group` utility class
 * (used to reveal hover actions), so `.group` scoped by the file name text
 * reliably targets one row instead of matching every ancestor container
 * that also happens to contain the file name in its subtree.
 */
export function getPrinterFileRow(filesDialog: Locator, fileName: string): Locator {
  return filesDialog.locator('div.group').filter({ hasText: fileName });
}

/** Get the progress bar locator (role="progressbar", named "Print progress") within a scope. */
export function getProgressBar(scope: Locator): Locator {
  return scope.getByRole('progressbar', { name: 'Print progress' });
}

/** Read the current `aria-valuenow` of a progress bar as a number. */
export async function getProgressValue(progressBar: Locator): Promise<number> {
  const value = await progressBar.getAttribute('aria-valuenow');
  return Number(value ?? '0');
}

/**
 * Upload a throwaway G-code file directly to a scenario printer's real
 * Moonraker file-upload endpoint (`POST /server/files/upload`), bypassing
 * the PrintFarmer UI (which has no upload control wired up — see
 * `e2e/README.md`, "Out of scope"). Used only as test *setup* — e.g. to
 * exercise the delete flow without touching the guaranteed `benchy.gcode`
 * seed file, which is never restored by the root `/__emulator/reset`.
 */
export async function uploadScratchGcodeFile(
  request: APIRequestContext,
  scenario: MoonrakerControllableScenario,
  fileName: string
): Promise<void> {
  const url = `${getMoonrakerEmulatorUrl(scenario)}/server/files/upload`;
  const res = await request.post(url, {
    multipart: {
      file: {
        name: fileName,
        mimeType: 'text/x-gcode',
        buffer: Buffer.from('; e2e scratch file\nG28\n'),
      },
      root: 'gcodes',
    },
  });
  expect(res.ok(), `Failed to upload scratch file "${fileName}" to ${url} (status ${res.status()})`).toBeTruthy();
}

/**
 * Delete a file directly via the real Moonraker file-delete endpoint,
 * bypassing the UI. Tests can use this for immediate cleanup within one
 * scenario; the root `/__emulator/reset` also restores the complete seeded
 * virtual filesystem before the next test.
 */
export async function deleteScratchGcodeFile(
  request: APIRequestContext,
  scenario: MoonrakerControllableScenario,
  fileName: string,
  allowMissing = false
): Promise<void> {
  const url = `${getMoonrakerEmulatorUrl(scenario)}/server/files/gcodes/${encodeURIComponent(fileName)}`;
  const res = await request.delete(url);
  if (allowMissing && res.status() === 404) {
    return;
  }
  expect(res.ok(), `Failed to delete scratch file "${fileName}" at ${url} (status ${res.status()})`).toBeTruthy();
}

export { expect, getPrinterCards };
