import React from 'react';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { AdminControlCenterPage } from '@/features/admin/pages/AdminControlCenterPage';
import { AdminNavPinsProvider } from '@/common/contexts/AdminNavPinsContext';
import type { AdminOverviewDto } from '@/types/adminOverview';
import { ADMIN_HUB_ROUTE_STATE } from '@/features/admin/utils/adminHubParentState';

vi.mock('react-router', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router')>();
  return {
    ...actual,
    Link: ({ state, ...props }: Record<string, unknown>) => React.createElement(actual.Link, {
      ...props,
      state,
      'data-route-state': state ? JSON.stringify(state) : undefined,
    }),
  };
});

// ── Mocks ────────────────────────────────────────────────────────────────────

vi.mock('@/services/api/httpClient', () => ({
  client: {
    get: vi.fn(),
  },
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: vi.fn(),
}));

// Fold PageTemplate to a minimal wrapper so we can assert on the hub's own DOM
// without navigating global chrome. The real PageTemplate is covered elsewhere.
vi.mock('@/common/components/PageTemplate', () => ({
  PageTemplate: ({
    title,
    subtitle,
    parent,
    actions,
    children,
  }: {
    title: string;
    subtitle?: string;
    parent?: { label: string };
    actions?: React.ReactNode;
    children: React.ReactNode;
  }) => (
    <div data-testid="page-template">
      {parent && <div data-testid="page-parent">{parent.label}</div>}
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

function makeOverview(overrides: Partial<AdminOverviewDto> = {}): AdminOverviewDto {
  return {
    checkedAt: '2026-07-25T17:04:00Z',
    overallStatus: 'Degraded',
    subsystems: [
      { key: 'api', name: 'API', status: 'Healthy', detail: 'Responding' },
      {
        key: 'database',
        name: 'Database',
        status: 'Healthy',
        detail: 'PostgreSQL · seeded (8 manufacturers)',
      },
      { key: 'signalr', name: 'SignalR Hub', status: 'Healthy', detail: 'Hub accessible' },
      {
        key: 'backends',
        name: 'Printer Backends',
        status: 'Degraded',
        detail: '2 / 3 reachable',
      },
    ],
    attention: [
      {
        key: 'printer-1111-unreachable',
        severity: 'Warning',
        title: "Printer 'printer-02' is unreachable",
        detail:
          'printer-02 did not respond at http://printer-02.local:7125/server/info (Connection refused).',
        actionLabel: 'Open Printers',
        actionRoute: '/printers',
      },
    ],
    ...overrides,
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

function operatorAccess() {
  return {
    ...farmAdminAccess(),
    hasRole: (role: string) => role === 'operator',
    hasPermission: () => false,
  };
}

function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
}

function renderHub() {
  const client = createQueryClient();
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/admin']}>
        <AdminNavPinsProvider>
          <AdminControlCenterPage />
        </AdminNavPinsProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

// ── Tests ────────────────────────────────────────────────────────────────────

describe('AdminControlCenterPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    // Default: authenticated farm_admin.
    // Individual tests override via mockReturnValue.
    mockedUseAuth.mockReturnValue(
      farmAdminAccess() as unknown as ReturnType<typeof useAuth>,
    );
  });

  it('renders the AdminLoading placeholder while the overview is in flight', () => {
    // Keep the promise unresolved for this render pass.
    mockedApiGet.mockImplementation(() => new Promise(() => {}));

    renderHub();

    expect(screen.getByTestId('admin-loading-card-grid')).toBeInTheDocument();
  });

  it('renders subsystem tiles for every subsystem the server returns', async () => {
    mockedApiGet.mockResolvedValue({ data: makeOverview() });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-subsystems')).toBeInTheDocument();
    });

    const tiles = screen.getAllByTestId('admin-hub-subsystem');
    expect(tiles).toHaveLength(4);

    const keys = tiles.map((tile) => tile.getAttribute('data-subsystem-key'));
    expect(keys).toEqual(['api', 'database', 'signalr', 'backends']);

    // Status text is present alongside icon (WCAG: no color-only signalling).
    expect(within(tiles[3]).getByText('Degraded')).toBeInTheDocument();
  });

  it('puts attention before health and keeps the checked timestamp visible', async () => {
    mockedApiGet.mockResolvedValue({ data: makeOverview() });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-attention')).toBeInTheDocument();
    });

    const headings = screen.getAllByRole('heading');
    expect(headings.findIndex((heading) => heading.textContent === 'Needs attention')).toBeLessThan(
      headings.findIndex((heading) => heading.textContent === 'System health checks'),
    );
    expect(screen.getByText(/Checked at/i)).toBeInTheDocument();
  });

  it('reflects the worst subsystem status in the overall badge (issue #2222 regression)', async () => {
    // Reproduces the exact reported scenario: printer backends degraded
    // (4/5 reachable) while every other subsystem is healthy. The overall
    // status must roll up to Degraded, not silently report Healthy.
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        overallStatus: 'Degraded',
        subsystems: [
          { key: 'api', name: 'API', status: 'Healthy', detail: 'Responding' },
          { key: 'database', name: 'Database', status: 'Healthy', detail: 'Connected' },
          { key: 'signalr', name: 'SignalR', status: 'Healthy', detail: 'Connected' },
          {
            key: 'backends',
            name: 'Printer Backends',
            status: 'Degraded',
            detail: '4/5 reachable',
          },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-subsystems')).toBeInTheDocument();
    });

    const overallBadge = screen.getByTestId('admin-hub-overall-status');
    expect(overallBadge).toHaveAttribute('data-overall-status', 'Degraded');
    expect(within(overallBadge).getByText(/Health checks: Degraded/i)).toBeInTheDocument();
    expect(within(overallBadge).queryByText(/Health checks: Healthy/i)).not.toBeInTheDocument();
  });

  it('shows a healthy overall badge when every subsystem is healthy', async () => {
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        overallStatus: 'Healthy',
        subsystems: [
          { key: 'api', name: 'API', status: 'Healthy', detail: 'Responding' },
          { key: 'database', name: 'Database', status: 'Healthy', detail: 'Connected' },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-subsystems')).toBeInTheDocument();
    });

    const overallBadge = screen.getByTestId('admin-hub-overall-status');
    expect(overallBadge).toHaveAttribute('data-overall-status', 'Healthy');
    expect(within(overallBadge).getByText(/Health checks: Healthy/i)).toBeInTheDocument();
  });

  /**
   * #2517: the hub's health band and the header's System pill read different
   * feeds — subsystem health checks (`/api/admin/overview`) versus service
   * health (`/api/system/info`). Users were reading the two as contradicting
   * each other. The band must therefore say which feed it speaks for and point
   * at the other, so a disagreement reads as two domains rather than a bug.
   */
  it('states which health feed the band reports and names the other one', async () => {
    mockedApiGet.mockResolvedValue({ data: makeOverview() });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-subsystems')).toBeInTheDocument();
    });

    expect(
      screen.getByRole('heading', { name: 'System health checks' }),
    ).toBeInTheDocument();

    const domainNote = screen.getByTestId('admin-hub-health-domain');
    expect(domainNote).toHaveTextContent(/admin overview/i);
    expect(domainNote).toHaveTextContent(/System pill/i);
    expect(domainNote).toHaveTextContent(/service health/i);

    // The badge must not read as an unqualified whole-system verdict.
    expect(screen.getByTestId('admin-hub-overall-status')).toHaveTextContent(
      /^Health checks:/,
    );
  });

  it('does not hardcode the four subsystems — renders whatever arrives (e.g. spoolman)', async () => {
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        subsystems: [
          { key: 'api', name: 'API', status: 'Healthy', detail: 'Responding' },
          { key: 'spoolman', name: 'Spoolman', status: 'Healthy', detail: 'Connected' },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-subsystems')).toBeInTheDocument();
    });

    const tiles = screen.getAllByTestId('admin-hub-subsystem');
    expect(tiles).toHaveLength(2);
    expect(tiles[1].getAttribute('data-subsystem-key')).toBe('spoolman');
    expect(within(tiles[1]).getByText('Spoolman')).toBeInTheDocument();
  });

  it('degrades unknown subsystem statuses to the Unknown treatment without crashing', async () => {
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        subsystems: [
          {
            key: 'future',
            name: 'Something New',
            status: 'Chartreuse',
            detail: 'never before seen',
          },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-subsystems')).toBeInTheDocument();
    });

    const tile = screen.getByTestId('admin-hub-subsystem');
    expect(tile.getAttribute('data-subsystem-status')).toBe('Chartreuse');
    // Falls through to a labelled badge — the label is the raw value so the
    // operator still gets a signal, but there is no thrown error.
    expect(within(tile).getByText('Chartreuse')).toBeInTheDocument();
    expect(tile).toHaveAttribute(
      'aria-label',
      expect.stringContaining('Unknown status "Chartreuse"'),
    );
  });

  it('renders a single reassuring line when the attention list is empty', async () => {
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        overallStatus: 'Healthy',
        attention: [],
        subsystems: [
          { key: 'api', name: 'API', status: 'Healthy', detail: 'Responding' },
          {
            key: 'database',
            name: 'Database',
            status: 'Healthy',
            detail: 'PostgreSQL · seeded (8 manufacturers)',
          },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-attention-clear')).toBeInTheDocument();
    });
    expect(screen.getByTestId('admin-hub-attention-clear')).toHaveTextContent(
      'Nothing needs your attention — every subsystem health check is reporting healthy.',
    );
    // An all-clear must not be an illustrated empty state: it used to push the
    // destination grid down by 206px to report that nothing happened.
    expect(
      screen.queryByRole('heading', { name: /nothing needs your attention/i }),
    ).not.toBeInTheDocument();
    expect(screen.queryByTestId('admin-hub-attention')).not.toBeInTheDocument();
  });

  it('does not claim all-clear when attention is empty but health is degraded', async () => {
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        overallStatus: 'Degraded',
        attention: [],
        subsystems: [
          { key: 'api', name: 'API', status: 'Degraded', detail: 'Intermittent failures' },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-attention-clear')).toBeInTheDocument();
    });

    expect(screen.getByTestId('admin-hub-attention-clear')).toHaveTextContent(
      'The admin overview reported no attention items. Review the system health checks below for the current status.',
    );
    expect(screen.getByTestId('admin-hub-attention-clear')).not.toHaveTextContent(
      'every subsystem health check is reporting healthy',
    );
  });

  it('does not claim all-clear when the overview reports no subsystems', async () => {
    mockedApiGet.mockResolvedValue({
      data: makeOverview({ overallStatus: 'Unknown', attention: [], subsystems: [] }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-attention-clear')).toBeInTheDocument();
    });

    expect(screen.getByTestId('admin-hub-attention-clear')).toHaveTextContent(
      'The admin overview reported no attention items. Review the system health checks below for the current status.',
    );
    expect(screen.getByRole('heading', { name: 'No subsystems reported' })).toBeInTheDocument();
  });

  it('renders attention rows with action links when actionRoute is present', async () => {
    mockedApiGet.mockResolvedValue({ data: makeOverview() });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-attention')).toBeInTheDocument();
    });

    const row = screen.getByTestId('admin-hub-attention-item');
    expect(within(row).getByText("Printer 'printer-02' is unreachable")).toBeInTheDocument();
    // Text label paired with icon/colour.
    expect(within(row).getByText('Warning')).toBeInTheDocument();

    const actionLink = within(row).getByRole('link', { name: /Open Printers/i });
    expect(actionLink).toHaveAttribute('href', '/printers');
  });

  it('marks a raw attention fallback route as admin-origin', async () => {
    mockedApiGet.mockResolvedValue({ data: makeOverview() });

    renderHub();

    const actionLink = await screen.findByRole('link', { name: /Open Printers/i });
    expect(actionLink).toHaveAttribute('data-route-state', JSON.stringify(ADMIN_HUB_ROUTE_STATE));
  });

  it('omits the action link when actionRoute is missing', async () => {
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        attention: [
          {
            key: 'nolink',
            severity: 'Info',
            title: 'Something is worth noting',
            detail: 'No dedicated destination for this.',
            actionLabel: null,
            actionRoute: null,
          },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-attention-item')).toBeInTheDocument();
    });

    // The only <a> matching an attention row shouldn't exist.
    const row = screen.getByTestId('admin-hub-attention-item');
    expect(within(row).queryByRole('link')).not.toBeInTheDocument();
  });

  it('resolves actionDestinationId through the ADMIN_DESTINATIONS registry', async () => {
    // Backend sends the stable id "ops-status" rather than a hardcoded route.
    // The client must look it up in ADMIN_DESTINATIONS and use the current canonical
    // path (/admin/status), never a legacy /admin/system.
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        attention: [
          {
            key: 'admin-overview-probe-failed',
            severity: 'Error',
            title: 'System health probes are not reporting',
            detail: 'probe error',
            actionLabel: 'Open System logs',
            actionDestinationId: 'ops-status',
            actionRoute: null,
          },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-attention-item')).toBeInTheDocument();
    });

    const row = screen.getByTestId('admin-hub-attention-item');
    const actionLink = within(row).getByRole('link', { name: /Open System logs/i });
    expect(actionLink).toHaveAttribute('href', '/admin/status');
    // Legacy path must not leak through.
    expect(actionLink).not.toHaveAttribute('href', '/admin/system');
  });

  it('marks a hub tile to a top-level admin destination as admin-origin', async () => {
    mockedApiGet.mockResolvedValue({ data: makeOverview() });

    renderHub();

    const analyticsCard = await screen.findByRole('link', { name: /analytics production, cost, and utilization dashboards\./i });
    expect(analyticsCard).toHaveAttribute('data-route-state', JSON.stringify(ADMIN_HUB_ROUTE_STATE));
  });

  it('prefers actionDestinationId over actionRoute when both are supplied', async () => {
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        attention: [
          {
            key: 'both',
            severity: 'Error',
            title: 'Both fields set',
            detail: 'id wins',
            actionLabel: 'Open',
            actionDestinationId: 'ops-status',
            actionRoute: '/legacy-fallback-should-not-be-used',
          },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-attention-item')).toBeInTheDocument();
    });

    const row = screen.getByTestId('admin-hub-attention-item');
    const actionLink = within(row).getByRole('link', { name: /Open/i });
    expect(actionLink).toHaveAttribute('href', '/admin/status');
  });

  it('omits a stable action destination when the current user lacks its permission', async () => {
    mockedUseAuth.mockReturnValue({
      ...farmAdminAccess(),
      hasPermission: () => false,
    } as unknown as ReturnType<typeof useAuth>);
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        attention: [
          {
            key: 'denied',
            severity: 'Error',
            title: 'Denied destination',
            detail: 'The destination is not available to this principal.',
            actionLabel: 'Open',
            actionDestinationId: 'ops-status',
            actionRoute: '/printers',
          },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-attention-item')).toBeInTheDocument();
    });

    expect(screen.getByTestId('admin-hub-attention-item').querySelector('a')).toBeNull();
  });

  it('falls back to actionRoute when actionDestinationId does not resolve', async () => {
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        attention: [
          {
            key: 'unknown-id',
            severity: 'Warning',
            title: 'Unknown destination id',
            detail: 'Backend shipped an id the frontend does not know about.',
            actionLabel: 'Open',
            actionDestinationId: 'nonexistent-destination-xyz',
            actionRoute: '/printers',
          },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-attention-item')).toBeInTheDocument();
    });

    const row = screen.getByTestId('admin-hub-attention-item');
    const actionLink = within(row).getByRole('link', { name: /Open/i });
    expect(actionLink).toHaveAttribute('href', '/printers');
  });

  it('drops the action link when only an unknown destination id is supplied', async () => {
    // Registry drift: id unknown AND no route fallback → link disappears entirely.
    // Better a visible missing button than a silent broken navigation.
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        attention: [
          {
            key: 'orphan',
            severity: 'Warning',
            title: 'Orphaned destination',
            detail: 'Backend shipped a stale id; the link cannot be rendered.',
            actionLabel: 'Open',
            actionDestinationId: 'nonexistent-destination-xyz',
            actionRoute: null,
          },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-attention-item')).toBeInTheDocument();
    });

    const row = screen.getByTestId('admin-hub-attention-item');
    expect(within(row).queryByRole('link')).not.toBeInTheDocument();
  });

  it('suppresses retired /admin/manage action routes', async () => {
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        attention: [
          {
            key: 'retired-route',
            severity: 'Warning',
            title: 'Retired admin route',
            detail: 'The old dashboard route is no longer supported.',
            actionLabel: 'Open',
            actionRoute: '/admin/manage?tab=system',
          },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-attention-item')).toBeInTheDocument();
    });

    expect(screen.getByTestId('admin-hub-attention-item')).not.toHaveAttribute(
      'href',
      '/admin/manage?tab=system',
    );
    expect(screen.getByTestId('admin-hub-attention-item').querySelector('a')).toBeNull();
  });

  it('degrades unknown attention severities to the Info treatment without crashing', async () => {
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        attention: [
          {
            key: 'unknown-sev',
            severity: 'CosmicRay',
            title: 'A brand-new severity',
            detail: 'Client older than server; should still render.',
          },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-attention-item')).toBeInTheDocument();
    });

    const row = screen.getByTestId('admin-hub-attention-item');
    expect(row.getAttribute('data-attention-severity')).toBe('CosmicRay');
    expect(within(row).getByText('CosmicRay')).toBeInTheDocument();
    expect(within(row).getByText('A brand-new severity')).toBeInTheDocument();
  });

  it('renders AdminError with a working retry when the fetch fails', async () => {
    const user = userEvent.setup();
    const errorInstance = new Error('boom');
    mockedApiGet
      .mockRejectedValueOnce(errorInstance)
      .mockResolvedValueOnce({ data: makeOverview() });

    renderHub();

    await waitFor(() => {
      expect(
        screen.getByRole('heading', { name: "Couldn't load the admin overview" }),
      ).toBeInTheDocument();
    });

    const retryButton = screen.getByRole('button', { name: /try again/i });
    await user.click(retryButton);

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-subsystems')).toBeInTheDocument();
    });
    expect(
      screen.queryByRole('heading', { name: "Couldn't load the admin overview" }),
    ).not.toBeInTheDocument();
    expect(mockedApiGet).toHaveBeenCalledTimes(2);
  });

  it('renders permitted operational cards and one settings entry from the registry', async () => {
    mockedApiGet.mockResolvedValue({ data: makeOverview() });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-operations')).toBeInTheDocument();
    });

    const cards = screen.getAllByTestId('admin-hub-destination');
    // 8 operational + 3 standalone configuration (Catalog, Locations, Power
    // Monitors) + 1 Farm & Admin Settings entry point.
    expect(cards.length).toBe(12);
    // Every card links somewhere absolute.
    for (const card of cards) {
      expect(card.getAttribute('href')).toMatch(/^\//);
    }
    expect(screen.getByRole('link', { name: /Farm & Admin Settings/i })).toHaveAttribute(
      'href',
      '/admin/settings?scope=system',
    );
    expect(screen.getByRole('link', { name: /Workers & Jobs/i })).toHaveAttribute(
      'href',
      '/admin/workers?workerTab=jobs',
    );
    expect(screen.getByRole('link', { name: /Power Monitors/i })).toHaveAttribute(
      'href',
      '/admin/power-monitors',
    );
    expect(screen.queryByText('Everything you can manage')).not.toBeInTheDocument();
  });

  it('never links back to itself — a hub has no self-link (#2526)', async () => {
    mockedApiGet.mockResolvedValue({ data: makeOverview() });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-operations')).toBeInTheDocument();
    });

    // The page *is* /admin. Every destination card, the settings entry point and
    // the attention actions must point somewhere else. The global Admin nav
    // entry and child pages' back links live outside this component and are
    // unaffected.
    //
    // Issue 2526 AC requires assertions that distinguish main content from
    // global navigation. This component renders page content only — the assert
    // below proves the global rail is genuinely absent from this tree, so the
    // document-wide anchor scan that follows can only see hub content. The
    // surviving global Admin entry is asserted separately, against the real
    // rail, in test/features/navigation/navigation-sections.test.tsx.
    expect(document.querySelector('nav[aria-label="Main navigation"]')).toBeNull();
    for (const card of screen.getAllByTestId('admin-hub-destination')) {
      expect(card.getAttribute('href')).not.toBe('/admin');
    }
    const selfLinks = Array.from(document.querySelectorAll<HTMLAnchorElement>('a[href]'))
      .filter((link) => {
        const href = link.getAttribute('href') ?? '';
        return href === '/admin'
          || href === '/admin/'
          || href.startsWith('/admin?')
          || href.startsWith('/admin#')
          || href.startsWith('/admin/?')
          || href.startsWith('/admin/#');
      });
    expect(selfLinks).toHaveLength(0);
  });

  it('opens the pin chooser with authorized destinations and restores focus on Escape', async () => {
    mockedApiGet.mockResolvedValue({ data: makeOverview() });
    const user = userEvent.setup();

    renderHub();

    const launcher = screen.getByRole('button', { name: 'Pin admin links' });
    await user.click(launcher);

    expect(screen.getByRole('region', { name: 'Pin admin links' })).toBeInTheDocument();
    const analyticsPin = screen.getByRole('button', { name: 'Pin Analytics from navbar' });
    expect(analyticsPin).toHaveAttribute('aria-pressed', 'false');
    await user.click(analyticsPin);
    expect(analyticsPin).toHaveAttribute('aria-pressed', 'true');

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('region', { name: 'Pin admin links' })).not.toBeInTheDocument();
    expect(launcher).toHaveFocus();
  });

  it('reorders pinned admin links in the chooser and preserves the order across remount', async () => {
    mockedApiGet.mockResolvedValue({ data: makeOverview() });
    const user = userEvent.setup();

    const firstRender = renderHub();

    await user.click(screen.getByRole('button', { name: 'Pin admin links' }));
    await user.click(screen.getByRole('button', { name: 'Pin Analytics from navbar' }));
    await user.click(screen.getByRole('button', { name: 'Pin Workers & Jobs from navbar' }));

    const destinationList = screen.getByRole('list', { name: 'Authorized admin destinations' });
    let [firstRow, secondRow] = within(destinationList).getAllByRole('listitem');
    expect(within(firstRow).getByText('Analytics')).toBeInTheDocument();
    expect(within(secondRow).getByText('Workers & Jobs')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Move Analytics down' }));

    [firstRow, secondRow] = within(destinationList).getAllByRole('listitem');
    expect(within(firstRow).getByText('Workers & Jobs')).toBeInTheDocument();
    expect(within(secondRow).getByText('Analytics')).toBeInTheDocument();

    firstRender.unmount();
    renderHub();

    await user.click(screen.getByRole('button', { name: 'Pin admin links' }));
    [firstRow, secondRow] = within(screen.getByRole('list', { name: 'Authorized admin destinations' })).getAllByRole('listitem');
    expect(within(firstRow).getByText('Workers & Jobs')).toBeInTheDocument();
    expect(within(secondRow).getByText('Analytics')).toBeInTheDocument();
  });

  it('uses the same live announcement for button and drag reorder interactions', async () => {
    mockedApiGet.mockResolvedValue({ data: makeOverview() });
    const user = userEvent.setup();

    renderHub();

    await user.click(screen.getByRole('button', { name: 'Pin admin links' }));
    await user.click(screen.getByRole('button', { name: 'Pin Analytics from navbar' }));
    await user.click(screen.getByRole('button', { name: 'Pin Workers & Jobs from navbar' }));

    await user.click(screen.getByRole('button', { name: 'Move Analytics down' }));
    const announcement = screen.getByText('Moved Analytics to position 2 of 2.');
    expect(announcement).toHaveAttribute('aria-live', 'polite');

    const destinationList = screen.getByRole('list', { name: 'Authorized admin destinations' });
    const [workersRow, analyticsRow] = within(destinationList).getAllByRole('listitem');
    const dataTransfer = { effectAllowed: '', setData: vi.fn() };
    await act(async () => {
      fireEvent.dragStart(analyticsRow, { dataTransfer });
    });
    await act(async () => {
      fireEvent.dragOver(workersRow);
      fireEvent.drop(workersRow);
    });

    expect(screen.getByText('Moved Analytics to position 1 of 2.')).toHaveAttribute('aria-live', 'polite');
    expect(within(destinationList).getAllByRole('listitem')[0]).toHaveTextContent('Analytics');
  });

  it('does not accept a pinned shortcut drop on an unpinned destination', async () => {
    mockedApiGet.mockResolvedValue({ data: makeOverview() });
    const user = userEvent.setup();

    renderHub();

    await user.click(screen.getByRole('button', { name: 'Pin admin links' }));
    await user.click(screen.getByRole('button', { name: 'Pin Analytics from navbar' }));
    await user.click(screen.getByRole('button', { name: 'Pin Workers & Jobs from navbar' }));

    const destinationList = screen.getByRole('list', { name: 'Authorized admin destinations' });
    const analyticsRow = within(destinationList).getByText('Analytics').closest('[role="listitem"]')!;
    const statusRow = within(destinationList).getByText('System Status').closest('[role="listitem"]')!;
    const dataTransfer = { effectAllowed: '', setData: vi.fn() };
    await act(async () => {
      fireEvent.dragStart(analyticsRow, { dataTransfer });
    });
    await act(async () => {
      fireEvent.dragOver(statusRow);
      fireEvent.drop(statusRow);
    });

    expect(within(destinationList).getAllByRole('listitem')[0]).toHaveTextContent('Analytics');
    expect(within(destinationList).getAllByRole('listitem')[1]).toHaveTextContent('Workers & Jobs');
  });

  // #2526 — removing a destination from the navbar is only safe because the hub
  // actually owns it. This is the positive half of that contract: if a tile ever
  // disappears from the hub, the destination is stranded with no default home.
  it.each([
    ['Maintenance', '/maintenance'],
    ['Analytics', '/analytics'],
    ['Locations', '/locations'],
    ['Catalog', '/catalog'],
    ['Auto-Dispatch', '/auto-dispatch'],
    ['Printed Parts', '/parts-inventory'],
  ])('owns %s as its single default home (#2526)', async (_label, href) => {
    mockedApiGet.mockResolvedValue({ data: makeOverview() });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-operations')).toBeInTheDocument();
    });

    const tiles = screen
      .getAllByTestId('admin-hub-destination')
      .filter((card) => card.getAttribute('href') === href);
    expect(tiles).toHaveLength(1);
  });

  it.each([
    ['a destination id that resolves to /admin', { actionDestinationId: 'admin-home' }],
    ['a raw /admin action route', { actionRoute: '/admin' }],
    ['a query-suffixed /admin action route', { actionRoute: '/admin?from=attention' }],
    ['a hash-suffixed /admin action route', { actionRoute: '/admin#attention' }],
    ['a trailing-slash /admin/ action route', { actionRoute: '/admin/' }],
    ['a trailing-slash query /admin/?x=1 action route', { actionRoute: '/admin/?x=1' }],
    // Route *identity* must survive equivalent spellings. React Router matches
    // case-insensitively and folds a trailing slash, so each of these lands on
    // /admin and would be a live self-link under a raw string comparison.
    ['an uppercase /ADMIN action route', { actionRoute: '/ADMIN' }],
    ['a mixed-case /Admin/ action route', { actionRoute: '/Admin/' }],
    ['a whitespace-padded /admin action route', { actionRoute: '  /admin  ' }],
    ['a repeated-trailing-slash /admin// action route', { actionRoute: '/admin//' }],
    // A browser applies full URL semantics to an href before the router sees
    // it, so these three also resolve to /admin. Lexical normalisation misses
    // every one of them.
    ['a dot-segment route resolving to /admin', { actionRoute: '/foo/../admin' }],
    ['a dot-segment route climbing above root', { actionRoute: '/foo/bar/../../admin' }],
    ['a percent-encoded dot-segment route', { actionRoute: '/foo/%2e%2e/admin' }],
    ['a percent-encoded /%61dmin action route', { actionRoute: '/%61dmin' }],
    ['a percent-encoded trailing slash /admin%2f', { actionRoute: '/admin%2f' }],
    ['a backslash-folded /admin\\ action route', { actionRoute: '/admin\\' }],
    ['a dot-segment route resolving to retired /admin/manage', { actionRoute: '/admin/settings/../manage' }],
  ])('suppresses an attention action pointing at the hub itself — %s (#2526)', async (_label, action) => {
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        attention: [
          {
            key: 'self-link',
            severity: 'Warning',
            title: 'Points at the hub',
            detail: 'A backend item that would send the user back to /admin.',
            actionLabel: 'Open',
            ...action,
          },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-attention-item')).toBeInTheDocument();
    });

    expect(screen.getByTestId('admin-hub-attention-item').querySelector('a')).toBeNull();
  });

  // `actionRoute` is untrusted backend payload rendered straight into a link
  // target. A malformed or compromised payload must make the link disappear, not
  // navigate off-origin. `startsWith('/')` alone is not enough: a
  // protocol-relative URL passes it and still leaves the app.
  it.each([
    ['a protocol-relative URL', '//evil.test/steal'],
    ['a backslash protocol-relative URL', '/\\evil.test/steal'],
    ['an absolute external URL', 'https://evil.test/steal'],
    ['a javascript: scheme', 'javascript:alert(1)'],
    ['a data: scheme', 'data:text/html,<script>alert(1)</script>'],
    ['a mailto: scheme', 'mailto:someone@evil.test'],
    ['a bare relative path with no leading slash', 'admin/status'],
    ['an embedded newline', '/admin\nstatus'],
    ['a literal backslash path separator', '/printers\\evil'],
    ['malformed percent-encoding', '/printers/%zz'],
    // Dot-segment resolution can *create* a protocol-relative path from an
    // app-relative input: ".." pops the segment before an empty segment, so
    // these normalise to "//evil.test/steal" and navigate off-origin.
    ['a dot-segment path that normalises to protocol-relative', '/foo/..//evil.test/steal'],
    ['a dot-segment path normalising to triple-slash', '/foo/..///evil.test/steal'],
    ['a root-relative dot segment normalising to protocol-relative', '/..//evil.test'],
    ['an empty segment after a percent-encoded dot segment', '/foo/%2e%2e//evil.test/steal'],
    // Suffix invariance: a query or hash must not launder a hostile path. The
    // guard canonicalises the *pathname*, so appending "?x=1" or "#f" to any of
    // the payloads above must not change the verdict.
    ['a protocol-relative URL with a query suffix', '//evil.test/steal?x=1'],
    ['a protocol-relative URL with a hash suffix', '//evil.test/steal#f'],
    ['a dot-segment protocol-relative path with a query suffix', '/foo/..//evil.test/steal?x=1'],
    ['a dot-segment protocol-relative path with a hash suffix', '/foo/..//evil.test/steal#f'],
    ['a backslash protocol-relative URL with a query suffix', '/\\evil.test/steal?x=1'],
    ['an empty string', ''],
  ])('drops an unsafe backend action route — %s', async (_label, actionRoute) => {
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        attention: [
          {
            key: 'unsafe-route',
            severity: 'Warning',
            title: 'Untrusted target',
            detail: 'The backend supplied a route that is not an in-app destination.',
            actionLabel: 'Open',
            actionRoute,
          },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-attention-item')).toBeInTheDocument();
    });

    expect(screen.getByTestId('admin-hub-attention-item').querySelector('a')).toBeNull();
  });

  it.each([
    ['a child destination under the hub', '/admin/status'],
    ['the settings shell', '/admin/settings?tab=general'],
    ['the worker console', '/admin/workers?workerTab=jobs'],
    // The naive `startsWith('/admin')` bug would wrongly suppress this: it is a
    // sibling route whose path merely shares the `/admin` prefix, not a child.
    ['an unrelated sibling route sharing the /admin prefix', '/admin-something'],
  ])('keeps a legitimate action route — %s (the self-link guard is exact)', async (_label, actionRoute) => {
    mockedApiGet.mockResolvedValue({
      data: makeOverview({
        attention: [
          {
            key: 'child-route',
            severity: 'Warning',
            title: 'Worker offline',
            detail: 'A child destination under /admin is still a valid target.',
            actionLabel: 'Open',
            actionRoute,
          },
        ],
      }),
    });

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-attention-item')).toBeInTheDocument();
    });

    expect(screen.getByTestId('admin-hub-attention-item').querySelector('a')).toHaveAttribute(
      'href',
      actionRoute,
    );
  });

  it('bypasses overview fetch and hides health/attention bands for non-system-settings delegates', async () => {
    // A delegate who has specific resource permissions (e.g. printers:admin)
    // but not system_settings:admin or farm_admin should not trigger a 403
    // on GET /admin/overview.
    mockedUseAuth.mockReturnValue({
      isAuthenticated: true,
      isLoading: false,
      user: {
        id: 'user-delegate',
        email: 'delegate@test.com',
        roles: ['operator'],
        isActive: true,
      },
      hasRole: () => false,
      hasPermission: (resource: string, action: string) =>
        resource === 'printers' && action === 'admin',
      error: null,
      login: vi.fn(),
      loginWithPasskey: vi.fn(),
      register: vi.fn(),
      logout: vi.fn(),
    } as unknown as ReturnType<typeof useAuth>);

    renderHub();

    // Overview endpoint is never called
    expect(mockedApiGet).not.toHaveBeenCalled();

    // Health and attention bands are not rendered
    expect(screen.queryByTestId('admin-hub-health-heading')).not.toBeInTheDocument();
    expect(screen.queryByTestId('admin-hub-subsystems')).not.toBeInTheDocument();
    expect(screen.queryByTestId('admin-hub-attention-heading')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /refresh/i })).not.toBeInTheDocument();

    // Destination cards for accessible resources are still rendered
    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-operations')).toBeInTheDocument();
    });
    const cards = screen.getAllByTestId('admin-hub-destination');
    expect(cards.length).toBe(1);
    expect(screen.getByRole('link', { name: /Farm & Admin Settings/i })).toHaveAttribute(
      'href',
      '/admin/settings?scope=system',
    );
  });

  it('hides every admin destination for a non-admin user', async () => {
    mockedUseAuth.mockReturnValue(
      operatorAccess() as unknown as ReturnType<typeof useAuth>,
    );
    mockedApiGet.mockResolvedValue({ data: makeOverview() });

    renderHub();

    await waitFor(() => {
      expect(
        screen.getByRole('heading', { name: 'No operational tools available' }),
      ).toBeInTheDocument();
    });
    expect(screen.queryByTestId('admin-hub-destination')).not.toBeInTheDocument();
  });

  it('does not show a dead-end settings card for a delegate whose only permission is a configuration destination outside /admin/settings', async () => {
    // catalog:admin grants the `data-catalog` destination (kind: 'configuration',
    // path `/catalog`) but does not unlock any `/admin/settings`-reachable tab.
    // A "Farm & Admin Settings" card here would be a visible-but-denied false
    // affordance (see PR #2510 review feedback).
    mockedUseAuth.mockReturnValue({
      isAuthenticated: true,
      isLoading: false,
      user: {
        id: 'user-catalog-only',
        email: 'catalog-delegate@test.com',
        roles: ['operator'],
        isActive: true,
      },
      hasRole: () => false,
      hasPermission: (resource: string, action: string) =>
        resource === 'catalog' && action === 'admin',
      error: null,
      login: vi.fn(),
      loginWithPasskey: vi.fn(),
      register: vi.fn(),
      logout: vi.fn(),
    } as unknown as ReturnType<typeof useAuth>);

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-operations')).toBeInTheDocument();
    });

    // catalog:admin does not map to any of the curated operational
    // destinations, so the Operations band is empty for this delegate — but the
    // `data-catalog` configuration destination is surfaced directly so the hub
    // is not a dead end, and no dead-end settings card is offered.
    expect(
      screen.queryByRole('link', { name: /Farm & Admin Settings/i }),
    ).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Catalog/i })).toHaveAttribute('href', '/catalog');
    expect(
      screen.queryByRole('heading', { name: 'No operational tools available' }),
    ).not.toBeInTheDocument();
  });

  it('renders a direct card for a delegate whose only permission is power_monitors:admin', async () => {
    // `hw-power-monitors` is a configuration destination at /admin/power-monitors —
    // outside the /admin/settings shell and, unlike /catalog and /locations, only
    // reachable through the admin surface. `hasAccessibleHubTile` lights up the
    // Admin nav entry for this delegate, so the hub must render something for them
    // rather than "No operational tools available" (#2508, Hicks review).
    mockedUseAuth.mockReturnValue({
      isAuthenticated: true,
      isLoading: false,
      user: {
        id: 'user-power-monitors-only',
        email: 'power-delegate@test.com',
        roles: ['operator'],
        isActive: true,
      },
      hasRole: () => false,
      hasPermission: (resource: string, action: string) =>
        resource === 'power_monitors' && action === 'admin',
      error: null,
      login: vi.fn(),
      loginWithPasskey: vi.fn(),
      register: vi.fn(),
      logout: vi.fn(),
    } as unknown as ReturnType<typeof useAuth>);

    renderHub();

    await waitFor(() => {
      expect(screen.getByTestId('admin-hub-configuration')).toBeInTheDocument();
    });

    // Overview is farm_admin / system_settings:admin only — this delegate must not fetch it.
    expect(mockedApiGet).not.toHaveBeenCalled();

    const cards = screen.getAllByTestId('admin-hub-destination');
    expect(cards.length).toBe(1);
    expect(cards[0].getAttribute('data-destination-id')).toBe('hw-power-monitors');
    expect(screen.getByRole('link', { name: /Power Monitors/i })).toHaveAttribute(
      'href',
      '/admin/power-monitors',
    );

    // No dead-end settings card, and no false "nothing here" claim.
    expect(
      screen.queryByRole('link', { name: /Farm & Admin Settings/i }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('heading', { name: 'No operational tools available' }),
    ).not.toBeInTheDocument();
  });
});
