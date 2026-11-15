import { expect, test, type Page } from "@playwright/test";
import {
  assertCollapsedFiltersDisclosures,
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

for (const width of [390, 639, 641, 1440, "below64rem", "above64rem"] as const) {
  test(`paired Files toolbar rows preserve geometry and keyboard access at ${width}px`, async ({ page }, testInfo) => {
    test.setTimeout(90_000);
    await page.setViewportSize({ width: typeof width === "number" ? width : 1440, height: 1000 });
    const measurements = [];
    for (const source of ["local", "printers"] as const) {
      const payloads = await routeQueryPayloads(page);
      const requests = await openFilesState(page, source === "local" ? "/files" : "/files?source=printers");
      const toolbar = page.getByRole("group", { name: source === "local" ? "File controls" : "Printer file controls", exact: true });
      const row = toolbar.locator(":scope > .pf-toolbar__controls");
      if (typeof width === "string") {
        const threshold = await toolbar.evaluate(() => 64 * Number.parseFloat(getComputedStyle(document.documentElement).fontSize));
        const target = threshold + (width === "below64rem" ? -1 : 1);
        // Sidebar, main padding and tab-panel borders consume viewport space.
        // Resize from their measured total rather than treating viewport width as container width.
        for (let attempt = 0; attempt < 4; attempt++) {
          const containerWidth = await toolbar.evaluate(element => {
            const style = getComputedStyle(element);
            return element.getBoundingClientRect().width - Number.parseFloat(style.paddingLeft) -
              Number.parseFloat(style.paddingRight) - Number.parseFloat(style.borderLeftWidth) -
              Number.parseFloat(style.borderRightWidth);
          });
          await page.setViewportSize({ width: Math.round(page.viewportSize()!.width + target - containerWidth), height: 1000 });
        }
        const measured = await toolbar.evaluate(element => {
          const style = getComputedStyle(element);
          return element.getBoundingClientRect().width - Number.parseFloat(style.paddingLeft) -
            Number.parseFloat(style.paddingRight) - Number.parseFloat(style.borderLeftWidth) -
            Number.parseFloat(style.borderRightWidth);
        });
        expect(measured, "measured toolbar content-box brackets its 64rem container query").toBeCloseTo(target, 1);
      }
      await expect(toolbar).toHaveClass(/pf-toolbar--files/);
      await expect(page.getByRole("group", { name: "Selected file actions", exact: true })).toHaveCount(0);
      const measureRow = () => row.evaluate(element => {
        const bounds = element.getBoundingClientRect();
        const inputs = Array.from(element.querySelectorAll<HTMLElement>('input:not([type="checkbox"]), select, button'));
        const controls = inputs.map(input => {
          const rect = input.getBoundingClientRect();
          return { name: input.getAttribute("aria-label") || input.id || input.textContent, height: rect.height, bottom: rect.bottom };
        });
        const search = element.querySelector<HTMLInputElement>('input[type="search"]')!;
        return {
          width: bounds.width, clientWidth: element.clientWidth, scrollWidth: element.scrollWidth,
          overflowX: getComputedStyle(element).overflowX,
          searchWidth: search.getBoundingClientRect().width, controls,
        };
      });
      const geometry = await measureRow();
      expect(geometry.overflowX).toBe("auto");
      expect(geometry.searchWidth).toBeGreaterThanOrEqual(192);
      for (const control of geometry.controls) {
        expect(control.height, `${source}: ${control.name} keeps its full size`).toBeGreaterThanOrEqual(36);
        expect(Math.abs(control.bottom - geometry.controls[0].bottom), `${source}: controls stay in one row`).toBeLessThanOrEqual(0.5);
      }
      if (width === 390 && source === "local") expect(geometry.scrollWidth).toBeGreaterThan(geometry.clientWidth);
      if (width === 1440) expect(geometry.scrollWidth).toBe(geometry.clientWidth);
      const checkKeyboardAccess = async () => {
      const tabbables = row.locator('input:not(:disabled):not([type="hidden"]), select:not(:disabled), button:not(:disabled)');
      await tabbables.first().focus();
      for (let index = 0; index < await tabbables.count(); index++) {
        const control = tabbables.nth(index);
        await expect(control).toBeFocused();
        const visibility = await control.evaluate(element => {
          const row = element.closest(".pf-toolbar__controls")!.getBoundingClientRect();
          const box = element.getBoundingClientRect();
          return {
            name: element.getAttribute("aria-label") || element.id || element.textContent,
            row: { left: row.left, right: row.right, top: row.top, bottom: row.bottom },
            box: { left: box.left, right: box.right, top: box.top, bottom: box.bottom },
            visible: box.left >= row.left - 0.5 && box.right <= row.right + 0.5 && box.top >= row.top - 0.5 && box.bottom <= row.bottom + 0.5,
          };
        });
        expect(visibility.visible, `Tab automatically reveals each complete control: ${JSON.stringify(visibility)}`).toBe(true);
        if (index + 1 < await tabbables.count()) await page.keyboard.press("Tab");
      }
      await row.evaluate(element => { element.scrollLeft = 0; });
      };
      await checkKeyboardAccess();
      const content = source === "local" ? page.locator('[data-tour="gcode-file-list"]')
        : page.getByRole("region", { name: "Files on printers", exact: true });
      const gap = (await content.boundingBox())!.y - ((await toolbar.boundingBox())!.y + (await toolbar.boundingBox())!.height);
      expect(gap, `${source}: one standard toolbar/content gap`).toBeCloseTo(16, 1);
      measurements.push({ source, testWidth: width, gap, ...geometry });
      await page.screenshot({ path: testInfo.outputPath(`${source}-${width}.png`), fullPage: true });
      const filters = toolbar.getByRole("button", { name: /^Filters/ });
      await filters.click();
      const panel = toolbar.getByRole("region", { name: "Filters", exact: true });
      await expect(panel).toBeVisible();
      expect(await panel.evaluate(element => element.closest(".pf-toolbar__controls") === null)).toBe(true);
      if (source === "local") {
        await toolbar.getByRole("combobox", { name: "Type" }).selectOption("models");
        await expect.poll(() => payloads.at(-1)?.filter).toBe("models");
        await filters.click();
        await toolbar.getByRole("combobox", { name: "Sort by" }).selectOption("name");
        await expect.poll(() => payloads.at(-1)?.sortBy).toBe("name");
        await page.getByRole("checkbox", { name: "Select adapter.stl" }).check();
        const actions = page.getByRole("group", { name: "Selected file actions", exact: true });
        await expect(actions).toBeVisible();
        expect(await actions.evaluate(element => element.closest(".pf-toolbar__controls") === null)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`local-selected-actions-${width}.png`), fullPage: true });
        await page.getByRole("checkbox", { name: "Select adapter.stl" }).uncheck();
        await expect(actions).toHaveCount(0);
      } else {
        await toolbar.getByRole("combobox", { name: "Printer" }).selectOption("toolbar-printer-1");
        await expect.poll(() => new URL(page.url()).searchParams.get("printer")).toBe("toolbar-printer-1");
        await filters.click();
      }
      await toolbar.getByRole("button", { name: "Explorer view", exact: true }).click();
      await expect(toolbar.getByRole("button", { name: "Explorer view", exact: true })).toHaveAttribute("aria-pressed", "true");
      await expect(filters).toHaveAttribute("aria-expanded", "false");
      await expect(page.getByRole("group", { name: "Selected file actions", exact: true })).toHaveCount(0);
      const table = source === "local" ? page.locator('[data-tour="gcode-file-list"] table')
        : page.getByRole("table", { name: "Files on printers", exact: true });
      await expect(table).toBeVisible();
      const tableGeometry = await measureRow();
      for (const control of tableGeometry.controls) {
        expect(control.height, `${source} Explorer: ${control.name} keeps its full size`).toBeGreaterThanOrEqual(36);
        expect(Math.abs(control.bottom - tableGeometry.controls[0].bottom), `${source} Explorer: one controls row`).toBeLessThanOrEqual(0.5);
      }
      expect(tableGeometry.searchWidth).toBeGreaterThanOrEqual(192);
      await checkKeyboardAccess();
      const toolbarBox = (await toolbar.boundingBox())!;
      const actualTableTop = (await table.boundingBox())!.y;
      const frame = source === "local"
        ? page.getByRole("region", { name: "Explorer view", exact: true })
        : table.locator("..");
      const frameBox = (await frame.boundingBox())!;
      const frameGap = frameBox.y - (toolbarBox.y + toolbarBox.height);
      const tableInset = actualTableTop - frameBox.y;
      expect(frameGap, `${source} Explorer: toolbar/table-frame gap`).toBeCloseTo(16, 1);
      const frameBorderInset = 1;
      expect(tableInset, `${source} Explorer: existing frame borders precede the actual table`).toBeCloseTo(frameBorderInset, 1);
      const actualTableGap = actualTableTop - (toolbarBox.y + toolbarBox.height);
      expect(actualTableGap, `${source} Explorer: actual rendered table top`).toBeCloseTo(16 + frameBorderInset, 1);
      measurements.push({ source, testWidth: width, mode: "explorer", viewportWidth: page.viewportSize()!.width,
        rootFontSize: await page.evaluate(() => getComputedStyle(document.documentElement).fontSize),
        frameGap, actualTableTop, actualTableGap, ...tableGeometry });
      await page.screenshot({ path: testInfo.outputPath(`${source}-explorer-${width}.png`), fullPage: true });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      expect(requests.unhandled).toEqual([]);
      expectSafeFixtureTraffic(requests);
    }
    const tableMeasurements = measurements.filter(measurement => "actualTableGap" in measurement);
    expect(tableMeasurements).toHaveLength(2);
    expect(tableMeasurements[0].actualTableGap, "both tabs place the actual table at the same gap").toBe(tableMeasurements[1].actualTableGap);
    await testInfo.attach("paired-toolbar-measurements", { body: JSON.stringify(measurements, null, 2), contentType: "application/json" });
  });
}

