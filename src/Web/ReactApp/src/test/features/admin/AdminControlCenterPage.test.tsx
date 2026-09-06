import React from 'react';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { AdminControlCenterPage } from '@/features/admin/pages/AdminControlCenterPage';
import type { AdminOverviewDto } from '@/types/adminOverview';

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
        <AdminControlCenterPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

// ── Tests ────────────────────────────────────────────────────────────────────

describe('AdminControlCenterPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
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
      headings.findIndex((heading) => heading.textContent === 'System health'),
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
    expect(within(overallBadge).getByText(/System Degraded/i)).toBeInTheDocument();
    expect(within(overallBadge).queryByText(/System Healthy/i)).not.toBeInTheDocument();
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
    expect(within(overallBadge).getByText(/System Healthy/i)).toBeInTheDocument();
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
      'Nothing needs your attention — every subsystem is reporting healthy.',
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
      'No attention items were reported. Review system health below for the current status.',
    );
    expect(screen.getByTestId('admin-hub-attention-clear')).not.toHaveTextContent(
      'every subsystem is reporting healthy',
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
      'No attention items were reported. Review system health below for the current status.',
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
    expect(cards.length).toBe(8);
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
    expect(screen.queryByText('Everything you can manage')).not.toBeInTheDocument();
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
    expect(screen.queryByRole('link', { name: /Farm & Admin Settings/i })).not.toBeInTheDocument();
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
    // destinations, so the Operations band is empty for this delegate — the
    // key assertion is that no dead-end settings card is offered either.
    expect(
      screen.queryByRole('link', { name: /Farm & Admin Settings/i }),
    ).not.toBeInTheDocument();
  });
});
