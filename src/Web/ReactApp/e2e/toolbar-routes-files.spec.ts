import { expect, test, type Page } from "@playwright/test";
import {
  captureRoutedToolbarState,
  mountRoutedToolbarPage,
  toolbarRouteUser,
} from "./fixtures/toolbar-routed";
import { filesToolbarSeeds } from "./fixtures/toolbar-routes-files";

const UNIFIED_FILES_QUERY = "/api/3d-models/files/query";

async function openFilesState(
  page: Page,
  path: "/files" | "/files?source=printers",
) {
  // Keep the representative screenshots deterministic rather than inheriting
  // view preferences from a previous test or browser profile.
  await page.addInitScript(() => {
    localStorage.setItem("printfarmer-files-viewmode", "grid");
    localStorage.setItem("printfarmer-printer-files-viewmode", "grid");
  });
  const requests = await mountRoutedToolbarPage(
    page,
    path,
    filesToolbarSeeds(),
  );
  await expect(
    page.getByRole("main").getByRole("heading", { name: "Files", level: 1 }),
  ).toBeVisible();
  if (path === "/files") {
    await expect(
      page.getByRole("checkbox", { name: "Select adapter.stl" }),
    ).toBeVisible();
    await expect(
      page.getByRole("checkbox", { name: "Select bracket.gcode" }),
    ).toBeVisible();
  } else {
    await expect(
      page.getByRole("group", { name: "bracket.gcode", exact: true }),
    ).toBeVisible();
  }
  return requests;
}

function expectSafeFixtureTraffic(
  requests: Awaited<ReturnType<typeof mountRoutedToolbarPage>>,
) {
  // The route helper labels every non-GET/HEAD request as a write. The Files list
  // is a read-only POST by API contract; allow only that exact query endpoint.
  expect(
    requests.writes.filter(
      (write) => !write.endsWith(` ${UNIFIED_FILES_QUERY}`),
    ),
    "no printer or other state-changing request should be sent",
  ).toEqual([]);
  expect(
    requests.external,
    "external traffic must be blocked and absent",
  ).toEqual([]);
  expect(
    requests.errors,
    "the real routed page must mount without browser exceptions",
  ).toEqual([]);
}

async function routeQueryPayloads(page: Page) {
  const payloads: Array<Record<string, unknown>> = [];
  page.on("request", (request) => {
    if (
      request.method() === "POST" &&
      new URL(request.url()).pathname === UNIFIED_FILES_QUERY
    ) {
      payloads.push(request.postDataJSON() as Record<string, unknown>);
    }
  });
  return payloads;
}

