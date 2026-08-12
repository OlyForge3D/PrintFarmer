import { LoginModal } from '@/features/auth/components/LoginModal';
import { RegisterModal } from '@/features/auth/components/RegisterModal';
import { EmailConfirmationBanner } from '@/features/auth/components/EmailConfirmationBanner';
import { TasksBadge } from '@/features/tasks';
import { InstallBanner } from '@/common/components/InstallBanner';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';
import {
  HomeIcon,
  PrinterIcon,
  LayersIcon,
  SettingsIcon,
  MenuIcon,
  CloseIcon,
  GearIcon,
  ArrowUpIcon,
  ArrowDownIcon,
  EyeIcon,
  EyeOffIcon,
  FolderOpenIcon,
  HistoryIcon,
  WrenchIcon,
  TrendingUpIcon,
  AlertIcon,
  ClipboardListIcon,
  PlayIcon,
  CalendarIcon,
  LocationIcon,
  PackageIcon,
} from '@/common/components/icons/MdiIcons';
import { PrintFarmerLogoIcon } from '@/common/components/icons/PrintFarmerLogoIcon';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { hasAccessibleDestinationWithPrefix, hasAccessibleHubTile } from '@/features/admin/registry/adminDestinations';
import { useSlicer } from '@/hooks/useSlicer';
import { useSystemCapabilities } from '@/common/hooks/useSystemCapabilities';
import { hasResolvedQueryData } from '@/common/utils/queryState';
import { PlatformBanner } from '@/common/components/PlatformBanner';
import { usePrinterStatusUpdates, useSignalRConnection } from '@/common/hooks/useSignalR';
import { Fragment, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useAllAutoDispatchStatuses } from '@/features/printers/hooks/useAutoDispatch';
import { requiresBedClearConfirmation } from '@/common/utils/printerStateDisplay';
import type { AutoDispatchStatus } from '@/types/api';
import { RouteErrorBoundary } from '@/common/components/ErrorBoundary';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router';
import DebugPrinterSignalRPanel from '@/features/printers/components/DebugPrinterSignalRPanel';
import { printerSignalRService } from '@/services/printer-signalr';
import { NfcPairingModal } from '@/features/nfc/components/NfcPairingModal';
import { useNfcPairingSession } from '@/features/nfc/hooks/useNfcPairingSession';
import { FloatingControlBar } from '@/common/components/FloatingControlBar';
import { GlobalCommandPaletteProvider } from '@/features/settings/components/GlobalCommandPaletteProvider';
import { BoxIcon, SpoolIcon } from 'lucide-react';
import {
  createDefaultNavPreferences,
  getNavMoveFocusTarget,
  getNavPreferencesStorageKey,
  groupNavItemsByResolvedOrder,
  loadNavPreferences,
  moveNavItem,
  normalizeNavPreferences,
  resolveNavPreferences,
  saveNavPreferences,
  setNavItemHidden,
  setNavItemPinned,
} from '@/common/utils/navPreferences';
import type { NavPreferenceItem, NavPreferenceRole, NavPreferences } from '@/common/utils/navPreferences';
// Layout now uses <Outlet /> for nested routes

interface NavigationItem {
  id: string;
  name: string;
  href: string;
  icon: React.ComponentType<{ className?: string }>;
  requiredPermission?: { resource: string; action: string };
  requiredRole?: string;
  /**
   * Visible only if at least one destination under `href` is reachable given
   * the user's role/permissions (#1457) — used for hub-style links (like the
   * Admin Control Center) that don't correspond to a single resource
   * permission themselves but should stay hidden from a user who can't reach
   * anything underneath them.
   */
  requiresAnyAccessUnder?: string;
  /**
   * Visible only if at least one hub-tile destination (`isHubTile: true` in
   * the admin destination registry) is reachable given the user's
   * role/permissions (#1457 round-3, Bishop review). Unlike
   * `requiresAnyAccessUnder`, this does not require the destination's `path`
   * to share a prefix with `href` — several hub tiles (e.g. `/maintenance`,
   * `/analytics`, `/locations`) intentionally live outside `/admin` itself,
   * so a path-prefix check would hide the Admin nav link for a user whose
   * only accessible hub tile is one of those, even though the hub page
   * would render something useful for them.
   */
  requiresAnyAccessibleHubTile?: boolean;
  requiresSlicer?: boolean;
  /** Hide when platform-level slicing is disabled (ARM / Raspberry Pi) */
  requiresSlicingCapability?: boolean;
  /** Hide when platform-level model file support is disabled (ARM / Raspberry Pi) */
  requiresModelFiles?: boolean;
  matches?: (pathname: string) => boolean;
  anchored?: true;
  isDivider?: false;
  isSectionHeader?: false;
}

interface NavigationDivider {
  name: string;
  isDivider: true;
}

interface NavigationSectionHeader {
  name: string;
  icon: React.ComponentType<{ className?: string }>;
  isSectionHeader: true;
  requiredRole?: string;
}

interface NavigationGroup {
  header: Pick<NavigationSectionHeader, 'name' | 'icon'>;
  items: SectionedNavigationItem[];
}

type NavigationElement = NavigationItem | NavigationDivider | NavigationSectionHeader;
type SectionedNavigationItem = NavigationItem & { sectionName: string };
type MoveButtonDirection = 'up' | 'down';

const isDivider = (item: NavigationElement): item is NavigationDivider => 'isDivider' in item && item.isDivider === true;
const isSectionHeader = (item: NavigationElement): item is NavigationSectionHeader => 'isSectionHeader' in item && item.isSectionHeader === true;
const isNavigationItem = (item: NavigationElement): item is NavigationItem => !isDivider(item) && !isSectionHeader(item);

