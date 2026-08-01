import type { ComponentType } from 'react';

import {
  AlertIcon,
  CameraIcon,
  DashboardIcon,
  DatabaseIcon,
  FolderOpenIcon,
  GearIcon,
  HomeIcon,
  KeyIcon,
  LayersIcon,
  LayersTripleOutlineIcon,
  LocationIcon,
  NfcIcon,
  PlayIcon,
  PrinterIcon,
  ServerIcon,
  SettingsIcon,
  TagIcon,
  TrendingUpIcon,
  UsersIcon,
  WrenchIcon,
} from '@/common/components/icons/MdiIcons';

/**
 * Icon component contract used by the destination registry.
 *
 * Kept intentionally minimal (`className` only) so downstream consumers — the
 * `/admin` Control Center hub (#936) and the Ctrl+K command palette (#938) —
 * can render icons at their preferred size without depending on any layout
 * component. All icons resolved here come from `@/common/components/icons/MdiIcons`.
 */
export type AdminDestinationIcon = ComponentType<{ className?: string }>;

/**
 * Domain grouping for admin destinations.
 *
 * The hub uses these to lay out cards; the palette uses them as section headers.
 * Values are intentionally short kebab-case slugs so they can be serialized into
 * analytics events or preference stores without additional mapping.
 */
export type AdminDestinationGroup =
  | 'overview'
  | 'operations'
  | 'users'
  | 'data'
  | 'hardware'
  | 'slicing'
  | 'integrations'
  | 'general'
  | 'automation'
  | 'quotas';

export interface AdminDestinationPermission {
  resource: string;
  action: string;
}

export interface AdminDestination {
  /** Stable unique identifier. Used as React key, palette command id, hub card id. */
  id: string;
  /** Short label shown in nav, hub cards, and palette results. */
  label: string;
  /** One-sentence description surfaced in hub cards, tooltips, and palette rows. */
  description: string;
  /** Canonical route path (may include a query string) the user is sent to. */
  path: string;
  /**
   * Icon component from `@/common/components/icons/MdiIcons`.
   *
   * Consumers render at their preferred size via the `className` prop.
   */
  icon: AdminDestinationIcon;
  /** Domain group. Hub layout and palette section headers both key off this. */
  group: AdminDestinationGroup;
  /**
   * Role required to access this destination. Defaults to `'farm_admin'`.
   *
   * Set to `null` to mark a destination as reachable by any authenticated user.
   */
  requiredRole?: string | null;
  /**
   * Optional additional permission gate. Mirrors the existing `Layout.tsx` nav
   * gating semantics — checked in addition to `requiredRole`.
   */
  requiredPermission?: AdminDestinationPermission;
  /** Search terms for the Ctrl+K palette fuzzy matcher. */
  keywords?: string[];
  /**
   * Whether this destination is a top-level hub tile.
   *
   * `true` = show as a domain card on the `/admin` hub (#936).
   * `false`/undefined = deep destination, palette-only.
   */
  isHubTile?: boolean;
}

/**
 * Default role for admin destinations.
 *
 * Every destination in this file is admin-only unless explicitly overridden
 * with `requiredRole: null`.
 */
const DEFAULT_ADMIN_ROLE = 'farm_admin';

/**
 * The Admin Control Center hub itself, as a back-link target.
 *
 * Every admin destination is reached from `/admin`, so every admin page renders
 * this as its parent. Kept here, next to the destinations it is the parent of,
 * so the hub's identity has exactly one definition.
 */
export const ADMIN_HUB_PARENT = {
  label: 'Admin Control Center',
  to: '/admin',
} as const;

