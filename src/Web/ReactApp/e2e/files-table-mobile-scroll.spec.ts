import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';

// Regression test for #1712: at mobile widths, the files table (~674px wide,
// 5 columns: Name/Type/Size/Uploaded/Details, plus an Actions column) is wider
// than its container (~348-370px). FilesPage only gives FileBrowser a
// `min-h-[65vh]` ancestor (a minimum, not a fixed/viewport height), so with
// many rows the table's `overflow-x-auto` region grows unbounded and its
// horizontal scrollbar ends up wherever the last row happens to land -
// arbitrarily far down the page and effectively unreachable. This harness
// deliberately reproduces that same "no fixed height ancestor" shape (unlike
// a harness that mounts into a fixed-height div, which would hide the bug)
// and uses enough rows to make the table taller than the viewport, matching
// real-world file lists.
async function mountFilesTableHarness(page: Page): Promise<void> {
  await page.goto('/');
  await page.evaluate(() => {
    // Mirrors FilesPage's actual ancestor chain around <FileBrowser>: only a
    // `min-h-*` is set, never a fixed/viewport height.
    document.body.innerHTML =
      '<div class="min-h-[65vh] min-w-0 overflow-hidden"><div id="files-table-root"></div></div>';
  });
  await page.addStyleTag({
    content: '* { transition: none !important; animation: none !important; }',
  });
  await page.addScriptTag({
    type: 'module',
    content: `
      import React from '/node_modules/.vite/deps/react.js';
      import ReactDom from '/node_modules/.vite/deps/react-dom_client.js';
      import { ExplorerView } from '/src/features/fileBrowser/components/ExplorerView.tsx';

      const noop = () => {};
      const folders = [{ path: '/', name: 'Root', children: [] }];
      // Enough rows that the table is taller than any mobile viewport under
      // test, matching real-world file lists (the bug only manifests once
      // the scroll region's content height exceeds the viewport).
      const files = Array.from({ length: 30 }, (_, i) => ({
        id: 'file-' + i,
        fileName: 'a-fairly-long-model-file-name-' + i + '.gcode',
        type: 'gcode',
        size: 1234567,
        uploadedAt: '2026-01-01T00:00:00.000Z',
      }));
      const columns = [
        { key: 'fileName', label: 'Name', sortable: true },
        { key: 'type', label: 'Type', sortable: true },
        { key: 'size', label: 'Size', sortable: true },
        { key: 'uploadedAt', label: 'Uploaded', sortable: true },
        {
          key: 'details',
          label: 'Details',
          sortable: false,
          render: () => React.createElement('span', null, '\u2014'),
        },
      ];
      const props = {
        folders,
        files,
        selectedIds: [],
        onToggle: noop,
        onSelectAll: noop,
        onNavigate: noop,
        currentPath: '/',
        renderActions: (file) => React.createElement(
          'button',
          { type: 'button', 'data-testid': 'row-action-' + file.id },
          'Open'
        ),
        sortBy: 'fileName',
        sortOrder: 'asc',
        onSort: noop,
        page: 1,
        totalPages: 1,
        onPageChange: noop,
        columns,
      };

      ReactDom.createRoot(document.getElementById('files-table-root')).render(
        React.createElement(ExplorerView, props)
      );
      requestAnimationFrame(() => requestAnimationFrame(() => {
        window.__filesTableHarnessReady = true;
      }));
    `,
  });
  await page.waitForFunction(() => {
    return (window as Window & { __filesTableHarnessReady?: boolean })
      .__filesTableHarnessReady === true;
  });
}

const MOBILE_VIEWPORTS = [
  { width: 390, height: 844 },
  { width: 412, height: 915 },
];

test.describe('Files table mobile horizontal scroll (#1712)', () => {
  for (const viewport of MOBILE_VIEWPORTS) {
    test(`Details and Actions columns are reachable at ${viewport.width}x${viewport.height}`, async ({
      page,
    }) => {
      test.setTimeout(60_000);
      await page.setViewportSize(viewport);
      await mountFilesTableHarness(page);

      const scrollRegion = page.getByRole('table', { name: 'Files list' }).locator('..');

      // The scroll region must actually overflow horizontally - otherwise this
      // test would pass vacuously.
      const { scrollWidth, clientWidth } = await scrollRegion.evaluate((el) => ({
        scrollWidth: el.scrollWidth,
        clientWidth: el.clientWidth,
      }));
      expect(scrollWidth, 'table must overflow its scroll container').toBeGreaterThan(clientWidth);

      // Root cause of #1712: without a bounded height, the scroll region grows
      // with every row, pushing its horizontal scrollbar arbitrarily far down
      // the (very tall) page. Assert the region stays within a bounded,
      // reachable area relative to the viewport regardless of row count, and
      // that its scrollbar is within the initial viewport (no page scroll
      // needed to discover it).
      const box = await scrollRegion.boundingBox();
      expect(box, 'scroll region must be visible').not.toBeNull();
      expect(
        box!.height,
        'scroll region height must be bounded, not grow unbounded with row count',
      ).toBeLessThanOrEqual(viewport.height * 0.75);
      expect(
        box!.y + box!.height,
        'scroll region (and its scrollbar) must be reachable without scrolling the page',
      ).toBeLessThanOrEqual(viewport.height);

      // The Details header and a row's Actions button are clipped off the
      // right edge before scrolling...
      await expect(page.getByRole('columnheader', { name: 'Details' })).not.toBeInViewport();
      const firstRowAction = page.getByTestId('row-action-file-0');
      await expect(firstRowAction).not.toBeInViewport();

      // ...but scrolling the table region horizontally must reveal both,
      // proving there is a working scroll path to Details and Actions.
      await scrollRegion.evaluate((el) => {
        el.scrollLeft = el.scrollWidth;
      });
      await expect(page.getByRole('columnheader', { name: 'Details' })).toBeInViewport();
      await expect(firstRowAction).toBeInViewport();
    });
  }
});
