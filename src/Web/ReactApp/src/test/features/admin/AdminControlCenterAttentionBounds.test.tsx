import React from 'react';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { AdminControlCenterPage } from '@/features/admin/pages/AdminControlCenterPage';
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
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/admin']}>
        <AdminControlCenterPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
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
  renderHub();
  await waitFor(() => {
    expect(screen.getByTestId('admin-hub-overall-status')).toBeInTheDocument();
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
      // reader user knows what they landed in.
      expect(region).toHaveAttribute('tabindex', '0');
      expect(screen.getByRole('region', { name: `All ${count} attention items` })).toBe(region);
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
});