const navigation: NavigationElement[] = [
  { name: 'Dashboard', icon: HomeIcon, isSectionHeader: true },
  { id: 'overview', name: 'Overview', href: '/dashboard', icon: HomeIcon, matches: (pathname) => pathname === '/' || pathname.startsWith('/dashboard') },
  {
    id: 'print-queue',
    name: 'Print Queue',
    href: '/printQueue',
    icon: HistoryIcon,
    requiredPermission: { resource: 'printers', action: 'read' },
    matches: (pathname) => pathname.startsWith('/printQueue')
  },

  { name: 'Printers', icon: PrinterIcon, isSectionHeader: true },
  {
    id: 'printers',
    name: 'Printers',
    href: '/printers',
    icon: PrinterIcon,
    requiredPermission: { resource: 'printers', action: 'read' },
    matches: (pathname) => pathname === '/printers' || /^\/printers\/[^/]+$/.test(pathname)
  },
  {
    id: 'filament-inventory',
    name: 'Filament Inventory',
    href: '/spools',
    icon: SpoolIcon,
    matches: (pathname) => pathname.startsWith('/spools')
  },

  { name: 'Files', icon: FolderOpenIcon, isSectionHeader: true },
  {
    id: 'files',
    name: 'Files',
    href: '/files',
    icon: FolderOpenIcon,
    requiredPermission: { resource: 'models', action: 'read' },
    matches: (pathname) => pathname.startsWith('/files')
  },
  {
    id: 'projects',
    name: 'Projects',
    href: '/projects',
    icon: ClipboardListIcon,
    requiredPermission: { resource: 'models', action: 'read' },
    matches: (pathname) => pathname.startsWith('/projects')
  },
  {
    id: 'scheduling',
    name: 'Scheduling',
    href: '/scheduling',
    icon: CalendarIcon,
    matches: (pathname) => pathname.startsWith('/scheduling')
  },

  { name: 'Slicer', icon: BoxIcon, isSectionHeader: true },
  {
    id: 'slice-job',
    name: 'Slice Job',
    href: '/slicer',
    icon: BoxIcon,
    requiredPermission: { resource: 'models', action: 'read' },
    requiresSlicer: true,
    requiresSlicingCapability: true,
    matches: (pathname) => pathname.startsWith('/slicer') || pathname.startsWith('/profiles/import')
  },



  { name: 'Admin', icon: SettingsIcon, isSectionHeader: true },
  {
    id: 'maintenance',
    name: 'Maintenance',
    href: '/maintenance',
    icon: WrenchIcon,
    // MaintenanceController is class-level `[RequirePermission("maintenance", "admin")]` (#1457).
    requiredPermission: { resource: 'maintenance', action: 'admin' },
    anchored: true,
    matches: (pathname) => pathname === '/maintenance' || pathname.endsWith('/maintenance')
  },
  {
    id: 'parts-inventory',
    name: 'Printed Parts',
    href: '/parts-inventory',
    icon: PackageIcon,
    // PartsInventoryController, `[RequirePermission("parts_inventory", "admin")]` (#1457).
    requiredPermission: { resource: 'parts_inventory', action: 'admin' },
    anchored: true,
    matches: (pathname) => pathname === '/parts-inventory' || pathname.startsWith('/parts-inventory/')
  },
  {
    id: 'locations',
    name: 'Locations',
    href: '/locations',
    icon: LocationIcon,
    // LocationsController write endpoints require `[RequirePermission("locations", "admin")]` (#1457).
    requiredPermission: { resource: 'locations', action: 'admin' },
    anchored: true,
    matches: (pathname) => pathname.startsWith('/locations')
  },
  {
    id: 'analytics',
    name: 'Analytics',
    href: '/analytics',
    icon: TrendingUpIcon,
    // Backed by JobQueueAnalyticsController, `[RequirePermission(Queue.Read)]` (#1457).
    requiredPermission: { resource: 'queue', action: 'read' },
    anchored: true,
    matches: (pathname) => pathname.startsWith('/analytics')
  },
  {
    id: 'auto-dispatch',
    name: 'Auto-Dispatch',
    href: '/auto-dispatch',
    icon: PlayIcon,
    // AutoDispatchController read endpoints require `[RequirePermission(Queue.Read)]` (#1457).
    requiredPermission: { resource: 'queue', action: 'read' },
    anchored: true,
    matches: (pathname) => pathname.startsWith('/auto-dispatch')
  },
  {
    id: 'catalog',
    name: 'Catalog',
    href: '/catalog',
    icon: LayersIcon,
    // CatalogController is class-level `[RequirePermission("catalog", "admin")]` (#1457).
    requiredPermission: { resource: 'catalog', action: 'admin' },
    anchored: true,
    matches: (pathname) => pathname.startsWith('/catalog')
  },
  {
    id: 'admin',
    name: 'Admin',
    href: '/admin',
    icon: SettingsIcon,
    // The Admin Control Center hub itself is not gated on a single role or
    // resource permission (#1457) — it self-filters tiles to whatever the
    // user's own permissions unlock. But the nav *entry* should still hide
    // for a user who can't reach anything the hub would show them. Gated on
    // "any accessible hub tile" rather than "any accessible destination
    // under /admin" (round-3, Bishop review): several hub tiles live outside
    // /admin (e.g. /maintenance, /analytics, /locations), so a path-prefix
    // check hid the nav link for a user whose only accessible hub tile was
    // one of those, even though the hub page would render it for them.
    requiresAnyAccessibleHubTile: true,
    anchored: true,
    matches: (pathname) => pathname === '/admin' || pathname.startsWith('/admin/')
  },
];

const FAVORITES_HEADER: Pick<NavigationSectionHeader, 'name' | 'icon'> = { name: 'Favorites', icon: HomeIcon };
const NAVBAR_COLLAPSED_STORAGE_KEY = 'pf_navbar_collapsed';
const FOCUSABLE_SELECTOR = [
  'button:not([disabled])',
  '[href]',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(', ');

function getFocusableElements(container: HTMLElement | null): HTMLElement[] {
  if (!container) {
    return [];
  }

  return Array.from(container.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)).filter((element) => {
    if (element.hasAttribute('disabled') || element.getAttribute('aria-hidden') === 'true') {
      return false;
    }

    return element.tabIndex >= 0;
  });
}

function focusFirstElement(container: HTMLElement | null): boolean {
  const firstFocusable = getFocusableElements(container)[0];

  if (!firstFocusable) {
    return false;
  }

  firstFocusable.focus();
  return true;
}

function getSectionedNavigation(elements: NavigationElement[]): SectionedNavigationItem[] {
  const sectionedItems: SectionedNavigationItem[] = [];
  let currentSectionName = 'Navigation';

  elements.forEach((item) => {
    if (isSectionHeader(item)) {
      currentSectionName = item.name;
      return;
    }

    if (isNavigationItem(item)) {
      sectionedItems.push({ ...item, sectionName: currentSectionName });
    }
  });

  return sectionedItems;
}

const navigationItems = getSectionedNavigation(navigation);
const navigationHeadersByName = new Map(
  navigation.filter(isSectionHeader).map((header) => [header.name, { name: header.name, icon: header.icon }])
);

function toPreferenceItem(item: SectionedNavigationItem): NavPreferenceItem {
  return {
    id: item.id,
    name: item.name,
    sectionName: item.sectionName,
    anchored: item.anchored,
  };
}

function isAnchoredNavigationGroup(group: NavigationGroup) {
  return group.items.some((item) => item.anchored);
}

function groupNavigationItems(items: SectionedNavigationItem[]): NavigationGroup[] {
  return groupNavItemsByResolvedOrder(items).map((group) => ({
    header: navigationHeadersByName.get(group.sectionName) ?? { name: group.sectionName, icon: HomeIcon },
    items: group.items,
  }));
}