/**
 * Complete, ordered list of admin destinations.
 *
 * Ordering within a group matters — it drives the display order on the hub
 * (#936) and in the palette section (#938). Do not shuffle without checking
 * downstream visual snapshots.
 *
 * Path conventions:
 * - `/admin/settings?tab=X&sub=Y` — SettingsShell system scope (deep-linkable tab).
 * - `/admin/manage?tab=X&sub=Y` — SettingsShell admin scope (deep-linkable tab).
 * - `/admin/*` — standalone admin pages.
 * - Bare `/foo` paths are operational pages surfaced in admin because a farm_admin
 *   uses them daily (Locations, Catalog, Analytics, Auto-Dispatch, Maintenance).
 *
 * Contract for entries under `/admin`:
 *
 * Every such destination renders its frame through `AdminPageShell` — meaning the
 * shell owns the page's single `<h1>` and its back link to `ADMIN_HUB_PARENT`. A
 * page below `/admin` never renders its own `PageTemplate`, its own heading, or its
 * own back navigation. Pages that also stand alone on a non-admin route take an
 * `embedded` prop and suppress their own frame when the shell already drew one;
 * forgetting to pass it yields two competing `<h1>`s for one view.
 *
 * `src/test/features/admin/AdminDestinationContract.test.tsx` enforces this: it
 * walks every path in this array, asserts each resolves to a route declared in
 * `App.tsx`, and asserts exactly one `<h1>` and one link to `/admin` on each
 * shell-owned destination. Adding an entry here is what puts a page under that
 * contract, so a new destination that skips `AdminPageShell` fails the walk by name
 * rather than being discovered visually later.
 *
 * The operational `/foo` entries are deliberately outside this contract — they are
 * not below `/admin` and are reachable from elsewhere in the app, so a back link to
 * the Control Center would misdescribe where the user came from.
 */
