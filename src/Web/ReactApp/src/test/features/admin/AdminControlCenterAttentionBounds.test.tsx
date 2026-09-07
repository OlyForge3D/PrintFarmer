import React from 'react';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { AdminControlCenterPage } from '@/features/admin/pages/AdminControlCenterPage';
import { ADMIN_OVERVIEW_QUERY_KEY } from '@/features/admin/hooks/useAdminOverview';
import {
  ATTENTION_PREVIEW_LIMIT_DESKTOP,
  ATTENTION_PREVIEW_LIMIT_NARROW,
} from '@/features/admin/components';
import type { AdminOverviewDto, AttentionItemDto } from '@/types/adminOverview';

/**
 * Bounds regression suite for the `/admin` "Needs attention" region (#2517).
 *
 * The hub used to map *every* attention item into one unbounded list, so a farm
 * with a realistic alert volume saw the panel take over the console and push
 * system health and the operational tools below the fold. Nobody had ever
 * reproduced a *populated* panel, which is why the regression shipped, so this
 * file exercises the full range the issue calls out: 0, 1, 4, 25 and 50 items.
 *
 * jsdom does not lay out, so no assertion here can measure the rendered pixel
 * height the acceptance criteria state. What is measurable — and what actually
 * causes the overflow — is *how many rows are in the DOM* and whether the
 * expanded overflow is confined to a bounded scroll container. Those are the
 * proxies asserted below; the pixel ceilings live in the class names
 * (`ATTENTION_EXPANDED_MAX_HEIGHT_CLASS`) and are verified visually.
 */

// ── Mocks ────────────────────────────────────────────────────────────────────

vi.mock('@/services/api/httpClient', () => ({
  client: {
    get: vi.fn(),
  },
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: vi.fn(),
}));

vi.mock('@/common/components/PageTemplate', () => ({
  PageTemplate: ({
    title,
    subtitle,
    actions,
    children,
  }: {
    title: string;
    subtitle?: string;
    actions?: React.ReactNode;
    children: React.ReactNode;
  }) => (
    <div data-testid="page-template">
      <h1>{title}</h1>
      {subtitle && <p>{subtitle}</p>}
      {actions && <div data-testid="page-template-actions">{actions}</div>}
      {children}
    </div>
  ),
}));

import { client } from '@/services/api/httpClient';
import { useAuth } from '@/features/auth/hooks/useAuth';

const mockedApiGet = vi.mocked(client.get);
const mockedUseAuth = vi.mocked(useAuth);

// ── Fixtures ─────────────────────────────────────────────────────────────────

/**
 * Build a server-ordered attention feed: Errors first, then Warnings, then
 * Info — the exact shape `/api/admin/overview` returns. The panel must slice
 * this order, never re-sort it, so the fixture interleaves nothing.
 */
function makeAttention(count: number, errorCount = 0): AttentionItemDto[] {
  return Array.from({ length: count }, (_, index) => {
    const severity =
      index < errorCount ? 'Error' : index < errorCount + Math.ceil((count - errorCount) / 2)
        ? 'Warning'
        : 'Info';
    return {
      key: `attention-${index}`,
      severity,
      title: `Attention item ${index}`,
      detail: `Detail for attention item ${index}.`,
      actionLabel: 'Open Printers',
      actionRoute: '/printers',
    } satisfies AttentionItemDto;
  });
}

function makeOverview(attention: AttentionItemDto[]): AdminOverviewDto {
  return {
    checkedAt: '2026-07-25T17:04:00Z',
    overallStatus: attention.length > 0 ? 'Degraded' : 'Healthy',
    subsystems: [
      { key: 'api', name: 'API', status: 'Healthy', detail: 'Responding' },
      { key: 'database', name: 'Database', status: 'Healthy', detail: 'PostgreSQL' },
      { key: 'signalr', name: 'SignalR Hub', status: 'Healthy', detail: 'Hub accessible' },
      {
        key: 'backends',
        name: 'Printer Backends',
        status: attention.length > 0 ? 'Degraded' : 'Healthy',
        detail: '2 / 3 reachable',
      },
    ],
    attention,
  };
}