export function Layout() {
  const { isConnected } = useSignalRConnection('printer');
  const { user, logout, isAuthenticated, hasRole, hasPermission } = useAuth();
  const { isSlicerAvailable } = useSlicer();
  const canRole = useCallback((role: string) => typeof hasRole === 'function' ? hasRole(role) : user?.role === role, [hasRole, user?.role]);
  const canPermission = useCallback((resource: string, action: string) => typeof hasPermission === 'function' ? hasPermission(resource, action) : true, [hasPermission]);
  const { data: capabilities } = useSystemCapabilities();

  const navigate = useNavigate();
  const { data: allAutoDispatchStatuses } = useAllAutoDispatchStatuses();
  const { printerStatuses } = usePrinterStatusUpdates();
  const nfcPairingSession = useNfcPairingSession();
  const pendingAttentionCount = useMemo(
    () => ((allAutoDispatchStatuses ?? []) as AutoDispatchStatus[]).filter((status) =>
      requiresBedClearConfirmation(status, printerStatuses.get(status.printerId)?.state)
    ).length,
    [allAutoDispatchStatuses, printerStatuses]
  );
  const location = useLocation();

  // Global debug subscription to printer SignalR events (for dev verification)
  useEffect(() => {
    if (!import.meta.env.VITE_PRINTFARMER_DEBUG) return;
    let unsub: (() => void) | null = null;
    try {
      printerSignalRService.connect();
      unsub = printerSignalRService.onPrinterStatusUpdate((status) => {
        // Gate debug logging behind per-area flag so it can be enabled in-browser
        const win = window as unknown as { PrintFarmerDebug?: Record<string, unknown> };
        if (win.PrintFarmerDebug?.layout) {
          console.debug('[Layout] Received PrinterUpdated', status.id, status.state, status.isOnline);
        }
      });
    } catch (err) {
      console.warn('[Layout] Could not subscribe to printerSignalRService', err);
    }
    return () => { if (unsub) unsub(); };
  }, []);
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [userMenuOpen, setUserMenuOpen] = useState(false);
  const [showLoginModal, setShowLoginModal] = useState(false);
  const [showRegisterModal, setShowRegisterModal] = useState(false);
  const [navbarCollapsed, setNavbarCollapsed] = useState<boolean>(() => {
    const saved = localStorage.getItem(NAVBAR_COLLAPSED_STORAGE_KEY);
    return saved ? Boolean(JSON.parse(saved)) : false;
  });
  const navPreferencesStorageKey = useMemo(() => getNavPreferencesStorageKey(user?.id), [user?.id]);
  const [storedNavPreferences, setStoredNavPreferences] = useState<Partial<NavPreferences> | null>(() => loadNavPreferences(navPreferencesStorageKey));
  const [customizeNavigation, setCustomizeNavigation] = useState(false);
  const [showHiddenNavigation, setShowHiddenNavigation] = useState(false);
  const [draggingNavItemId, setDraggingNavItemId] = useState<string | null>(null);
  const desktopRailRef = useRef<HTMLDivElement | null>(null);
  const mobileDrawerAnnouncementRef = useRef<HTMLDivElement | null>(null);
  const mobileMenuButtonRef = useRef<HTMLButtonElement | null>(null);
  const mobileDrawerRef = useRef<HTMLDivElement | null>(null);
  const sidebarAnnouncementTimeoutRef = useRef<number | null>(null);
  const customizeMoveButtonRefs = useRef(new Map<string, { up: HTMLButtonElement | null; down: HTMLButtonElement | null; row: HTMLDivElement | null }>());
  const pendingCustomizeMoveFocusRef = useRef<{ itemId: string; direction: MoveButtonDirection } | null>(null);
  const previousSidebarOpenRef = useRef(false);
  const previousDrawerFocusRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    localStorage.setItem(NAVBAR_COLLAPSED_STORAGE_KEY, JSON.stringify(navbarCollapsed));
  }, [navbarCollapsed]);

  useEffect(() => {
    setStoredNavPreferences(loadNavPreferences(navPreferencesStorageKey));
  }, [navPreferencesStorageKey]);

  const navPreferenceRole = useMemo<NavPreferenceRole>(() => {
    if (!isAuthenticated) {
      return 'guest';
    }

    return canRole('farm_admin') ? 'admin' : 'operator';
  }, [canRole, isAuthenticated]);

  const availableNavigationItems = useMemo<SectionedNavigationItem[]>(() => {
    const isHiddenByCapabilities = (item: NavigationItem) => {
      if (!hasResolvedQueryData(capabilities)) {
        return item.requiresSlicingCapability || item.requiresModelFiles;
      }
      if (item.requiresSlicingCapability && capabilities?.slicingEnabled === false) return true;
      if (item.requiresModelFiles && capabilities?.modelFilesEnabled === false) return true;
      return false;
    };

    if (!isAuthenticated) {
      return navigationItems.filter((item) => {
        if (isHiddenByCapabilities(item)) return false;
        if (item.requiresSlicer && !isSlicerAvailable) return false;
        // A signed-out user can never satisfy requiresAnyAccessUnder or
        // requiresAnyAccessibleHubTile (every destination/hub tile needs a
        // role/permission), so treat them the same as
        // requiredRole/requiredPermission here. requiresAnyAccessUnder was
        // missed in the round-2 #1457 fix -- Bishop review: the Admin nav
        // link (gated only via requiresAnyAccessUnder: '/admin' at the time)
        // was visible to signed-out users.
        return (
          !item.requiredRole
          && !item.requiredPermission
          && !item.requiresAnyAccessUnder
          && !item.requiresAnyAccessibleHubTile
        );
      });
    }

    return navigationItems.filter((item) => {
      if (item.requiredRole && !canRole(item.requiredRole)) return false;
      if (item.requiredPermission && !canPermission(item.requiredPermission.resource, item.requiredPermission.action)) return false;
      if (
        item.requiresAnyAccessUnder &&
        !hasAccessibleDestinationWithPrefix({ hasRole: canRole, hasPermission: canPermission }, item.requiresAnyAccessUnder)
      ) {
        return false;
      }
      if (
        item.requiresAnyAccessibleHubTile &&
        !hasAccessibleHubTile({ hasRole: canRole, hasPermission: canPermission })
      ) {
        return false;
      }
      if (isHiddenByCapabilities(item)) return false;
      if (item.requiresSlicer && !isSlicerAvailable) return false;
      return true;
    });
  }, [isAuthenticated, canRole, canPermission, isSlicerAvailable, capabilities]);

  const navPreferenceItems = useMemo(() => availableNavigationItems.map(toPreferenceItem), [availableNavigationItems]);
  const navigationItemById = useMemo(() => new Map(availableNavigationItems.map((item) => [item.id, item])), [availableNavigationItems]);
  const resolvedNavPreferences = useMemo(
    () => resolveNavPreferences(navPreferenceItems, navPreferenceRole, storedNavPreferences),
    [navPreferenceItems, navPreferenceRole, storedNavPreferences]
  );
  const navPreferences = resolvedNavPreferences.preferences;
  const favoriteNavigationItems = useMemo(
    () => resolvedNavPreferences.favoriteItems
      .map((item) => navigationItemById.get(item.id))
      .filter((item): item is SectionedNavigationItem => Boolean(item)),
    [navigationItemById, resolvedNavPreferences.favoriteItems]
  );
  const regularNavigationItems = useMemo(
    () => resolvedNavPreferences.regularItems
      .map((item) => navigationItemById.get(item.id))
      .filter((item): item is SectionedNavigationItem => Boolean(item)),
    [navigationItemById, resolvedNavPreferences.regularItems]
  );
  const hiddenNavigationItems = useMemo(
    () => resolvedNavPreferences.hiddenItems
      .map((item) => navigationItemById.get(item.id))
      .filter((item): item is SectionedNavigationItem => Boolean(item)),
    [navigationItemById, resolvedNavPreferences.hiddenItems]
  );
  const favoriteNavigationGroups = useMemo<NavigationGroup[]>(
    () => favoriteNavigationItems.length > 0 ? [{ header: FAVORITES_HEADER, items: favoriteNavigationItems }] : [],
    [favoriteNavigationItems]
  );
  const navigationGroups = useMemo<NavigationGroup[]>(() => groupNavigationItems(regularNavigationItems), [regularNavigationItems]);
  const allNavigationGroups = useMemo<NavigationGroup[]>(
    () => [...favoriteNavigationGroups, ...navigationGroups],
    [favoriteNavigationGroups, navigationGroups]
  );

  const updateNavPreferences = useCallback((updater: (preferences: NavPreferences) => NavPreferences) => {
    setStoredNavPreferences((current) => {
      const normalized = normalizeNavPreferences(navPreferenceItems, navPreferenceRole, current);
      const next = updater(normalized);
      saveNavPreferences(navPreferencesStorageKey, next);
      return next;
    });
  }, [navPreferenceItems, navPreferenceRole, navPreferencesStorageKey]);

  const resetNavPreferences = useCallback(() => {
    const defaults = createDefaultNavPreferences(navPreferenceItems, navPreferenceRole);
    saveNavPreferences(navPreferencesStorageKey, defaults);
    setStoredNavPreferences(defaults);
    setShowHiddenNavigation(false);
  }, [navPreferenceItems, navPreferenceRole, navPreferencesStorageKey]);

  const isNavItemActive = (item: NavigationItem) => {
    if (item.matches) {
      return item.matches(location.pathname);
    }

    return location.pathname === item.href || location.pathname.startsWith(`${item.href}/`);
  };

  const handleLogout = async () => {
    await logout();
    setUserMenuOpen(false);
  };

  const announceMobileDrawer = useCallback((message: string) => {
    if (sidebarAnnouncementTimeoutRef.current !== null) {
      window.clearTimeout(sidebarAnnouncementTimeoutRef.current);
    }

    if (mobileDrawerAnnouncementRef.current) {
      mobileDrawerAnnouncementRef.current.textContent = message;
    }

    sidebarAnnouncementTimeoutRef.current = window.setTimeout(() => {
      if (mobileDrawerAnnouncementRef.current) {
        mobileDrawerAnnouncementRef.current.textContent = '';
      }
      sidebarAnnouncementTimeoutRef.current = null;
    }, 1_500);
  }, []);

  const toggleNavbarCollapsed = useCallback(() => {
    setNavbarCollapsed((prev) => {
      const next = !prev;
      announceMobileDrawer(next ? 'Navigation collapsed to icons.' : 'Navigation expanded.');
      return next;
    });
  }, [announceMobileDrawer]);

  useEffect(() => {
    if (!sidebarOpen && !userMenuOpen) {
      return;
    }

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setSidebarOpen(false);
        setUserMenuOpen(false);
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [sidebarOpen, userMenuOpen]);

  useEffect(() => {
    if (sidebarOpen) {
      previousDrawerFocusRef.current = document.activeElement instanceof HTMLElement
        ? document.activeElement
        : mobileMenuButtonRef.current;

      const frame = window.requestAnimationFrame(() => {
        if (!focusFirstElement(mobileDrawerRef.current)) {
          mobileDrawerRef.current?.focus();
        }
      });

      previousSidebarOpenRef.current = true;
      return () => window.cancelAnimationFrame(frame);
    }

    if (previousSidebarOpenRef.current) {
      announceMobileDrawer('Navigation menu closed.');

      window.requestAnimationFrame(() => {
        previousDrawerFocusRef.current?.focus();
      });
    }

    previousSidebarOpenRef.current = false;

    return undefined;
  }, [announceMobileDrawer, sidebarOpen]);

  useEffect(() => {
    if (!sidebarOpen) {
      return;
    }

    announceMobileDrawer('Navigation menu opened.');

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Tab') {
        return;
      }

      const drawer = mobileDrawerRef.current;
      const focusableElements = getFocusableElements(drawer);
      if (focusableElements.length === 0) {
        event.preventDefault();
        drawer?.focus();
        return;
      }

      const firstElement = focusableElements[0];
      const lastElement = focusableElements[focusableElements.length - 1];
      const activeElement = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      const containsFocus = activeElement ? drawer?.contains(activeElement) : false;

      if (event.shiftKey) {
        if (!containsFocus || activeElement === firstElement) {
          event.preventDefault();
          lastElement.focus();
        }
        return;
      }

      if (!containsFocus || activeElement === lastElement) {
        event.preventDefault();
        firstElement.focus();
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [announceMobileDrawer, sidebarOpen]);

  useEffect(() => () => {
    if (sidebarAnnouncementTimeoutRef.current !== null) {
      window.clearTimeout(sidebarAnnouncementTimeoutRef.current);
    }
  }, []);

  const switchToRegister = () => {
    setShowLoginModal(false);
    setShowRegisterModal(true);
  };

  const switchToLogin = () => {
    setShowRegisterModal(false);
    setShowLoginModal(true);
  };

  const desktopRailWidthClassName = navbarCollapsed ? 'lg:w-16' : 'lg:w-[248px]';
  const customizeNavigationItems = useMemo(
    () => resolvedNavPreferences.orderedItems
      .map((item) => navigationItemById.get(item.id))
      .filter((item): item is SectionedNavigationItem => Boolean(item))
      .filter((item) => !item.anchored),
    [navigationItemById, resolvedNavPreferences.orderedItems]
  );
  const hiddenNavigationIds = useMemo(() => new Set(navPreferences.hiddenItemIds), [navPreferences.hiddenItemIds]);
  const pinnedNavigationIds = useMemo(() => new Set(navPreferences.pinnedItemIds), [navPreferences.pinnedItemIds]);
  const activeToggleHintId = 'desktop-nav-active-toggle-hint';

  const reorderNavItem = useCallback((itemId: string, targetIndex: number, focusDirection?: MoveButtonDirection) => {
    const item = customizeNavigationItems.find((candidate) => candidate.id === itemId);
    const targetPosition = Math.max(1, Math.min(targetIndex + 1, customizeNavigationItems.length));
    if (focusDirection) {
      pendingCustomizeMoveFocusRef.current = { itemId, direction: focusDirection };
    }

    updateNavPreferences((preferences) => moveNavItem(preferences, itemId, targetIndex, navPreferenceItems));
    if (item) {
      announceMobileDrawer(`Moved ${item.name} to position ${targetPosition} of ${customizeNavigationItems.length}.`);
    }
  }, [announceMobileDrawer, customizeNavigationItems, navPreferenceItems, updateNavPreferences]);

  useEffect(() => {
    const pendingFocus = pendingCustomizeMoveFocusRef.current;
    if (!pendingFocus) {
      return;
    }

    const itemIndex = customizeNavigationItems.findIndex((item) => item.id === pendingFocus.itemId);
    if (itemIndex < 0) {
      pendingCustomizeMoveFocusRef.current = null;
      return;
    }

    const moveRefs = customizeMoveButtonRefs.current.get(pendingFocus.itemId);
    getNavMoveFocusTarget(moveRefs, pendingFocus.direction)?.focus();

    pendingCustomizeMoveFocusRef.current = null;
  }, [customizeNavigationItems]);

  const renderNavigationLink = (item: SectionedNavigationItem, collapsed = false, onNavigate?: () => void, enableActiveToggle = false) => {
    const ItemIcon = item.icon;
    const isActive = isNavItemActive(item);
    const activeToggleDescription = isActive && enableActiveToggle
      ? `${item.name} — activate again to ${navbarCollapsed ? 'expand' : 'collapse'} the menu`
      : undefined;
    const activeToggleHint = isActive && enableActiveToggle
      ? `Activate again to ${navbarCollapsed ? 'expand' : 'collapse'} the menu.`
      : undefined;
    const activeToggleExpanded = isActive && enableActiveToggle ? !navbarCollapsed : undefined;
    const handleClick = (event: React.MouseEvent<HTMLAnchorElement>) => {
      if (isActive && enableActiveToggle) {
        event.preventDefault();
        toggleNavbarCollapsed();
        return;
      }

      onNavigate?.();
    };
    const handleKeyDown = (event: React.KeyboardEvent<HTMLAnchorElement>) => {
      if (!isActive || !enableActiveToggle || event.key !== ' ') {
        return;
      }

      event.preventDefault();
      toggleNavbarCollapsed();
    };

    if (collapsed) {
      return (
        <NavLink
          key={item.id}
          to={item.href}
          title={activeToggleDescription ?? item.name}
          aria-label={item.name}
          aria-describedby={activeToggleHint ? activeToggleHintId : undefined}
          aria-expanded={activeToggleExpanded}
          aria-current={isActive ? 'page' : undefined}
          onClick={handleClick}
          onKeyDown={handleKeyDown}
          className={clsx(
            'flex h-9 w-11 items-center justify-center rounded-lg transition-colors focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent',
            isActive
              ? 'bg-pf-accent-bg/18 text-pf-accent'
              : 'text-pf-text-secondary hover:bg-pf-bg-2 hover:text-pf-text-primary'
          )}
        >
          <span aria-hidden="true">
            <ItemIcon className="h-5 w-5" />
          </span>
        </NavLink>
      );
    }

    return (
      <NavLink
        key={item.id}
        to={item.href}
        title={activeToggleDescription}
        aria-describedby={activeToggleHint ? activeToggleHintId : undefined}
        aria-expanded={activeToggleExpanded}
        aria-current={isActive ? 'page' : undefined}
        onClick={handleClick}
        onKeyDown={handleKeyDown}
        className={clsx(
          'group flex items-center rounded-sm border-l-3 px-3 py-2 text-sm transition-colors focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent',
          isActive
            ? 'border-pf-accent bg-pf-bg-2 font-semibold text-pf-accent'
            : 'border-transparent text-pf-text-secondary hover:bg-pf-bg-2 hover:text-pf-text-primary'
        )}
      >
        <span aria-hidden="true">
          <ItemIcon className="mr-3 h-4 w-4 shrink-0" />
        </span>
        <span className="flex-1 text-left">{item.name}</span>
      </NavLink>
    );
  };

  const renderNavigationGroups = (groups: NavigationGroup[], collapsed = false, onNavigate?: () => void, enableActiveToggle = false) => (
    <div className="space-y-1">
      {groups.map((group, groupIndex) => (
        <Fragment key={`${group.header.name}-${groupIndex}`}>
          {groupIndex > 0 && isAnchoredNavigationGroup(group) && (
            <hr className="mx-3 border-pf-border" aria-hidden="true" />
          )}
          {collapsed ? (
            <div className="flex flex-col items-center space-y-0.5" aria-label={group.header.name} role="group">
              {group.items.map((item) => renderNavigationLink(item, true, onNavigate, enableActiveToggle))}
            </div>
          ) : (
            <section aria-label={group.header.name} className="space-y-0.5">
              <div className="space-y-0.5">
                {group.items.map((item) => renderNavigationLink(item, false, onNavigate, enableActiveToggle))}
              </div>
            </section>
          )}
        </Fragment>
      ))}
    </div>
  );

  const renderHiddenNavigation = (collapsed = false, onNavigate?: () => void, enableActiveToggle = false) => {
    if (hiddenNavigationItems.length === 0) {
      return null;
    }

    return (
      <div className={clsx('border-t border-pf-border pt-2', collapsed ? 'mt-2 flex flex-col items-center gap-1' : 'mt-3 space-y-2')}>
        <Button
          type="button"
          variant="subtle"
          size="sm"
          className={clsx(collapsed ? 'h-10 w-10 px-0' : 'w-full justify-center')}
          aria-expanded={showHiddenNavigation}
          aria-label={`${showHiddenNavigation ? 'Hide' : 'Show'} hidden navigation items`}
          title={`${showHiddenNavigation ? 'Hide' : 'Show'} hidden navigation items`}
          onClick={() => setShowHiddenNavigation((prev) => !prev)}
          iconLeft={!collapsed ? (showHiddenNavigation ? <EyeOffIcon className="h-4 w-4" /> : <EyeIcon className="h-4 w-4" />) : undefined}
          iconCenter={collapsed ? (showHiddenNavigation ? <EyeOffIcon className="h-4 w-4" /> : <EyeIcon className="h-4 w-4" />) : undefined}
        >
          {!collapsed && `${showHiddenNavigation ? 'Hide' : 'Show'} hidden (${hiddenNavigationItems.length})`}
        </Button>
        {showHiddenNavigation && (
          <div className={collapsed ? 'flex flex-col items-center space-y-0.5' : 'space-y-0.5'} aria-label="Hidden navigation items" role="group">
            {hiddenNavigationItems.map((item) => renderNavigationLink(item, collapsed, onNavigate, enableActiveToggle))}
          </div>
        )}
      </div>
    );
  };

  const renderCustomizeNavigationPanel = () => {
    if (!customizeNavigation) {
      return null;
    }

    return (
      <section className="mb-3 rounded-lg border border-pf-border bg-pf-bg-2 p-2" aria-label="Customize navigation">
        <div className="mb-2 flex items-center justify-between gap-2">
          <div>
            <h2 className="text-sm font-semibold text-pf-text-primary">Customize nav</h2>
            <p className="text-xs text-pf-text-muted">Drag items or use Move up/down. Hidden items remain available under Show hidden.</p>
          </div>
          <Button type="button" variant="ghost" size="sm" onClick={() => setCustomizeNavigation(false)}>
            Done
          </Button>
        </div>
        <div className="space-y-1" role="list" aria-label="Navigation order">
          {customizeNavigationItems.map((item, index) => {
            const hidden = hiddenNavigationIds.has(item.id);
            const pinned = pinnedNavigationIds.has(item.id);

            return (
              <div
                key={item.id}
                ref={(node) => {
                  const current = customizeMoveButtonRefs.current.get(item.id) ?? { up: null, down: null, row: null };
                  current.row = node;
                  customizeMoveButtonRefs.current.set(item.id, current);
                }}
                role="listitem"
                tabIndex={-1}
                draggable
                onDragStart={() => setDraggingNavItemId(item.id)}
                onDragEnd={() => setDraggingNavItemId(null)}
                onDragOver={(event) => event.preventDefault()}
                onDrop={(event) => {
                  event.preventDefault();
                  if (draggingNavItemId && draggingNavItemId !== item.id) {
                    reorderNavItem(draggingNavItemId, index);
                  }
                  setDraggingNavItemId(null);
                }}
                className={clsx(
                  'rounded-lg border border-pf-border bg-pf-bg-1 p-2 text-xs',
                  draggingNavItemId === item.id && 'opacity-60'
                )}
              >
                <div className="flex flex-col gap-2">
                  <span className="min-w-0 w-full">
                    <span className="block break-words font-medium leading-snug text-pf-text-primary">{item.name}</span>
                    <span className="block break-words text-pf-text-muted">{item.sectionName}</span>
                  </span>
                  <div className="flex w-full items-center gap-1">
                    <Button
                      type="button"
                      variant="subtle"
                      size="sm"
                      className="h-8 w-8 px-0"
                      aria-label={`Move ${item.name} up`}
                      disabled={index === 0}
                      ref={(node) => {
                        const current = customizeMoveButtonRefs.current.get(item.id) ?? { up: null, down: null, row: null };
                        current.up = node;
                        customizeMoveButtonRefs.current.set(item.id, current);
                      }}
                      onClick={() => reorderNavItem(item.id, index - 1, 'up')}
                      iconCenter={<ArrowUpIcon className="h-4 w-4" />}
                    />
                    <Button
                      type="button"
                      variant="subtle"
                      size="sm"
                      className="h-8 w-8 px-0"
                      aria-label={`Move ${item.name} down`}
                      disabled={index === customizeNavigationItems.length - 1}
                      ref={(node) => {
                        const current = customizeMoveButtonRefs.current.get(item.id) ?? { up: null, down: null, row: null };
                        current.down = node;
                        customizeMoveButtonRefs.current.set(item.id, current);
                      }}
                      onClick={() => reorderNavItem(item.id, index + 1, 'down')}
                      iconCenter={<ArrowDownIcon className="h-4 w-4" />}
                    />
                    <Button
                      type="button"
                      variant={pinned ? 'secondary' : 'subtle'}
                      size="sm"
                      aria-pressed={pinned}
                      onClick={() => updateNavPreferences((preferences) => setNavItemPinned(preferences, item.id, !pinned))}
                    >
                      {pinned ? 'Pinned' : 'Pin'}
                    </Button>
                    <Button
                      type="button"
                      variant={hidden ? 'secondary' : 'subtle'}
                      size="sm"
                      aria-pressed={!hidden}
                      onClick={() => updateNavPreferences((preferences) => setNavItemHidden(preferences, item.id, !hidden))}
                    >
                      {hidden ? 'Show' : 'Hide'}
                    </Button>
                  </div>
                </div>
              </div>
            );
          })}
        </div>
        <Button type="button" variant="ghost" size="sm" className="mt-2 w-full justify-center" onClick={resetNavPreferences}>
          Reset to defaults
        </Button>
      </section>
    );
  };

  return (
    <div data-print-layout className="h-screen overflow-hidden bg-pf-bg-0 text-pf-text-primary">
      <a
        href="#main-content"
        className="sr-only focus:not-sr-only focus:absolute focus:left-4 focus:top-4 focus:z-[80] focus:rounded-md focus:bg-pf-bg-1 focus:px-3 focus:py-2 focus:text-sm focus:text-pf-text-primary focus:shadow-lg focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent"
      >
        Skip to main content
      </a>
      <div ref={mobileDrawerAnnouncementRef} className="sr-only" aria-live="polite" aria-atomic="true" />
      <div id={activeToggleHintId} className="sr-only">
        Activate again to {navbarCollapsed ? 'expand' : 'collapse'} the menu.
      </div>

      <div className="flex h-full min-h-0 flex-col">
        <header
          data-print-hidden
          inert={sidebarOpen || undefined}
          className="hidden shrink-0 items-center justify-between border-b border-pf-border bg-pf-bg-1 px-4 py-2 lg:flex"
        >
          <div className="flex min-w-0 items-center gap-3">
            <PrintFarmerLogoIcon decorative className="h-7 w-7 shrink-0 text-pf-accent" />
            <div className="min-w-0">
              <div className="truncate text-lg font-bold uppercase tracking-wide text-pf-text-primary font-bebas">PrintFarmer</div>
            </div>
            <span
              className={clsx('ml-1 h-2.5 w-2.5 rounded-full', isConnected ? 'bg-pf-success' : 'bg-pf-error')}
              title={isConnected ? 'Connected' : 'Disconnected'}
              aria-label={isConnected ? 'Connected' : 'Disconnected'}
            />
          </div>

          <FloatingControlBar
            mobile
            isAuthenticated={isAuthenticated}
            userName={user?.username}
            userMenuOpen={userMenuOpen}
            onToggleUserMenu={() => setUserMenuOpen((prev) => !prev)}
            onCloseUserMenu={() => setUserMenuOpen(false)}
            onViewSystemStatus={() => navigate('/admin/manage?tab=operations&sub=status')}
            onOpenPreferences={() => navigate('/settings')}
            onOpenLogin={() => setShowLoginModal(true)}
            onOpenRegister={() => setShowRegisterModal(true)}
            onLogout={handleLogout}
          />
        </header>

        <div className="flex flex-col lg:flex-row min-h-0 flex-1">
        <header
          data-print-hidden
          inert={sidebarOpen || undefined}
          className="z-40 flex h-12 shrink-0 items-center justify-between border-b border-pf-border bg-pf-bg-1 px-3 lg:hidden"
        >
          <div className="flex min-w-0 items-center gap-2">
            <Button
              ref={mobileMenuButtonRef}
              type="button"
              aria-controls="mobile-navigation-drawer"
              aria-expanded={sidebarOpen}
              aria-label={sidebarOpen ? 'Close navigation menu' : 'Open navigation menu'}
              title={sidebarOpen ? 'Close navigation menu' : 'Open navigation menu'}
              variant="subtle"
              size="sm"
              className="h-9 w-9 shrink-0 justify-center px-0"
              onClick={() => {
                setSidebarOpen(prev => !prev);
                setUserMenuOpen(false);
              }}
              iconCenter={<MenuIcon className="h-5 w-5" />}
            />
            <div className="flex min-w-0 items-center gap-2">
              <PrintFarmerLogoIcon decorative className="h-7 w-7 shrink-0 text-pf-accent" />
              <div className="min-w-0">
                <div className="truncate text-lg font-bold text-pf-text-primary font-bebas uppercase">PrintFarmer</div>
              </div>
            </div>
          </div>

          {/* `shrink-0` here (not `min-w-0`) keeps this whole cluster's flex
              box exactly as large as its rendered content, so the header's
              flex layout never shrinks it below what its children actually
              need — that mismatch was pushing the account button past the
              viewport edge even though nothing here looked clipped. All
              squeeze pressure lands on the truncating title on the left
              instead. The attention/tasks badges are secondary — if space is
              still tight after that they scroll away first (never invisibly
              clipped, since this container isn't the ancestor's
              `overflow-hidden`). The status, notification, and account
              controls in `FloatingControlBar` always stay fully visible and
              reachable, matching what issue #1417 calls out explicitly. */}
          <div className="flex shrink-0 items-center gap-1">
            <div className="flex min-w-0 items-center gap-1 overflow-x-auto [scrollbar-width:none] [-ms-overflow-style:none] [&::-webkit-scrollbar]:hidden">
              {pendingAttentionCount > 0 && (
                <Button
                  type="button"
                  variant="unstyled"
                  onClick={() => navigate('/printers?view=collapsed')}
                  className="relative flex h-9 w-9 shrink-0 items-center justify-center rounded-md text-pf-warning transition-colors hover:bg-pf-bg-2 focus-visible:ring-2 focus-visible:ring-pf-accent"
                  title={`${pendingAttentionCount} printer${pendingAttentionCount !== 1 ? 's' : ''} need${pendingAttentionCount === 1 ? 's' : ''} attention — click to view`}
                  aria-label={`${pendingAttentionCount} printer${pendingAttentionCount !== 1 ? 's' : ''} need${pendingAttentionCount === 1 ? 's' : ''} attention`}
                >
                  <AlertIcon className="h-4 w-4" />
                  {/* Count badge: `h-4 min-w-4` is a circle at one digit and a
                      pill at two, which is the intended shape at both. */}
                  <span data-pf-radius="full" className="absolute right-1 top-1 flex h-4 min-w-4 items-center justify-center rounded-full bg-pf-warning px-1 text-[9px] font-bold leading-none text-black">
                    {pendingAttentionCount}
                  </span>
                </Button>
              )}

              {isAuthenticated && <TasksBadge />}
            </div>
            <FloatingControlBar
              mobile
              compact
              isAuthenticated={isAuthenticated}
              userName={user?.username}
              userMenuOpen={userMenuOpen}
              onToggleUserMenu={() => setUserMenuOpen((prev) => !prev)}
              onCloseUserMenu={() => setUserMenuOpen(false)}
              onViewSystemStatus={() => navigate('/admin/manage?tab=operations&sub=status')}
              onOpenPreferences={() => navigate('/settings')}
              onOpenLogin={() => setShowLoginModal(true)}
              onOpenRegister={() => setShowRegisterModal(true)}
              onLogout={handleLogout}
            />
          </div>
        </header>

        <div
          data-print-hidden
          className={clsx(
            'fixed inset-x-0 top-12 bottom-0 z-50 lg:hidden',
            sidebarOpen ? 'pointer-events-auto' : 'pointer-events-none'
          )}
          aria-hidden={!sidebarOpen}
          inert={!sidebarOpen}
        >
          <div
            className={clsx(
              'absolute inset-0 bg-black/60 transition-opacity duration-200',
              sidebarOpen ? 'opacity-100' : 'opacity-0'
            )}
            onClick={() => setSidebarOpen(false)}
          />

          <div
            ref={mobileDrawerRef}
            id="mobile-navigation-drawer"
            role="dialog"
            aria-modal="true"
            aria-label="Mobile navigation drawer"
            tabIndex={-1}
            className={clsx(
              'relative flex h-full min-h-0 w-[248px] max-w-[calc(100vw-1rem)] flex-col overflow-hidden border-r border-pf-border bg-pf-bg-1 shadow-2xl transition-transform duration-200 ease-out',
              sidebarOpen ? 'translate-x-0' : '-translate-x-full'
            )}
          >
            <div className="flex shrink-0 items-center justify-between border-b border-pf-border px-4 py-3">
              <div className="flex min-w-0 items-center gap-3">
                <PrintFarmerLogoIcon decorative className="h-7 w-7 text-pf-accent" />
                <div className="min-w-0">
                  <div className="truncate text-base font-semibold text-pf-text-primary font-bebas uppercase">PrintFarmer</div>
                </div>
              </div>
              <Button
                type="button"
                variant="subtle"
                size="sm"
                className="h-9 w-9 justify-center px-0"
                aria-label="Close navigation menu"
                onClick={() => setSidebarOpen(false)}
                iconCenter={<CloseIcon className="h-5 w-5" />}
              />
            </div>

            <nav className="min-h-0 flex-1 basis-0 space-y-1 overflow-y-auto overscroll-contain px-3 py-3" aria-label="Main navigation">
              <div className="mb-3 flex items-center gap-2">
                <Button
                  type="button"
                  variant={customizeNavigation ? 'secondary' : 'subtle'}
                  size="sm"
                  className="w-full justify-center"
                  aria-pressed={customizeNavigation}
                  onClick={() => setCustomizeNavigation((prev) => !prev)}
                  iconLeft={<GearIcon className="h-4 w-4" />}
                >
                  Customize
                </Button>
              </div>
              {renderCustomizeNavigationPanel()}
              {renderNavigationGroups(allNavigationGroups, false, () => setSidebarOpen(false))}
              {renderHiddenNavigation(false, () => setSidebarOpen(false))}
            </nav>
          </div>
        </div>

        <aside
          data-print-hidden
          ref={desktopRailRef}
          className={clsx('hidden h-full max-h-full min-h-0 overflow-hidden border-r border-pf-border bg-pf-bg-1 shadow-[12px_0_32px_rgba(0,0,0,0.16)] lg:flex lg:shrink-0', desktopRailWidthClassName)}
        >
          <div className="flex h-full max-h-full min-h-0 w-full flex-col overflow-hidden">
            <nav
              className={clsx('relative min-h-0 flex-1 basis-0 overflow-y-auto overscroll-contain py-4', navbarCollapsed ? 'px-2' : 'px-3')}
              aria-label="Main navigation"
            >
              {navbarCollapsed ? (
                <>
                  {renderNavigationGroups(allNavigationGroups, true, undefined, true)}
                  {renderHiddenNavigation(true, undefined, true)}
                </>
              ) : (
                <>
                  {renderCustomizeNavigationPanel()}
                  {renderNavigationGroups(allNavigationGroups, false, undefined, true)}
                  {renderHiddenNavigation(false, undefined, true)}
                </>
              )}
            </nav>

            <div className="max-h-[40%] shrink-0 overflow-y-auto overscroll-contain border-t border-pf-border p-2">
              {!navbarCollapsed && pendingAttentionCount > 0 && (
                <div className="mb-2 flex items-center justify-end rounded-lg border border-pf-border bg-pf-bg-2 px-3 py-2">
                  <Button
                    type="button"
                    variant="unstyled"
                    onClick={() => navigate('/printers?view=collapsed')}
                    className="relative flex h-8 w-8 items-center justify-center rounded-md text-pf-warning transition-colors hover:bg-pf-bg-1 focus-visible:ring-2 focus-visible:ring-pf-accent"
                    title={`${pendingAttentionCount} printer${pendingAttentionCount !== 1 ? 's' : ''} need${pendingAttentionCount === 1 ? 's' : ''} attention — click to view`}
                    aria-label={`${pendingAttentionCount} printer${pendingAttentionCount !== 1 ? 's' : ''} need${pendingAttentionCount === 1 ? 's' : ''} attention`}
                  >
                    <AlertIcon className="h-4 w-4" />
                    {/* Count badge: circle at one digit, pill at two. */}
                    <span data-pf-radius="full" className="absolute right-0.5 top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-pf-warning px-1 text-[9px] font-bold leading-none text-black">
                      {pendingAttentionCount}
                    </span>
                  </Button>
                </div>
              )}

              <div className={clsx('flex items-center gap-2', navbarCollapsed ? 'flex-col' : 'justify-between')}>
                <div className={clsx('flex items-center gap-2', navbarCollapsed && 'flex-col')}>
                  {isAuthenticated && <TasksBadge />}
                </div>

                <div className="flex items-center gap-2">
                  {navbarCollapsed && pendingAttentionCount > 0 && (
                    <Button
                      type="button"
                      variant="unstyled"
                      onClick={() => navigate('/printers?view=collapsed')}
                      className="relative flex h-9 w-9 items-center justify-center rounded-md text-pf-warning transition-colors hover:bg-pf-bg-2 focus-visible:ring-2 focus-visible:ring-pf-accent"
                      aria-label={`${pendingAttentionCount} printer${pendingAttentionCount !== 1 ? 's' : ''} need${pendingAttentionCount === 1 ? 's' : ''} attention`}
                      iconCenter={<AlertIcon className="h-4 w-4" />}
                    >
                      <span data-pf-radius="full" className="absolute right-0.5 top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-pf-warning px-1 text-[9px] font-bold leading-none text-black">
                        {pendingAttentionCount}
                      </span>
                    </Button>
                  )}
                </div>
              </div>

              <div className="mt-2">
                <Button
                  type="button"
                  aria-label={customizeNavigation ? 'Finish customizing navigation' : 'Customize navigation'}
                  title={customizeNavigation ? 'Finish customizing navigation' : 'Customize navigation'}
                  variant={customizeNavigation ? 'secondary' : 'subtle'}
                  size="sm"
                  className="mb-2 w-full justify-center"
                  aria-pressed={customizeNavigation}
                  onClick={() => {
                    setCustomizeNavigation((prev) => !prev);
                    setNavbarCollapsed(false);
                  }}
                  iconCenter={navbarCollapsed ? <GearIcon className="h-5 w-5" /> : undefined}
                  iconLeft={!navbarCollapsed ? <GearIcon className="h-4 w-4" /> : undefined}
                >
                  {!navbarCollapsed && (customizeNavigation ? 'Done customizing' : 'Customize')}
                </Button>
              </div>
            </div>
          </div>
        </aside>

        <main
          id="main-content"
          data-main-content
          inert={sidebarOpen || undefined}
          tabIndex={-1}
          className="flex-1 min-h-0 overflow-x-hidden overflow-y-auto bg-pf-bg-0 focus:outline-hidden lg:h-full has-[[data-page-fill]]:flex has-[[data-page-fill]]:flex-col"
        >
          <EmailConfirmationBanner />
          <PlatformBanner />
          <InstallBanner />
          <div className="px-1 pt-1 pb-2 lg:px-2 lg:pt-2 lg:pb-2 has-[[data-page-fill]]:flex has-[[data-page-fill]]:min-h-0 has-[[data-page-fill]]:flex-1 has-[[data-page-fill]]:flex-col">
            {/* React Router's `location.key` is a unique string generated
                per history entry. It changes on ANY navigation — including
                same-pathname but different search or hash — where
                `pathname` alone would not. Using `pathname` as the reset
                key would leave a stuck error boundary on
                `/reports?range=week` when the operator navigates to
                `/reports?range=day` or `/reports#summary` because the
                pathname is identical. Reviewers explicitly flagged this
                (Hicks #5): tests must exercise real router navigation
                between same-path/different-query and same-path/different-
                hash. `location.key` covers both. See:
                https://reactrouter.com/en/main/hooks/use-location */}
            <RouteErrorBoundary resetKey={location.key}>
              <Suspense
                fallback={
                  <div className="flex min-h-[40vh] items-center justify-center" role="status" aria-label="Loading page">
                    <div className="h-8 w-8 rounded-full border-b-2 border-pf-accent pf-animate-spin"></div>
                  </div>
                }
              >
                <GlobalCommandPaletteProvider>
                  <Outlet />
                </GlobalCommandPaletteProvider>
              </Suspense>
            </RouteErrorBoundary>
          </div>
        </main>
        </div>
      </div>

      {userMenuOpen && (
        <div
          data-print-hidden
          className="fixed inset-0 z-10 pointer-events-auto lg:z-30"
          onClick={() => setUserMenuOpen(false)}
          aria-hidden="true"
        />
      )}

      <LoginModal
        isOpen={showLoginModal}
        onClose={() => setShowLoginModal(false)}
        onSwitchToRegister={switchToRegister}
      />
      <RegisterModal
        isOpen={showRegisterModal}
        onClose={() => setShowRegisterModal(false)}
        onSwitchToLogin={switchToLogin}
      />
      {import.meta.env.VITE_PRINTFARMER_DEBUG && <DebugPrinterSignalRPanel />}
      <NfcPairingModal session={nfcPairingSession} />
    </div>
  );
}

// Footer with build info now moved out of header for persistent display