export const ADMIN_DESTINATIONS: readonly AdminDestination[] = [
  // ── Overview ──────────────────────────────────────────────────────────
  {
    id: 'admin-home',
    label: 'Admin Home',
    description: 'Control Center — status, alerts, and every admin destination in one place.',
    path: '/admin',
    icon: HomeIcon,
    group: 'overview',
    keywords: ['admin', 'home', 'control center', 'dashboard', 'overview'],
    isHubTile: true,
  },

  // ── Operations ────────────────────────────────────────────────────────
  {
    id: 'ops-status',
    label: 'System Status',
    description: 'Uptime, health checks, database, and infrastructure signals.',
    path: '/admin/manage?tab=operations&sub=status',
    icon: ServerIcon,
    group: 'operations',
    keywords: ['status', 'health', 'uptime', 'cpu', 'memory', 'disk', 'database', 'services', 'monitoring'],
    isHubTile: true,
  },
  {
    id: 'ops-workers',
    label: 'Workers & Jobs',
    description: 'Slicer workers, background jobs, and processing queues.',
    path: '/admin/manage?tab=operations&sub=workers',
    icon: DashboardIcon,
    group: 'operations',
    keywords: ['workers', 'slicer', 'jobs', 'queue', 'processing', 'background'],
    isHubTile: true,
  },
  {
    id: 'ops-maintenance',
    label: 'Maintenance',
    description: 'Track printer maintenance schedules, tasks, and reminders.',
    path: '/maintenance',
    icon: WrenchIcon,
    group: 'operations',
    keywords: ['maintenance', 'schedule', 'task', 'reminder', 'service'],
    isHubTile: true,
  },
  {
    id: 'ops-analytics',
    label: 'Analytics',
    description: 'Production, cost, and utilization dashboards.',
    path: '/analytics',
    icon: TrendingUpIcon,
    group: 'operations',
    keywords: ['analytics', 'statistics', 'production', 'cost', 'utilization', 'reporting', 'metrics'],
    isHubTile: true,
  },
  {
    id: 'ops-auto-dispatch',
    label: 'Auto-Dispatch',
    description: 'Automated job dispatch across the farm.',
    path: '/auto-dispatch',
    icon: PlayIcon,
    group: 'operations',
    keywords: ['auto-dispatch', 'dispatch', 'automation', 'jobs'],
    isHubTile: true,
  },

  // ── Users ─────────────────────────────────────────────────────────────
  {
    id: 'users-accounts',
    label: 'User Accounts',
    description: 'Manage accounts, roles, and access levels.',
    path: '/admin/manage?tab=users&sub=accounts',
    icon: UsersIcon,
    group: 'users',
    keywords: ['user', 'account', 'role', 'permission', 'admin', 'staff'],
    isHubTile: true,
  },
  {
    id: 'users-audit',
    label: 'Login Audit',
    description: 'Authentication attempts and sign-in history.',
    path: '/admin/manage?tab=users&sub=audit',
    icon: AlertIcon,
    group: 'users',
    keywords: ['login', 'audit', 'history', 'security', 'log', 'sign-in'],
    isHubTile: true,
  },

  // ── Data ──────────────────────────────────────────────────────────────
  {
    id: 'data-tags',
    label: 'Tags',
    description: 'Reusable labels across models, printers, and jobs.',
    path: '/admin/manage?tab=data&sub=tags',
    icon: TagIcon,
    group: 'data',
    keywords: ['tag', 'label', 'category', 'metadata'],
    isHubTile: true,
  },
  {
    id: 'data-management',
    label: 'Data Management',
    description: 'Export, import, backup, and cleanup workflows.',
    path: '/admin/manage?tab=data&sub=management',
    icon: DatabaseIcon,
    group: 'data',
    keywords: ['backup', 'export', 'import', 'cleanup', 'storage', 'restore'],
    isHubTile: true,
  },
  {
    id: 'data-catalog',
    label: 'Catalog',
    description: 'Printer manufacturers, models, filaments, and reference data.',
    path: '/catalog',
    icon: LayersIcon,
    group: 'data',
    keywords: ['catalog', 'manufacturer', 'model', 'filament', 'reference'],
    isHubTile: true,
  },

  // ── Hardware ──────────────────────────────────────────────────────────
  {
    id: 'hw-locations',
    label: 'Locations',
    description: 'Physical zones, rooms, and printer placement.',
    path: '/locations/dashboard',
    icon: LocationIcon,
    group: 'hardware',
    keywords: ['location', 'zone', 'room', 'floor', 'placement', 'physical'],
    isHubTile: true,
  },
  {
    id: 'hw-printer-groups',
    label: 'Printer Groups',
    description: 'Organize printers into shared operational groups.',
    path: '/admin/settings?tab=hardware&sub=printer-groups',
    icon: PrinterIcon,
    group: 'hardware',
    keywords: ['printer', 'group', 'grouping', 'cluster'],
    isHubTile: true,
  },
  {
    id: 'hw-cameras',
    label: 'Cameras',
    description: 'Camera feeds and monitoring views.',
    path: '/admin/settings?tab=hardware&sub=cameras',
    icon: CameraIcon,
    group: 'hardware',
    keywords: ['camera', 'webcam', 'stream', 'video', 'monitoring'],
    isHubTile: true,
  },
  {
    id: 'hw-nfc',
    label: 'NFC Devices',
    description: 'Register and manage NFC readers and hardware.',
    path: '/admin/settings?tab=hardware&sub=nfc',
    icon: NfcIcon,
    group: 'hardware',
    keywords: ['nfc', 'reader', 'rfid', 'device'],
  },
  {
    id: 'hw-nfc-bindings',
    label: 'NFC Bindings',
    description: 'Map NFC tags to printers, spools, and actions.',
    path: '/admin/settings?tab=hardware&sub=nfc-bindings',
    icon: NfcIcon,
    group: 'hardware',
    keywords: ['nfc', 'binding', 'bind', 'tag', 'assignment'],
  },
  {
    id: 'hw-custom-fields',
    label: 'Custom Fields',
    description: 'Extend hardware records with custom metadata.',
    path: '/admin/settings?tab=hardware&sub=custom-fields',
    icon: LayersTripleOutlineIcon,
    group: 'hardware',
    keywords: ['custom', 'field', 'attribute', 'metadata', 'extend'],
  },
  {
    id: 'hw-power-monitors',
    label: 'Power Monitors',
    description: 'Smart plug power monitors for printers and equipment.',
    path: '/admin/power-monitors',
    icon: ServerIcon,
    group: 'hardware',
    keywords: ['power', 'monitor', 'plug', 'smart', 'energy'],
  },

  // ── Slicing ───────────────────────────────────────────────────────────
  {
    id: 'slicing-defaults',
    label: 'Slicer Defaults',
    description: 'Farm-wide slicer defaults, process behavior, and plate settings.',
    path: '/admin/settings?tab=slicing&sub=defaults',
    icon: LayersTripleOutlineIcon,
    group: 'slicing',
    keywords: ['slicer', 'default', 'process', 'print settings', 'nozzle', 'speed'],
    isHubTile: true,
  },
  {
    id: 'slicing-bed-types',
    label: 'Bed Types',
    description: 'Bed surfaces and plate presets.',
    path: '/admin/settings?tab=slicing&sub=bed-types',
    icon: LayersIcon,
    group: 'slicing',
    keywords: ['bed', 'type', 'surface', 'plate', 'build plate'],
  },
  {
    id: 'slicing-profiles',
    label: 'Slicer Profiles',
    description: 'OrcaSlicer and PrusaSlicer profile libraries.',
    path: '/admin/settings?tab=slicing&sub=profiles',
    icon: FolderOpenIcon,
    group: 'slicing',
    keywords: ['profile', 'slicer', 'orcaslicer', 'prusaslicer', 'process', 'library'],
  },

  // ── Integrations ──────────────────────────────────────────────────────
  {
    id: 'int-connections',
    label: 'External Services',
    description: 'Spoolman, Home Assistant, Telegram, and slicer auth.',
    path: '/admin/settings?tab=integrations&sub=connections',
    icon: SettingsIcon,
    group: 'integrations',
    keywords: ['spoolman', 'home assistant', 'telegram', 'slicer', 'octoprint', 'integration', 'external'],
    isHubTile: true,
  },
  {
    id: 'int-webhooks',
    label: 'Webhooks',
    description: 'Outgoing webhook endpoints for automation.',
    path: '/admin/settings?tab=integrations&sub=webhooks',
    icon: SettingsIcon,
    group: 'integrations',
    keywords: ['webhook', 'endpoint', 'automation', 'callback'],
  },

  // ── General ───────────────────────────────────────────────────────────
  {
    id: 'gen-farm',
    label: 'Farm Defaults',
    description: 'Farm identity, timezone, and appearance defaults.',
    path: '/admin/settings?tab=general&sub=farm',
    icon: GearIcon,
    group: 'general',
    keywords: ['farm', 'name', 'identity', 'timezone', 'appearance', 'branding'],
    isHubTile: true,
  },
  {
    id: 'gen-system',
    label: 'System Config',
    description: 'Database, logging, network discovery, and file parameters.',
    path: '/admin/settings?tab=general&sub=system',
    icon: ServerIcon,
    group: 'general',
    keywords: ['database', 'logging', 'network', 'discovery', 'files', 'system'],
  },

  // ── Automation ────────────────────────────────────────────────────────
  {
    id: 'auto-costs',
    label: 'Automation & Costs',
    description: 'Cost tracking, failure detection, and auto-tag rules.',
    path: '/admin/settings?tab=general&sub=automation',
    icon: KeyIcon,
    group: 'automation',
    keywords: ['automation', 'cost', 'tracking', 'failure', 'obico', 'auto-tag', 'rule'],
    isHubTile: true,
  },

  // ── Quotas ────────────────────────────────────────────────────────────
  {
    id: 'quotas',
    label: 'Quotas',
    description: 'Usage limits, allowance policies, and farm-wide constraints.',
    path: '/admin/settings?tab=quotas',
    icon: AlertIcon,
    group: 'quotas',
    keywords: ['quota', 'limit', 'allowance', 'budget', 'policy'],
    isHubTile: true,
  },
];

