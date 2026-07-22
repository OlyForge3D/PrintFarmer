import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Layout } from '@/common/components/Layout';
import { getNavPreferencesStorageKey, NAV_PREFERENCES_VERSION } from '@/common/utils/navPreferences';

const createTestQueryClient = () => new QueryClient({
  defaultOptions: {
    queries: { retry: false },
    mutations: { retry: false },
  },
});

let mockUserRole = 'farm_admin';

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    user: { id: '1', email: 'admin@test.com', role: mockUserRole, isActive: true, username: 'admin' },
    logout: vi.fn(),
    isAuthenticated: true,
    hasRole: (role: string) => role === mockUserRole,
    hasPermission: () => true,
  }),
}));

vi.mock('@/hooks/useSlicer', () => ({
  useSlicer: () => ({
    isSlicerAvailable: true,
    isLoading: false,
  }),
}));

vi.mock('@/contexts/ThemeContext', () => ({
  useTheme: () => ({
    theme: 'light',
    setTheme: vi.fn(),
  }),
}));

vi.mock('@/common/hooks/useSignalR', () => ({
  useSignalRConnection: () => ({
    isConnected: true,
  }),
}));

vi.mock('@/services/printer-signalr', () => ({
  printerSignalRService: {
    connect: vi.fn().mockResolvedValue(undefined),
    onPrinterStatusUpdate: vi.fn().mockReturnValue(() => {}),
    onAutoDispatchStateChanged: vi.fn().mockReturnValue(() => {}),
  },
}));

vi.mock('@/features/tasks', () => ({
  TasksBadge: () => null,
}));

vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAllAutoDispatchStatuses: () => ({
    data: [],
    isLoading: false,
  }),
}));