test.describe("actual Files routes: local library", () => {
  for (const { alias, filter } of [
    { alias: "models", filter: "models" },
    { alias: "gcode", filter: "gcode" },
  ] as const) {
    test(`normalizes /files/${alias} to the ${filter} library filter`, async ({
      page,
    }) => {
      await page.setViewportSize({ width: 1440, height: 900 });
      const payloads = await routeQueryPayloads(page);
      const requests = await mountRoutedToolbarPage(
        page,
        `/files/${alias}`,
        filesToolbarSeeds(),
      );
      await expect(
        page.getByRole("main").getByRole("heading", { name: "Files", level: 1 }),
      ).toBeVisible();
      await expect
        .poll(() => new URL(page.url()).searchParams.get("type"))
        .toBe(filter);
      const toolbar = page.getByRole("group", { name: "File controls" });
      const filtersButton = page.getByRole("button", { name: /^Filters/ });
      await expect(filtersButton).toHaveAttribute("aria-expanded", "false");
      await filtersButton.click();
      await expect(
        toolbar.getByRole("combobox", { name: "Type" }),
      ).toHaveValue(filter);
      await filtersButton.click();
      await expect(filtersButton).toHaveAttribute("aria-expanded", "false");
      await expect.poll(() => payloads.at(-1)?.filter).toBe(filter);
      await assertCollapsedFiltersDisclosures(
        page,
        `files-${filter}-legacy-route`,
        1,
      );

      expect(requests.unhandled).toEqual([]);
      expectSafeFixtureTraffic(requests);
    });
  }

  test("keeps nested search usable in the single compact toolbar row", async ({ page }) => {
    await page.setViewportSize({ width: 705, height: 900 });
    const requests = await openFilesState(page, "/files");
    const toolbar = page.getByRole("group", { name: "File controls", exact: true });
    const search = toolbar.getByRole("searchbox", { name: "Search", exact: true });
    const toolbarBounds = await toolbar.boundingBox();
    const searchGeometry = await search.evaluate((element) => {
      const toolbarElement = element.closest(".pf-toolbar__controls");
      const field = toolbarElement
        ? Array.from(toolbarElement.children).find((child) =>
            child.contains(element),
          )
        : undefined;
      const ancestors = [];
      for (
        let current: HTMLElement | null = element as HTMLElement;
        current && current !== document.body;
        current = current.parentElement
      ) {
        const style = getComputedStyle(current);
        const bounds = current.getBoundingClientRect();
        ancestors.push({
          element: current.tagName.toLowerCase(),
          className: current.className,
          width: bounds.width,
          minWidth: style.minWidth,
          flex: style.flex,
          flexBasis: style.flexBasis,
          overflow: style.overflow,
        });
      }
      return {
        fieldWidth: field?.getBoundingClientRect().width ?? 0,
        ancestors,
      };
    });
    expect(
      searchGeometry.fieldWidth,
      `search field width ${searchGeometry.fieldWidth}; toolbar width ${toolbarBounds!.width}; layout ancestors: ${JSON.stringify(searchGeometry.ancestors)}`,
    ).toBeGreaterThanOrEqual(192);
    expect(searchGeometry.fieldWidth).toBeLessThan(toolbarBounds!.width);
    await search.fill("adapter");
    await expect(search).toHaveValue("adapter");
    expect(await search.evaluate(element => element.scrollWidth <= element.clientWidth + 1))
      .toBe(true);
    expect(requests.unhandled).toEqual([]);
    expectSafeFixtureTraffic(requests);
  });

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
    await expect(toolbar.getByRole("combobox", { name: "Type" })).toBeHidden();
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
    await captureRoutedToolbarState(page, testInfo, "files-local-desktop", 1);

    await toolbar.getByRole("button", { name: "Filters", exact: true }).click();
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
    await captureRoutedToolbarState(page, testInfo, "files-local-mobile", 1);
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
    ).toBeHidden();
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
    await captureRoutedToolbarState(page, testInfo, "files-printers-desktop", 1);

    // View and filters are presentation/read controls; do not activate any
    // refresh, copy, queue, download, or delete command in a routed test.
    await toolbar.getByRole("button", { name: "Filters", exact: true }).click();
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
    await captureRoutedToolbarState(page, testInfo, "files-printers-mobile", 1);
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
    ).toBeHidden();
    await toolbar.getByRole("button", { name: "Filters", exact: true }).click();
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