/**
 * Ordered list of destination groups.
 *
 * Consumers should iterate this rather than `Object.keys` on a group map so
 * ordering is stable across renders and platforms.
 */
export const ADMIN_DESTINATION_GROUPS: readonly {
  id: AdminDestinationGroup;
  label: string;
  description: string;
}[] = [
  { id: 'overview', label: 'Overview', description: 'The Control Center home.' },
  { id: 'operations', label: 'Operations', description: 'Day-to-day monitoring and workflow.' },
  { id: 'users', label: 'Users', description: 'Accounts, roles, and audit trails.' },
  { id: 'data', label: 'Data', description: 'Reference data, tags, and lifecycle tools.' },
  { id: 'hardware', label: 'Hardware', description: 'Printers, cameras, NFC, and physical layout.' },
  { id: 'slicing', label: 'Slicing', description: 'Slicer profiles, bed types, and defaults.' },
  { id: 'integrations', label: 'Integrations', description: 'External services and webhooks.' },
  { id: 'general', label: 'General', description: 'Farm identity and system configuration.' },
  { id: 'automation', label: 'Automation', description: 'Cost tracking and automated rules.' },
  { id: 'quotas', label: 'Quotas', description: 'Usage limits and policies.' },
];

/**
 * Access predicate contract callers pass to {@link filterDestinationsByAccess}.
 *
 * Mirrors the shape returned by `useAuth()` so consumers can pass their
 * `{ hasRole, hasPermission }` object directly.
 */