describe('Navigation rail sections', () => {
  const getDesktopNav = (container: HTMLElement) => {
    const nav = container.querySelector('aside nav[aria-label="Main navigation"]');
    expect(nav).not.toBeNull();
    return nav as HTMLElement;
  };

  const renderLayout = (initialEntries = ['/']) => {
    const queryClient = createTestQueryClient();
    return render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={initialEntries}>
          <Layout />
        </MemoryRouter>
      </QueryClientProvider>,
    );
  };

  beforeEach(() => {
    mockUserRole = 'farm_admin';
    localStorage.clear();
  });

  it('renders the new left rail sections with grouped child links', async () => {
    const { container } = renderLayout();
    const desktopNav = getDesktopNav(container);

    await waitFor(() => {
      expect(desktopNav.querySelectorAll('section[aria-label]')).toHaveLength(7);
      expect(desktopNav.querySelectorAll('hr[aria-hidden="true"]')).toHaveLength(1);
    });
    expect(screen.queryByText('Dashboard', { selector: 'span.text-xs.font-semibold.uppercase.tracking-wider' })).not.toBeInTheDocument();
    expect(screen.queryByText('Admin', { selector: 'span.text-xs.font-semibold.uppercase.tracking-wider' })).not.toBeInTheDocument();

    expect(within(desktopNav).getByRole('link', { name: /overview/i })).toHaveAttribute('href', '/dashboard');
    expect(within(desktopNav).getByRole('link', { name: /print queue/i })).toHaveAttribute('href', '/printQueue');
    expect(desktopNav.querySelector('a[href="/files"]')).not.toBeNull();
    expect(desktopNav.querySelector('a[href="/projects"]')).not.toBeNull();
    expect(desktopNav.querySelector('a[href="/admin/settings"]')).not.toBeNull();
    expect(desktopNav.querySelector('a[href="/admin/manage"]')).not.toBeNull();
  });

  it('renders exactly one divider immediately before the anchored Admin group', async () => {
    const { container } = renderLayout();
    const desktopNav = getDesktopNav(container);

    await waitFor(() => {
      expect(desktopNav.querySelectorAll('hr[aria-hidden="true"]')).toHaveLength(1);
    });

    const divider = desktopNav.querySelector('hr[aria-hidden="true"]');
    expect(divider?.nextElementSibling).toHaveAttribute('aria-label', 'Admin');
    expect(within(desktopNav).getByRole('link', { name: 'Maintenance' })).toHaveAttribute('href', '/maintenance');
    expect(within(desktopNav).getByRole('link', { name: 'Admin Console' })).toHaveAttribute('href', '/admin/manage');
  });

  it('renders each default desktop nav link once', async () => {
    const { container } = renderLayout();
    const desktopNav = getDesktopNav(container);

    await waitFor(() => {
      expect(desktopNav.querySelectorAll('a[href]')).toHaveLength(15);
    });

    const hrefs = Array.from(desktopNav.querySelectorAll<HTMLAnchorElement>('a[href]')).map((link) => link.getAttribute('href'));
    expect(new Set(hrefs).size).toBe(hrefs.length);
  });

  it('keeps pinned items out of their original section and hidden items out of the main rail', async () => {
    localStorage.setItem(getNavPreferencesStorageKey('1'), JSON.stringify({
      version: NAV_PREFERENCES_VERSION,
      orderedItemIds: [],
      hiddenItemIds: ['projects'],
      pinnedItemIds: ['files'],
    }));
    const { container } = renderLayout();
    const desktopNav = getDesktopNav(container);

    await waitFor(() => {
      expect(desktopNav.querySelector('section[aria-label="Favorites"]')).not.toBeNull();
    });

    expect(desktopNav.querySelectorAll('a[href="/files"]')).toHaveLength(1);
    expect(desktopNav.querySelector('a[href="/projects"]')).toBeNull();
    expect(within(desktopNav).getByRole('button', { name: /show hidden navigation items/i })).toBeInTheDocument();
  });

  it('hides the admin section for authenticated non-admin users', () => {
    mockUserRole = 'operator';
    const { container } = renderLayout();
    const desktopNav = getDesktopNav(container);

    expect(container.querySelector('a[href="/admin/settings"]')).toBeNull();
    expect(container.querySelector('a[href="/admin/manage"]')).toBeNull();
    expect(screen.queryByText('Admin')).not.toBeInTheDocument();
    expect(desktopNav.querySelectorAll('hr[aria-hidden="true"]')).toHaveLength(0);
  });

  it('keeps the skip link and main navigation landmark available', () => {
    const { container } = renderLayout();

    expect(screen.getByRole('link', { name: /skip to main content/i })).toHaveAttribute('href', '#main-content');
    expect(screen.getAllByRole('navigation', { name: 'Main navigation' })).toHaveLength(1);
    expect(container.querySelector('aside[aria-label="Main navigation"]')).toBeNull();
  });

  it('bounds desktop and mobile navigation so nav links scroll independently of pinned controls', async () => {
    const { container } = renderLayout();
    const desktopNav = getDesktopNav(container);
    const desktopAside = container.querySelector('aside');
    const desktopShell = desktopAside?.firstElementChild;
    const desktopFooter = desktopNav.nextElementSibling;

    expect(desktopAside?.className).toContain('max-h-full');
    expect(desktopAside?.className).toContain('overflow-hidden');
    expect(desktopShell?.className).toContain('max-h-full');
    expect(desktopShell?.className).toContain('overflow-hidden');
    expect(desktopNav.className).toContain('min-h-0');
    expect(desktopNav.className).toContain('basis-0');
    expect(desktopNav.className).toContain('overflow-y-auto');
    expect(desktopNav.className).toContain('overscroll-contain');
    expect(desktopFooter?.className).toContain('shrink-0');
    expect(desktopFooter?.className).toContain('max-h-[40%]');
    expect(desktopFooter?.className).toContain('overflow-y-auto');

    const menuButton = screen.getByRole('button', { name: 'Open navigation menu' });
    fireEvent.click(menuButton);

    const mobileDrawer = await screen.findByRole('dialog', { name: 'Mobile navigation drawer' });
    const mobileNav = within(mobileDrawer).getByRole('navigation', { name: 'Main navigation' });
    expect(mobileDrawer.className).toContain('min-h-0');
    expect(mobileDrawer.className).toContain('overflow-hidden');
    expect(mobileDrawer.firstElementChild?.className).toContain('shrink-0');
    expect(mobileNav.className).toContain('min-h-0');
    expect(mobileNav.className).toContain('basis-0');
    expect(mobileNav.className).toContain('overflow-y-auto');
    expect(mobileNav.className).toContain('overscroll-contain');
  });

  it('renders full customize-nav item and section labels above the controls', () => {
    const { container } = renderLayout();
    const desktopNav = getDesktopNav(container);
    const desktopRail = desktopNav.parentElement as HTMLElement;

    fireEvent.click(within(desktopRail).getByRole('button', { name: 'Customize navigation' }));

    const customizePanel = within(desktopNav).getByRole('region', { name: 'Customize navigation' });
    const inventoryMoveUp = within(customizePanel).getByRole('button', { name: 'Move Filament Inventory up' });
    const inventoryRow = inventoryMoveUp.closest('[role="listitem"]');
    expect(inventoryRow).not.toBeNull();

    expect(within(inventoryRow as HTMLElement).getByText('Filament Inventory')).toBeInTheDocument();
    expect(within(inventoryRow as HTMLElement).getByText('Printers')).toBeInTheDocument();

    const labelBlock = within(inventoryRow as HTMLElement).getByText('Filament Inventory').parentElement;
    expect(labelBlock?.className).toContain('w-full');
    expect(labelBlock?.className).not.toContain('flex-1');
    expect(within(inventoryRow as HTMLElement).getByText('Filament Inventory').className).not.toContain('truncate');
    expect(within(inventoryRow as HTMLElement).getByText('Printers').className).not.toContain('truncate');
  });

  it('excludes anchored Admin links from customize controls while keeping them in the rail', () => {
    const { container } = renderLayout();
    const desktopNav = getDesktopNav(container);
    const desktopRail = desktopNav.parentElement as HTMLElement;

    expect(within(desktopNav).getByRole('link', { name: 'Maintenance' })).toHaveAttribute('href', '/maintenance');
    expect(within(desktopNav).getByRole('link', { name: 'System Settings' })).toHaveAttribute('href', '/admin/settings');
    expect(within(desktopNav).getByRole('link', { name: 'Admin Console' })).toHaveAttribute('href', '/admin/manage');

    fireEvent.click(within(desktopRail).getByRole('button', { name: 'Customize navigation' }));

    const customizePanel = within(desktopNav).getByRole('region', { name: 'Customize navigation' });
    expect(within(customizePanel).queryByText('Maintenance')).not.toBeInTheDocument();
    expect(within(customizePanel).queryByText('Admin Console')).not.toBeInTheDocument();
    expect(within(customizePanel).queryByRole('button', { name: 'Move Maintenance up' })).not.toBeInTheDocument();
    expect(within(customizePanel).queryByRole('button', { name: 'Move Admin Console down' })).not.toBeInTheDocument();
  });

  it('does not mark the "PrintFarmer" brand wordmark as a heading', () => {
    renderLayout();

    // The brand wordmark is branding within the banner, not a document heading.
    // Marking it <h1> (previously twice) competed with the page title for the
    // single page-level h1, so it must not appear as a heading at all.
    const brandHeadings = screen
      .queryAllByRole('heading')
      .filter((el) => el.textContent === 'PrintFarmer');
    expect(brandHeadings).toHaveLength(0);
  });

  it('stacks the mobile header above main on small screens (column), row on desktop', () => {
    const { container } = renderLayout();

    // Regression guard: the content container that holds the mobile top-header,
    // the desktop rail, and <main> must be a column on mobile and a row only at
    // lg+. Without flex-col on mobile, the mobile header becomes a horizontal
    // sibling of <main> and pushes the page content off-screen (blank on phones).
    const main = container.querySelector('#main-content');
    const contentRow = main?.parentElement;
    expect(contentRow).not.toBeNull();
    expect(contentRow!.className).toContain('flex-col');
    expect(contentRow!.className).toContain('lg:flex-row');
  });

  it('makes the mobile drawer inert when closed and moves focus into it when opened', async () => {
    const { container } = renderLayout();

    const drawerWrapper = container.querySelector('#mobile-navigation-drawer')?.parentElement;
    const mobileHeader = container.querySelector('header');
    const mainContent = container.querySelector('#main-content');
    expect(drawerWrapper).toHaveAttribute('inert');
    expect(mobileHeader).not.toHaveAttribute('inert');
    expect(mainContent).not.toHaveAttribute('inert');

    const menuButton = screen.getByRole('button', { name: 'Open navigation menu' });
    fireEvent.click(menuButton);

    expect(drawerWrapper).not.toHaveAttribute('inert');
    expect(mobileHeader).toHaveAttribute('inert');
    expect(mainContent).toHaveAttribute('inert');
    const dialog = await screen.findByRole('dialog', { name: 'Mobile navigation drawer' });
    expect(screen.getByText('Navigation menu opened.')).toBeInTheDocument();
    await waitFor(() => {
      expect(within(dialog).getByRole('button', { name: 'Close navigation menu' })).toHaveFocus();
    });

    fireEvent.keyDown(window, { key: 'Escape' });
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Mobile navigation drawer' })).not.toBeInTheDocument();
      expect(screen.getByText('Navigation menu closed.')).toBeInTheDocument();
      expect(mobileHeader).not.toHaveAttribute('inert');
      expect(mainContent).not.toHaveAttribute('inert');
    });
  });

  it('renders the collapsed rail as a flat list of nav item icon links (no popovers)', async () => {
    localStorage.setItem('pf_navbar_collapsed', 'true');
    const { container } = renderLayout();

    // Each nav item is a direct icon link in the collapsed rail — labelled by
    // its name and pointing at its own route — rather than a grouped section
    // button that opens a popover.
    const railNav = screen.getByRole('navigation', { name: 'Main navigation' });
    const filesLink = within(railNav).getByRole('link', { name: 'Files' });
    expect(filesLink).toHaveAttribute('href', '/files');
    expect(filesLink.className).toContain('h-9');
    expect(filesLink.className).toContain('w-11');
    expect(within(railNav).getByRole('link', { name: 'Projects' })).toHaveAttribute('href', '/projects');
    expect(within(railNav).getByRole('link', { name: 'Overview' })).toHaveAttribute('href', '/dashboard');
    expect(railNav.className).toContain('py-4');
    expect(container.querySelector('div[aria-label="Files"][role="group"]')?.className).toContain('space-y-0.5');

    // The grouped section button + popover dialog behavior is gone.
    expect(screen.queryByRole('button', { name: 'Files' })).not.toBeInTheDocument();
    expect(screen.queryByRole('dialog', { name: 'Files navigation' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /expand navigation rail|collapse navigation rail/i })).not.toBeInTheDocument();

    // The only group divider is the boundary before the anchored Admin section.
    await waitFor(() => {
      expect(railNav.querySelectorAll('hr[aria-hidden="true"]')).toHaveLength(1);
    });
    railNav.querySelectorAll('hr[aria-hidden="true"]').forEach((divider) => {
      expect(divider.className).toContain('mx-3');
      expect(divider.className).not.toContain('my-2');
    });
  });

  it('toggles the desktop rail when activating the active nav link and persists both directions', async () => {
    const { container } = renderLayout(['/dashboard']);
    const desktopNav = getDesktopNav(container);

    const overviewLink = within(desktopNav).getByRole('link', { name: 'Overview' });
    const printersLink = within(desktopNav).getByRole('link', { name: 'Printers' });
    const describedBy = overviewLink.getAttribute('aria-describedby');
    expect(desktopNav).not.toHaveAttribute('aria-expanded');
    expect(overviewLink).toHaveAttribute('aria-expanded', 'true');
    expect(overviewLink).toHaveAttribute('aria-current', 'page');
    expect(overviewLink).toHaveAttribute('aria-describedby', describedBy);
    expect(overviewLink).toHaveAttribute('title', 'Overview — activate again to collapse the menu');
    expect(printersLink).not.toHaveAttribute('aria-expanded');
    expect(printersLink).not.toHaveAttribute('aria-describedby');
    expect(describedBy).toBeTruthy();
    expect(document.getElementById(describedBy!)).toHaveTextContent('Activate again to collapse the menu.');

    fireEvent.click(overviewLink);

    await waitFor(() => {
      expect(localStorage.getItem('pf_navbar_collapsed')).toBe('true');
    });
    expect(screen.getByText('Navigation collapsed to icons.')).toBeInTheDocument();

    const collapsedOverviewLink = within(desktopNav).getByRole('link', { name: 'Overview' });
    expect(desktopNav).not.toHaveAttribute('aria-expanded');
    expect(collapsedOverviewLink).toHaveAttribute('aria-expanded', 'false');
    expect(document.getElementById(describedBy!)).toHaveTextContent('Activate again to expand the menu.');
    expect(collapsedOverviewLink).toHaveAttribute('title', 'Overview — activate again to expand the menu');

    fireEvent.keyDown(collapsedOverviewLink, { key: ' ' });

    await waitFor(() => {
      expect(localStorage.getItem('pf_navbar_collapsed')).toBe('false');
    });
    expect(screen.getByText('Navigation expanded.')).toBeInTheDocument();
    expect(within(desktopNav).getByRole('link', { name: 'Overview' })).toHaveAttribute('aria-expanded', 'true');
  });

  it('does not toggle the desktop rail when clicking a non-active nav link', async () => {
    const { container } = renderLayout(['/dashboard']);
    const desktopNav = getDesktopNav(container);
    const printersLink = within(desktopNav).getByRole('link', { name: 'Printers' });
    expect(printersLink).not.toHaveAttribute('aria-expanded');

    fireEvent.click(printersLink);

    await waitFor(() => {
      expect(desktopNav).not.toHaveAttribute('aria-expanded');
      expect(localStorage.getItem('pf_navbar_collapsed')).toBe('false');
      expect(printersLink).toHaveAttribute('aria-current', 'page');
      expect(printersLink).toHaveAttribute('aria-expanded', 'true');
    });
  });
});