function farmAdminAccess() {
  return {
    isAuthenticated: true,
    isLoading: false,
    user: {
      id: 'user-1',
      email: 'admin@test.com',
      roles: ['farm_admin'],
      isActive: true,
    },
    hasRole: (role: string) => role === 'farm_admin',
    hasPermission: () => true,
    error: null,
    login: vi.fn(),
    loginWithPasskey: vi.fn(),
    register: vi.fn(),
    logout: vi.fn(),
  };
}

function renderHub() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } },
  });
  return {
    ...render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/admin']}>
          <AdminControlCenterPage />
        </MemoryRouter>
      </QueryClientProvider>,
    ),
    queryClient,
  };
}

/**
 * The global test polyfill answers every media query with `matches: false`, so
 * suites default to the desktop cap. Narrow cases opt in explicitly rather than
 * relying on a mutated global leaking between tests.
 */
function useNarrowViewport() {
  const original = window.matchMedia;
  window.matchMedia = ((query: string) => ({
    matches: query.includes('max-width'),
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn(),
  })) as unknown as typeof window.matchMedia;
  return () => {
    window.matchMedia = original;
  };
}

async function renderWithAttention(items: AttentionItemDto[]) {
  mockedApiGet.mockResolvedValue({ data: makeOverview(items) });
  const rendered = renderHub();
  await waitFor(() => {
    expect(screen.getByTestId('admin-hub-overall-status')).toBeInTheDocument();
  });
  return rendered;
}

/**
 * Refetch the way a *background* poll does — no click, so nothing moves DOM
 * focus. Driving this through the Refresh button instead would move focus onto
 * that button and mask whatever the panel does with focus of its own.
 */
async function backgroundRefresh(queryClient: QueryClient, items: AttentionItemDto[]) {
  mockedApiGet.mockResolvedValue({ data: makeOverview(items) });
  await act(async () => {
    await queryClient.invalidateQueries({ queryKey: ADMIN_OVERVIEW_QUERY_KEY });
  });
}

function visibleRowCount() {
  return screen.queryAllByTestId('admin-hub-attention-item').length;
}

// ── Suite ────────────────────────────────────────────────────────────────────