export interface AdminDestinationAccess {
  /**
   * True when the current user has the named role.
   *
   * Callers should pass `useAuth().hasRole`. If unavailable, pass
   * `(role) => user?.role === role`.
   */
  hasRole: (role: string) => boolean;
  /**
   * True when the current user has the named permission.
   *
   * Callers should pass `useAuth().hasPermission`. If unavailable, pass
   * `() => true` — the required-role check is usually enough.
   */
  hasPermission?: (resource: string, action: string) => boolean;
}

/**
 * Filter destinations to those the current user is allowed to reach.
 *
 * The default role is `farm_admin`; a destination with `requiredRole: null`
 * is treated as accessible to any authenticated user.
 */
export function filterDestinationsByAccess(
  destinations: readonly AdminDestination[],
  access: AdminDestinationAccess,
): AdminDestination[] {
  const hasPermission = access.hasPermission ?? (() => true);

  return destinations.filter((destination) => {
    const requiredRole = destination.requiredRole === undefined
      ? DEFAULT_ADMIN_ROLE
      : destination.requiredRole;

    if (requiredRole !== null && !access.hasRole(requiredRole)) {
      return false;
    }

    if (destination.requiredPermission
      && !hasPermission(destination.requiredPermission.resource, destination.requiredPermission.action)) {
      return false;
    }

    return true;
  });
}

/**
 * Find a single destination by its stable id.
 *
 * Returns `undefined` when no such id exists — callers should treat this as
 * a link that has been retired.
 */
export function getDestinationById(id: string): AdminDestination | undefined {
  return ADMIN_DESTINATIONS.find((destination) => destination.id === id);
}

/**
 * Get all destinations belonging to a single group, preserving registry order.
 */
export function getDestinationsByGroup(group: AdminDestinationGroup): AdminDestination[] {
  return ADMIN_DESTINATIONS.filter((destination) => destination.group === group);
}

/**
 * Get the destinations flagged as hub tiles, grouped for the Control Center.
 *
 * Each entry is a `{ group, destinations }` tuple in the canonical group order.
 * Groups with no hub-tile destinations after access filtering are omitted.
 */
export function getHubGroupedDestinations(access: AdminDestinationAccess): {
  group: (typeof ADMIN_DESTINATION_GROUPS)[number];
  destinations: AdminDestination[];
}[] {
  const accessible = filterDestinationsByAccess(ADMIN_DESTINATIONS, access);
  const hubDestinations = accessible.filter((destination) => destination.isHubTile);

  return ADMIN_DESTINATION_GROUPS
    .map((group) => ({
      group,
      destinations: hubDestinations.filter((destination) => destination.group === group.id),
    }))
    .filter((entry) => entry.destinations.length > 0);
}