test.describe("actual Files routes: local library", () => {
  test("keeps file creation controls hidden for a user without create permissions", async ({
    page,
  }) => {
    const readOnlyUser = { ...toolbarRouteUser, roles: [], permissions: [] };
    const requests = await mountRoutedToolbarPage(page, "/files", {
      ...filesToolbarSeeds(),
      "/api/auth/me": readOnlyUser,
    });
    await expect(
      page.getByRole("main").getByRole("heading", { name: "Files", level: 1 }),
    ).toBeVisible();
    await expect(
      page.getByRole("checkbox", { name: "Select adapter.stl" }),
    ).toBeVisible();
    await expect(page.getByRole("button", { name: "Printables" })).toHaveCount(
      0,
    );
    await expect(
      page.getByRole("button", { name: "Upload Model" }),
    ).toHaveCount(0);
    await expect(
      page.getByRole("button", { name: "Upload G-Code" }),
    ).toHaveCount(0);
    expect(requests.unhandled).toEqual([]);
    expectSafeFixtureTraffic(requests);
  });

  test("captures desktop routed state and preserves controls, command placement, filter/sort, and view preference", async ({
    page,
  }, testInfo) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    const requests = await openFilesState(page, "/files");
    const payloads = await routeQueryPayloads(page);
    const toolbar = page.getByRole("group", { name: "File controls" });
    const header = page.locator("main .pf-header-actions");
    await expect(
      header.getByRole("button", { name: "Printables" }),
    ).toBeVisible();
    await expect(
      header.getByRole("button", { name: "Upload Model" }),
    ).toBeVisible();
    await expect(
      header.getByRole("button", { name: "Upload G-Code" }),
    ).toBeVisible();
    await expect(
      header.locator('button[data-pf-variant="primary"]'),
    ).toHaveCount(0);
    await expect(
      toolbar.getByRole("searchbox", { name: "Search" }),
    ).toBeVisible();
    await expect(toolbar.getByRole("combobox", { name: "Type" })).toBeVisible();
    await expect(
      toolbar.getByRole("combobox", { name: "Sort by" }),
    ).toBeVisible();
    await expect(
      toolbar.getByRole("button", { name: "Sort direction: descending" }),
    ).toBeVisible();
    await expect(
      toolbar.getByRole("button", { name: "Folders" }),
    ).toBeVisible();
    await expect(
      toolbar.getByRole("group", { name: "File view mode" }),
    ).toBeVisible();
    expect(
      await page.locator("main").evaluate((main) => {
        const actions = main.querySelector(".pf-header-actions");
        const toolbarElement = main.querySelector(
          '[aria-label="File controls"].pf-toolbar',
        );
        return (
          !!actions &&
          !!toolbarElement &&
          Boolean(
            actions.compareDocumentPosition(toolbarElement) &
            Node.DOCUMENT_POSITION_FOLLOWING,
          )
        );
      }),
    ).toBe(true);
    await captureRoutedToolbarState(page, testInfo, "files-local-desktop");

    await toolbar
      .getByRole("combobox", { name: "Type" })
      .selectOption("models");
    await expect.poll(() => payloads.at(-1)?.filter).toBe("models");
    await toolbar
      .getByRole("combobox", { name: "Sort by" })
      .selectOption("name");
    await expect.poll(() => payloads.at(-1)?.sortBy).toBe("name");
    // Choosing a new sort key resets its direction to ascending.
    await toolbar
      .getByRole("button", { name: "Sort direction: ascending" })
      .click();
    await expect.poll(() => payloads.at(-1)?.sortOrder).toBe("desc");
    await expect(
      toolbar.getByRole("button", { name: "Sort direction: descending" }),
    ).toBeVisible();
    await toolbar.getByRole("button", { name: "Explorer view" }).click();
    await expect(
      toolbar.getByRole("button", { name: "Explorer view" }),
    ).toHaveAttribute("aria-pressed", "true");
    await expect
      .poll(() =>
        page.evaluate(() => localStorage.getItem("printfarmer-files-viewmode")),
      )
      .toBe("explorer");

    await page.getByRole("checkbox", { name: "Select adapter.stl" }).check();
    const bulk = page.getByRole("group", { name: "Selected file actions" });
    await expect(bulk).toBeVisible();
    expect(
      await toolbar.evaluate((element) =>
        element.nextElementSibling?.getAttribute("aria-label"),
      ),
    ).toBe("Selected file actions");
    await expect(
      bulk.getByRole("button", { name: /Tag Models/ }),
    ).toBeVisible();
    await expect(bulk.getByRole("button", { name: /Delete/ })).toBeVisible();
    expect(await bulk.locator('button[data-pf-variant="danger"]').count()).toBe(
      0,
    );
    await bulk.getByRole("button", { name: /^Delete \(/ }).click();
    await expect(
      page.getByRole("heading", { name: "Delete Selected Files" }),
    ).toBeVisible();
    await page.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(
      page.getByRole("heading", { name: "Delete Selected Files" }),
    ).toHaveCount(0);

    expect(
      requests.unhandled,
      "all page and layout API reads must have explicit fixtures",
    ).toEqual([]);
    expectSafeFixtureTraffic(requests);
  });

  test("touch viewport retains keyboard-operable controls and folder behavior", async ({
    page,
  }, testInfo) => {
    await page.setViewportSize({ width: 390, height: 844 });
    const requests = await openFilesState(page, "/files");
    const toolbar = page.getByRole("group", { name: "File controls" });
    const search = toolbar.getByRole("searchbox", { name: "Search" });
    await captureRoutedToolbarState(page, testInfo, "files-local-mobile");
    const mdHeight = await search.evaluate(
      () =>
        Number.parseFloat(
          getComputedStyle(document.documentElement).getPropertyValue(
            "--pf-control-h-md",
          ),
        ) * 16,
    );
    expect(await search.boundingBox().then((box) => box?.height)).toBe(
      mdHeight,
    );

    await search.fill("adapter");
    await search.focus();
    await page.keyboard.press("Tab");
    await expect(
      toolbar.getByRole("button", { name: "Clear search" }),
    ).toBeFocused();
    const folders = toolbar.getByRole("button", { name: "Folders" });
    await folders.focus();
    await page.keyboard.press("Enter");
    const panel = page.locator(
      `#${await folders.getAttribute("aria-controls")}`,
    );
    await expect(panel.getByRole("combobox", { name: "Folder" })).toBeVisible();
    await panel
      .getByRole("combobox", { name: "Folder" })
      .selectOption("/Parts");
    await expect(
      panel
        .getByRole("navigation", { name: "Folder path" })
        .getByRole("button", { name: "Root" }),
    ).toBeVisible();
    await expect
      .poll(() =>
        page.evaluate(
          () => document.documentElement.scrollWidth <= window.innerWidth,
        ),
      )
      .toBe(true);

    expect(requests.unhandled).toEqual([]);
    expectSafeFixtureTraffic(requests);
  });
});