describe('Admin Control Center attention bounds (#2517)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockedUseAuth.mockReturnValue(farmAdminAccess() as ReturnType<typeof useAuth>);
  });

  describe('0 items', () => {
    it('renders a calm empty state with no list, no toggle and no scroll region', async () => {
      await renderWithAttention([]);

      expect(screen.queryByTestId('admin-hub-attention-panel')).not.toBeInTheDocument();
      expect(screen.queryByTestId('admin-hub-attention')).not.toBeInTheDocument();
      expect(screen.queryByTestId('admin-hub-attention-toggle')).not.toBeInTheDocument();
      expect(
        screen.getByText(/every subsystem health check is reporting healthy/i),
      ).toBeInTheDocument();
    });
  });

  describe('1 item', () => {
    it('renders the single item inline with no disclosure control', async () => {
      await renderWithAttention(makeAttention(1));

      expect(visibleRowCount()).toBe(1);
      expect(screen.queryByTestId('admin-hub-attention-toggle')).not.toBeInTheDocument();
      expect(screen.queryByTestId('admin-hub-attention-summary')).not.toBeInTheDocument();
      expect(
        screen.getByTestId('admin-hub-attention-region'),
      ).toHaveAttribute('data-attention-scrollable', 'false');
    });
  });

  describe('4 items — the first count that overflows the desktop cap', () => {
    it('previews only the cap and offers a labelled disclosure for the rest', async () => {
      await renderWithAttention(makeAttention(4));

      expect(visibleRowCount()).toBe(ATTENTION_PREVIEW_LIMIT_DESKTOP);

      const toggle = screen.getByTestId('admin-hub-attention-toggle');
      expect(toggle).toHaveTextContent('Show all 4');
      expect(toggle).toHaveAttribute('aria-expanded', 'false');
      expect(screen.getByTestId('admin-hub-attention-summary')).toHaveTextContent(
        'Showing 3 of 4 items',
      );
    });

    it('expands to every item and collapses back to the cap', async () => {
      const user = userEvent.setup();
      await renderWithAttention(makeAttention(4));

      await user.click(screen.getByTestId('admin-hub-attention-toggle'));
      expect(visibleRowCount()).toBe(4);

      const toggle = screen.getByTestId('admin-hub-attention-toggle');
      expect(toggle).toHaveTextContent('Show fewer');
      expect(toggle).toHaveAttribute('aria-expanded', 'true');
      // "Show fewer" alone is contextless when a screen reader user reaches it
      // out of sequence via a controls list; the visible text stays a prefix of
      // the accessible name so label-in-name still holds.
      expect(toggle).toHaveAccessibleName('Show fewer attention items');

      await user.click(toggle);
      expect(visibleRowCount()).toBe(ATTENTION_PREVIEW_LIMIT_DESKTOP);
    });
  });

  describe.each([25, 50])('%i items', (count) => {
    it('never renders more than the preview cap while collapsed', async () => {
      await renderWithAttention(makeAttention(count));

      // The regression this file exists for: without the bound, this is `count`.
      expect(visibleRowCount()).toBe(ATTENTION_PREVIEW_LIMIT_DESKTOP);
      expect(screen.getByTestId('admin-hub-attention-toggle')).toHaveTextContent(
        `Show all ${count}`,
      );
    });

    it('keeps every item reachable inside a bounded, labelled scroll region when expanded', async () => {
      const user = userEvent.setup();
      await renderWithAttention(makeAttention(count));

      await user.click(screen.getByTestId('admin-hub-attention-toggle'));

      expect(visibleRowCount()).toBe(count);

      const region = screen.getByTestId('admin-hub-attention-region');
      expect(region).toHaveAttribute('data-attention-scrollable', 'true');
      // Bounded height + its own scrollbar: the overflow is confined here
      // instead of growing the page, which is what pushed the hub off-screen.
      expect(region.className).toContain('overflow-y-auto');
      expect(region.className).toMatch(/max-h-\[min\(560px,70dvh\)\]/);

      // Focusable so a keyboard user can scroll it, and named so a screen
      // reader user knows what they landed in. `group` rather than `region`:
      // AdminSection already renders a named <section>, so a nested region
      // landmark would only pad the screen-reader landmark menu.
      expect(region).toHaveAttribute('tabindex', '0');
      expect(screen.getByRole('group', { name: `All ${count} attention items` })).toBe(region);
      expect(region).not.toHaveAttribute('role', 'region');
    });

    it('leaves the collapse control outside the scroll container so it cannot be scrolled away', async () => {
      const user = userEvent.setup();
      await renderWithAttention(makeAttention(count));

      await user.click(screen.getByTestId('admin-hub-attention-toggle'));

      const region = screen.getByTestId('admin-hub-attention-region');
      const toggle = screen.getByTestId('admin-hub-attention-toggle');
      expect(region.contains(toggle)).toBe(false);
      expect(toggle).toHaveAttribute('aria-controls', region.id);
    });

    it('preserves the server ranking rather than re-sorting the preview', async () => {
      const items = makeAttention(count, 5);
      await renderWithAttention(items);

      const renderedKeys = screen
        .getAllByTestId('admin-hub-attention-item')
        .map((row) => row.getAttribute('data-attention-key'));
      expect(renderedKeys).toEqual(
        items.slice(0, ATTENTION_PREVIEW_LIMIT_DESKTOP).map((item) => item.key),
      );
    });

    it('states the total and severity mix so collapsing never reads as resolved', async () => {
      const items = makeAttention(count, 5);
      await renderWithAttention(items);

      const summary = screen.getByTestId('admin-hub-attention-summary');
      expect(summary).toHaveTextContent(`of ${count} items`);
      expect(summary).toHaveTextContent('5 Errors');
    });

    it('explicitly announces Errors that the preview is hiding', async () => {
      const user = userEvent.setup();
      // 5 Errors, only 3 rows previewed → 2 Errors are off screen.
      await renderWithAttention(makeAttention(count, 5));

      const hidden = screen.getByTestId('admin-hub-attention-hidden-errors');
      expect(hidden).toHaveTextContent('2 Errors not shown');

      // Once everything is on screen the warning is no longer true, so it goes.
      await user.click(screen.getByTestId('admin-hub-attention-toggle'));
      expect(
        screen.queryByTestId('admin-hub-attention-hidden-errors'),
      ).not.toBeInTheDocument();
    });

    it('does not hide the rest of the hub behind the attention list', async () => {
      const user = userEvent.setup();
      await renderWithAttention(makeAttention(count));
      await user.click(screen.getByTestId('admin-hub-attention-toggle'));

      // Health and the operational tools must still be rendered siblings, not
      // casualties of an unbounded list.
      expect(screen.getByTestId('admin-hub-subsystems')).toBeInTheDocument();
      expect(screen.getByRole('heading', { name: /system health checks/i })).toBeInTheDocument();
    });
  });

  describe('keyboard and focus behaviour', () => {
    it('returns focus to the toggle when collapsing, so focus never falls to the document', async () => {
      const user = userEvent.setup();
      await renderWithAttention(makeAttention(25));

      const toggle = screen.getByTestId('admin-hub-attention-toggle');
      await user.click(toggle);

      // Move focus into a row that collapsing is about to unmount.
      const rows = screen.getAllByTestId('admin-hub-attention-item');
      const deepLink = within(rows[rows.length - 1]).getByRole('link');
      deepLink.focus();
      expect(deepLink).toHaveFocus();

      await user.click(screen.getByTestId('admin-hub-attention-toggle'));

      expect(screen.getByTestId('admin-hub-attention-toggle')).toHaveFocus();
      expect(document.body).not.toHaveFocus();
    });

    it('exposes the disclosure as a real button, not a clickable div', async () => {
      await renderWithAttention(makeAttention(25));

      const toggle = screen.getByRole('button', { name: /show all 25/i });
      expect(toggle.tagName).toBe('BUTTON');
      expect(toggle).toHaveAttribute('aria-expanded');
      expect(toggle).toHaveAttribute('aria-controls');
    });

    it('recovers focus to the panel when a background refresh unmounts the focused row', async () => {
      const user = userEvent.setup();
      const { queryClient } = await renderWithAttention(makeAttention(25));

      await user.click(screen.getByTestId('admin-hub-attention-toggle'));

      // Park focus deep in the expanded list, past the preview cap, so the
      // shrink below is guaranteed to unmount the element holding focus.
      const rows = screen.getAllByTestId('admin-hub-attention-item');
      const deepLink = within(rows[rows.length - 1]).getByRole('link');
      deepLink.focus();
      expect(deepLink).toHaveFocus();

      // The farm recovers and the next poll returns two items. That unmounts
      // both the focused row and the toggle, so there is no control left for
      // the browser to fall back to except <body>.
      await backgroundRefresh(queryClient, makeAttention(2));

      await waitFor(() => {
        expect(visibleRowCount()).toBe(2);
      });
      expect(screen.queryByTestId('admin-hub-attention-toggle')).not.toBeInTheDocument();

      // Focus must not be dumped at the top of the document.
      expect(document.body).not.toHaveFocus();
      expect(screen.getByTestId('admin-hub-attention-panel')).toHaveFocus();
    });

    it('does not steal focus from elsewhere on the page when the feed shrinks', async () => {
      const user = userEvent.setup();
      const { queryClient } = await renderWithAttention(makeAttention(25));

      await user.click(screen.getByTestId('admin-hub-attention-toggle'));

      // The operator is working somewhere else entirely. An automatic feed
      // change must never yank focus back into the panel — unrequested focus
      // movement is a worse defect than the one the recovery path fixes.
      const refresh = screen.getByRole('button', { name: /refresh/i });
      refresh.focus();
      expect(refresh).toHaveFocus();

      await backgroundRefresh(queryClient, makeAttention(2));

      await waitFor(() => {
        expect(visibleRowCount()).toBe(2);
      });
      expect(refresh).toHaveFocus();
      expect(screen.getByTestId('admin-hub-attention-panel')).not.toHaveFocus();
    });

    it('recovers focus when the browser fires focusout on the removed row before the effect runs', async () => {
      const user = userEvent.setup();
      const { queryClient } = await renderWithAttention(makeAttention(25));

      await user.click(screen.getByTestId('admin-hub-attention-toggle'));

      const rows = screen.getAllByTestId('admin-hub-attention-item');
      const deepLink = within(rows[rows.length - 1]).getByRole('link');
      deepLink.focus();

      // jsdom silently repoints activeElement to <body> when the focused node
      // is removed and fires *no* focus events. A real browser fires focusout
      // with a null relatedTarget synchronously during the mutation, before
      // layout effects run. Dispatch it explicitly so this asserts the
      // browser's ordering rather than jsdom's convenient omission — the panel
      // must not read that blur as "the user left" and skip the repair.
      act(() => {
        deepLink.dispatchEvent(
          new FocusEvent('focusout', { bubbles: true, relatedTarget: null }),
        );
      });

      await backgroundRefresh(queryClient, makeAttention(2));

      await waitFor(() => {
        expect(visibleRowCount()).toBe(2);
      });
      expect(document.body).not.toHaveFocus();
      expect(screen.getByTestId('admin-hub-attention-panel')).toHaveFocus();
    });

    it('leaves focus on the document when the user parked it there and their row survives', async () => {
      const user = userEvent.setup();
      const { queryClient } = await renderWithAttention(makeAttention(25));

      await user.click(screen.getByTestId('admin-hub-attention-toggle'));

      // `attention-0` is in every fixture, so it survives the shrink below.
      const firstLink = within(screen.getAllByTestId('admin-hub-attention-item')[0]).getByRole(
        'link',
      );
      firstLink.focus();

      // Clicking non-focusable page background blurs to <body> with a null
      // relatedTarget — at blur time that is indistinguishable from a removal,
      // which is why recovery re-checks whether the node actually left the
      // document. It did not, so this focus placement is the user's and stands.
      act(() => {
        firstLink.dispatchEvent(
          new FocusEvent('focusout', { bubbles: true, relatedTarget: null }),
        );
        firstLink.blur();
      });
      expect(document.body).toHaveFocus();

      await backgroundRefresh(queryClient, makeAttention(2));

      await waitFor(() => {
        expect(visibleRowCount()).toBe(2);
      });
      expect(document.body).toHaveFocus();
      expect(screen.getByTestId('admin-hub-attention-panel')).not.toHaveFocus();
    });

    it('names the panel container so focus landing there is announced', async () => {
      await renderWithAttention(makeAttention(4));

      expect(screen.getByTestId('admin-hub-attention-panel')).toHaveAttribute(
        'aria-labelledby',
        'admin-hub-attention-heading',
      );
      expect(document.getElementById('admin-hub-attention-heading')).toHaveTextContent(
        /needs attention/i,
      );
    });
  });

  describe('narrow viewport', () => {
    let restoreViewport: (() => void) | undefined;

    beforeEach(() => {
      restoreViewport = useNarrowViewport();
    });

    afterEach(() => {
      restoreViewport?.();
      restoreViewport = undefined;
    });

    it('previews a single item below the sm breakpoint', async () => {
      await renderWithAttention(makeAttention(25));

      expect(visibleRowCount()).toBe(ATTENTION_PREVIEW_LIMIT_NARROW);
      expect(screen.getByTestId('admin-hub-attention-summary')).toHaveTextContent(
        'Showing 1 of 25 items',
      );
    });

    it('still renders a lone item inline without a disclosure', async () => {
      await renderWithAttention(makeAttention(1));

      expect(visibleRowCount()).toBe(1);
      expect(screen.queryByTestId('admin-hub-attention-toggle')).not.toBeInTheDocument();
    });
  });

  describe('severity summary truthfulness', () => {
    it('summarises an all-Error feed without inventing other severities', async () => {
      await renderWithAttention(makeAttention(25, 25));

      const summary = screen.getByTestId('admin-hub-attention-summary');
      expect(summary).toHaveTextContent('25 Errors');
      expect(summary).not.toHaveTextContent(/Warning/);
      expect(summary).not.toHaveTextContent(/Info/);
      expect(screen.getByTestId('admin-hub-attention-hidden-errors')).toHaveTextContent(
        '22 Errors not shown',
      );
    });

    it('summarises an all-Info feed and claims no hidden Errors', async () => {
      const items = makeAttention(25).map((item) => ({ ...item, severity: 'Info' }));
      await renderWithAttention(items);

      const summary = screen.getByTestId('admin-hub-attention-summary');
      expect(summary).toHaveTextContent('25 Info');
      expect(summary).not.toHaveTextContent(/Error/);
      expect(
        screen.queryByTestId('admin-hub-attention-hidden-errors'),
      ).not.toBeInTheDocument();
    });

    it('does not fold an unrecognised severity into Info', async () => {
      // `severity` is typed as `string` precisely because the backend can add
      // enum members without a frontend release.
      const items = makeAttention(25).map((item, index) =>
        index === 0 ? { ...item, severity: 'Catastrophe' } : item,
      );
      await renderWithAttention(items);

      // A severity the frontend does not know about must be counted honestly
      // rather than silently downgraded to the least alarming bucket.
      expect(screen.getByTestId('admin-hub-attention-summary')).toHaveTextContent(
        /unknown severity/i,
      );
    });
  });

  describe('resilience to feed changes and hostile data', () => {
    it('drops back to the preview when an expanded feed shrinks below the cap', async () => {
      const user = userEvent.setup();
      await renderWithAttention(makeAttention(25));

      await user.click(screen.getByTestId('admin-hub-attention-toggle'));
      expect(visibleRowCount()).toBe(25);

      // The farm recovers: the next poll returns two items, below the cap.
      mockedApiGet.mockResolvedValue({ data: makeOverview(makeAttention(2)) });
      await user.click(screen.getByRole('button', { name: /refresh/i }));

      await waitFor(() => {
        expect(visibleRowCount()).toBe(2);
      });
      expect(screen.queryByTestId('admin-hub-attention-toggle')).not.toBeInTheDocument();

      // And when it degrades again the panel must come back collapsed, not
      // silently re-expanded by a stale `isExpanded`.
      mockedApiGet.mockResolvedValue({ data: makeOverview(makeAttention(25)) });
      await user.click(screen.getByRole('button', { name: /refresh/i }));

      await waitFor(() => {
        expect(screen.getByTestId('admin-hub-attention-toggle')).toBeInTheDocument();
      });
      expect(visibleRowCount()).toBe(ATTENTION_PREVIEW_LIMIT_DESKTOP);
      expect(screen.getByTestId('admin-hub-attention-toggle')).toHaveAttribute(
        'aria-expanded',
        'false',
      );
    });

    it('keeps long unbroken titles and details wrappable so they cannot force horizontal overflow', async () => {
      const items = makeAttention(4).map((item, index) =>
        index === 0
          ? {
              ...item,
              title: 'Printer-with-an-extremely-long-unbroken-identifier-0123456789abcdef',
              detail:
                'http://printer-02.local:7125/server/info?token=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            }
          : item,
      );
      await renderWithAttention(items);

      // `overflow-wrap` is inherited, so asserting it on the row proves the
      // title and detail can both break. jsdom cannot measure the 320px
      // viewport itself; this is the property that makes it safe.
      const row = screen.getAllByTestId('admin-hub-attention-item')[0];
      expect(row.className).toContain('break-words');
    });

    it('refuses a protocol-relative action route instead of linking off-site', async () => {
      const items = makeAttention(1).map((item) => ({
        ...item,
        actionDestinationId: undefined,
        actionRoute: '//evil.example.com/phish',
      }));
      await renderWithAttention(items);

      const row = screen.getAllByTestId('admin-hub-attention-item')[0];
      // The item still renders — the alert is real — but with no link at all
      // rather than a link that leaves the app.
      expect(within(row).queryByRole('link')).not.toBeInTheDocument();
      expect(document.querySelector('a[href^="//evil.example.com"]')).toBeNull();
    });
  });

  describe('cached snapshot after a refresh failure', () => {
    /**
     * React Query keeps the last successful `data` when a background refetch
     * fails, so `isError` alone cannot tell "we have nothing" from "we have a
     * snapshot that just went stale". The hub used to treat both as a hard
     * error and blank the attention and health bands, throwing away the
     * operator's last-known state — including its checked-at — at exactly the
     * moment they needed it. #2517 requires that context be retained.
     */
    async function renderThenFailRefresh(items: AttentionItemDto[]) {
      const user = userEvent.setup();
      mockedApiGet.mockResolvedValueOnce({ data: makeOverview(items) });
      renderHub();
      await waitFor(() => {
        expect(screen.getByTestId('admin-hub-overall-status')).toBeInTheDocument();
      });

      mockedApiGet.mockRejectedValue(new Error('overview unavailable'));
      await user.click(screen.getByRole('button', { name: /refresh/i }));
      await waitFor(() => {
        expect(screen.getByTestId('admin-hub-stale-notice')).toBeInTheDocument();
      });
      return user;
    }

    it('keeps the last-known attention items rather than blanking the band', async () => {
      await renderThenFailRefresh(makeAttention(25, 5));

      expect(visibleRowCount()).toBe(ATTENTION_PREVIEW_LIMIT_DESKTOP);
      expect(screen.getByTestId('admin-hub-attention-toggle')).toHaveTextContent('Show all 25');
      expect(screen.getByTestId('admin-hub-attention-hidden-errors')).toBeInTheDocument();
      // The hard-failure treatment must not fire when we still have a snapshot.
      expect(screen.queryByText(/couldn't load the admin overview/i)).not.toBeInTheDocument();
    });

    it('retains the checked-at context and labels it as the last successful check', async () => {
      await renderThenFailRefresh(makeAttention(4));

      expect(screen.getByText(/last checked at/i)).toBeInTheDocument();
      expect(screen.getByTestId('admin-hub-subsystems')).toBeInTheDocument();
      expect(screen.getByTestId('admin-hub-stale-notice')).toHaveTextContent(
        /may be out of date/i,
      );
      expect(screen.getByTestId('admin-hub-stale-notice')).toHaveTextContent(
        /nothing here has been resolved/i,
      );
    });

    it('offers a working retry that restores the live snapshot', async () => {
      const user = await renderThenFailRefresh(makeAttention(4));

      mockedApiGet.mockResolvedValue({ data: makeOverview(makeAttention(4)) });
      await user.click(screen.getByRole('button', { name: /try again/i }));

      await waitFor(() => {
        expect(screen.queryByTestId('admin-hub-stale-notice')).not.toBeInTheDocument();
      });
      expect(screen.getByText(/^Checked at/i)).toBeInTheDocument();
    });

    it('never reports an all-clear from a healthy snapshot it could not refresh', async () => {
      await renderThenFailRefresh([]);

      // The snapshot was Healthy with zero attention items, so the live copy
      // would have been the reassuring one. A failed refresh must not let that
      // stand as a current all-clear.
      expect(
        screen.queryByText(/every subsystem health check is reporting healthy/i),
      ).not.toBeInTheDocument();
      expect(
        screen.getByText(/that refresh failed — this may be out of date/i),
      ).toBeInTheDocument();
    });

    it('marks the health badge itself as cached, not just the notice above it', async () => {
      await renderThenFailRefresh(makeAttention(4));

      // A user who navigates straight to the status badge must not read a
      // cached "Healthy" as a live one, so the caveat rides on the badge.
      const badge = screen.getByTestId('admin-hub-overall-status');
      expect(badge).toHaveAttribute('data-overall-stale', 'true');
      expect(badge).toHaveTextContent(/\(cached\)/i);
    });

    it('drops the cached caveat from the badge once a refresh succeeds', async () => {
      const user = await renderThenFailRefresh(makeAttention(4));

      mockedApiGet.mockResolvedValue({ data: makeOverview(makeAttention(4)) });
      await user.click(screen.getByRole('button', { name: /try again/i }));

      await waitFor(() => {
        expect(screen.getByTestId('admin-hub-overall-status')).toHaveAttribute(
          'data-overall-stale',
          'false',
        );
      });
      expect(screen.getByTestId('admin-hub-overall-status')).not.toHaveTextContent(/\(cached\)/i);
    });

    it('still shows the hard error when there is no snapshot to fall back on', async () => {
      mockedApiGet.mockRejectedValue(new Error('overview unavailable'));
      renderHub();

      await waitFor(() => {
        expect(screen.getByText(/couldn't load the admin overview/i)).toBeInTheDocument();
      });
      expect(screen.queryByTestId('admin-hub-stale-notice')).not.toBeInTheDocument();
      expect(screen.queryByTestId('admin-hub-attention-panel')).not.toBeInTheDocument();
      expect(screen.queryByTestId('admin-hub-subsystems')).not.toBeInTheDocument();
    });
  });
});
