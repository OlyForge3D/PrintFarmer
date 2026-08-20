import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const TEST_DIR = dirname(fileURLToPath(import.meta.url));
const REACT_APP_ROOT = resolve(TEST_DIR, '../../../..');

// ---------------------------------------------------------------------------
// Issue #1754 source-level guard.
//
// Tabs.test.tsx exercises the shared `Tabs.List`/`Tab` behavior in isolation,
// but it renders its own standalone `<Tabs.List className="overflow-x-auto">`
// rather than mounting PrintQueueDashboardPage itself (which pulls in
// react-query, SignalR, and several modals — too heavy to mount just for a
// className check). That means nothing pins PrintQueueDashboardPage's own
// wiring: the shared component test would keep passing even if this page
// forgot the `overflow-x-auto` opt-in entirely, silently reintroducing the
// Dispatch Log clipping/horizontal-scroll bug the issue reported.
//
// Assert the source contract directly instead: the page's tab strip
// `<Tabs.List>` must carry `overflow-x-auto`, matching the existing
// per-consumer opt-in convention already used by MaintenanceDashboardPage.
// ---------------------------------------------------------------------------
const PAGE_SOURCE = resolve(
  REACT_APP_ROOT,
  'src/features/queue/pages/PrintQueueDashboardPage.tsx',
);

describe('PrintQueueDashboardPage tab strip overflow contract (issue #1754)', () => {
  it('opts the Print Queue tab strip into horizontal scrolling', () => {
    const source = readFileSync(PAGE_SOURCE, 'utf-8');

    // Find the <Tabs.List ...> tag that wraps the print-queue tab strip
    // (identified by containing the "dispatch-log" tab reported clipped in
    // the issue) and assert it declares overflow-x-auto.
    const tabsListMatch = source.match(/<Tabs\.List\b[^>]*>[\s\S]*?<\/Tabs\.List>/);
    expect(
      tabsListMatch,
      'expected a <Tabs.List>...</Tabs.List> block in PrintQueueDashboardPage.tsx',
    ).not.toBeNull();

    const tabsListBlock = tabsListMatch![0];
    expect(tabsListBlock).toContain('dispatch-log');

    const openingTagMatch = tabsListBlock.match(/<Tabs\.List\b[^>]*>/);
    expect(openingTagMatch).not.toBeNull();
    const openingTag = openingTagMatch![0];

    expect(openingTag).toMatch(/className\s*=\s*"[^"]*\boverflow-x-auto\b[^"]*"/);
  });
});