test.describe("actual Files routes: on printers", () => {
  test("captures populated desktop catalog and protects printer commands", async ({
    page,
  }, testInfo) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    const requests = await openFilesState(page, "/files?source=printers");
    const toolbar = page.getByRole("group", { name: "Printer file controls" });
    await expect(
      toolbar.getByRole("searchbox", { name: "Search" }),
    ).toBeVisible();
    await expect(
      toolbar.getByRole("combobox", { name: "Printer" }),
    ).toBeVisible();
    await expect(
      toolbar.getByRole("button", { name: "Explorer view" }),
    ).toBeVisible();
    await expect(
      toolbar.getByRole("button", { name: "Grid view" }),
    ).toBeVisible();
    const refresh = toolbar.getByRole("button", {
      name: "Force refresh all online printers",
    });
    await expect(refresh).toBeEnabled();
    await expect(
      page.getByRole("group", { name: "bracket.gcode", exact: true }),
    ).toBeVisible();
    await expect(
      toolbar.getByRole("checkbox", { name: "Select all files on this page" }),
    ).toBeVisible();
    await captureRoutedToolbarState(page, testInfo, "files-printers-desktop");

    // View and filters are presentation/read controls; do not activate any
    // refresh, copy, queue, download, or delete command in a routed test.
    await toolbar
      .getByRole("combobox", { name: "Printer" })
      .selectOption("toolbar-printer-1");
    await expect(
      toolbar.getByRole("combobox", { name: "Printer" }),
    ).toHaveValue("toolbar-printer-1");
    await toolbar.getByRole("button", { name: "Explorer view" }).click();
    await expect(
      toolbar.getByRole("button", { name: "Explorer view" }),
    ).toHaveAttribute("aria-pressed", "true");
    await expect
      .poll(() =>
        page.evaluate(() =>
          localStorage.getItem("printfarmer-printer-files-viewmode"),
        ),
      )
      .toBe("explorer");
    await expect(refresh).toBeVisible();
    expect(
      requests.unhandled,
      "all page and layout API reads must have explicit fixtures",
    ).toEqual([]);
    expectSafeFixtureTraffic(requests);
  });

  test("touch viewport keeps all catalog controls visible and keyboard reachable", async ({
    page,
  }, testInfo) => {
    await page.setViewportSize({ width: 390, height: 844 });
    const requests = await openFilesState(page, "/files?source=printers");
    const toolbar = page.getByRole("group", { name: "Printer file controls" });
    const search = toolbar.getByRole("searchbox", { name: "Search" });
    await captureRoutedToolbarState(page, testInfo, "files-printers-mobile");
    const mdHeight = await search.evaluate(
      () =>
        Number.parseFloat(
          getComputedStyle(document.documentElement).getPropertyValue(
            "--pf-control-h-md",
          ),
        ) * 16,
    );
    expect(await search.boundingBox().then((box) => box?.height)).toBe(
      mdHeight,
    );
    await search.fill("bracket");
    await search.focus();
    await page.keyboard.press("Tab");
    await expect(
      toolbar.getByRole("button", { name: "Clear search" }),
    ).toBeFocused();
    await expect(
      toolbar.getByRole("combobox", { name: "Printer" }),
    ).toBeVisible();
    await expect(
      toolbar.getByRole("button", {
        name: "Force refresh all online printers",
      }),
    ).toBeVisible();
    await expect
      .poll(() =>
        page.evaluate(
          () => document.documentElement.scrollWidth <= window.innerWidth,
        ),
      )
      .toBe(true);
    expect(requests.unhandled).toEqual([]);
    expectSafeFixtureTraffic(requests);
  });
});
