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
  ShieldIcon,
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

export type SettingsDisplayGroup =
  | 'farm'
  | 'printing'
  | 'hardware'
  | 'automation'
  | 'integrations'
  | 'people'
  | 'organization'
  | 'system';

export const SETTINGS_DISPLAY_GROUPS: readonly { id: SettingsDisplayGroup; label: string }[] = [
  { id: 'farm', label: 'Farm' },
  { id: 'printing', label: 'Printing & slicing' },
  { id: 'hardware', label: 'Hardware' },
  { id: 'automation', label: 'Automation & costs' },
  { id: 'integrations', label: 'Integrations' },
  { id: 'people', label: 'People & access' },
  { id: 'organization', label: 'Organization' },
  { id: 'system', label: 'System' },
];

export interface AdminDestination {
  kind: 'hub' | 'configuration' | 'operational';
  settingsGroup?: SettingsDisplayGroup;
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
  /**
   * Optional OR-capable permission gate for destinations that bundle several
   * independently-permissioned features behind one tab (e.g. `int-connections`,
   * which surfaces Spoolman/Home Assistant/Telegram settings). Access is
   * granted when the user holds **any one** of these permissions — unlike
   * `requiredPermission`, which requires the single named grant. Mutually
   * exclusive with `requiredPermission`; if both are set, `requiredPermission`
   * is checked first and `requiredPermissionAnyOf` second (both must pass),
   * so in practice only set one of the two.
   */
  requiredPermissionAnyOf?: AdminDestinationPermission[];
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
    kind: 'hub',
    label: 'Admin Home',
    description: 'Control Center — status, alerts, and every admin destination in one place.',
    path: '/admin',
    icon: HomeIcon,
    group: 'overview',
    // Left on the default `farm_admin` role gate (#1457): this is a registry
    // *entry* used by hub-tile/palette lookups, not the `/admin` route guard
    // itself — the route (see App.tsx) no longer hard-gates on `farm_admin`,
    // so every authenticated user can still load the Control Center and see
    // whatever destinations their own permissions unlock. Leaving this entry
    // role-gated just keeps a self-referential "Admin Home" tile from
    // appearing inside the hub it points back to.
    keywords: ['admin', 'home', 'control center', 'dashboard', 'overview'],
    isHubTile: true,
  },

  // ── Operations ────────────────────────────────────────────────────────
  {
    id: 'ops-status',
    kind: 'operational',
    label: 'System Status',
    description: 'Uptime, health checks, database, and infrastructure signals.',
    path: '/admin/status',
    icon: ServerIcon,
    group: 'operations',
    // Backed by AdminOverviewController / SystemInfoController / SystemLogsController,
    // all `[RequirePermission("system_settings", "admin")]`.
    requiredRole: null,
    requiredPermission: { resource: 'system_settings', action: 'admin' },
    keywords: ['status', 'health', 'uptime', 'cpu', 'memory', 'disk', 'database', 'services', 'monitoring'],
    isHubTile: true,
  },
  {
    id: 'ops-workers',
    kind: 'operational',
    label: 'Workers & Jobs',
    description: 'Slicer workers, background jobs, and processing queues.',
    path: '/admin/workers',
    icon: DashboardIcon,
    group: 'operations',
    // Backed by the slicer module's WorkersController, `[RequirePermission("dispatch-settings:manage")]`.
    requiredRole: null,
    requiredPermission: { resource: 'dispatch-settings', action: 'manage' },
    keywords: ['workers', 'slicer', 'jobs', 'queue', 'processing', 'background'],
    isHubTile: true,
  },
  {
    id: 'ops-maintenance',
    kind: 'operational',
    label: 'Maintenance',
    description: 'Track printer maintenance schedules, tasks, and reminders.',
    path: '/maintenance',
    icon: WrenchIcon,
    group: 'operations',
    // MaintenanceController is class-level `[RequirePermission("maintenance", "admin")]`
    // for every endpoint, including reads.
    requiredRole: null,
    requiredPermission: { resource: 'maintenance', action: 'admin' },
    keywords: ['maintenance', 'schedule', 'task', 'reminder', 'service'],
    isHubTile: true,
  },
  {
    id: 'ops-analytics',
    kind: 'operational',
    label: 'Analytics',
    description: 'Production, cost, and utilization dashboards.',
    path: '/analytics',
    icon: TrendingUpIcon,
    group: 'operations',
    // Backed by JobQueueAnalyticsController, `[RequirePermission(Queue.Read)]`.
    requiredRole: null,
    requiredPermission: { resource: 'queue', action: 'read' },
    keywords: ['analytics', 'statistics', 'production', 'cost', 'utilization', 'reporting', 'metrics'],
    isHubTile: true,
  },
  {
    id: 'ops-auto-dispatch',
    kind: 'operational',
    label: 'Auto-Dispatch',
    description: 'Automated job dispatch across the farm.',
    path: '/auto-dispatch',
    icon: PlayIcon,
    group: 'operations',
    // Backed by AutoDispatchController, whose read endpoints require `[RequirePermission(Queue.Read)]`.
    requiredRole: null,
    requiredPermission: { resource: 'queue', action: 'read' },
    keywords: ['auto-dispatch', 'dispatch', 'automation', 'jobs'],
    isHubTile: true,
  },

  // ── Users ─────────────────────────────────────────────────────────────
  {
    id: 'users-accounts',
    kind: 'configuration',
    settingsGroup: 'people',
    label: 'User Accounts',
    description: 'Manage accounts, roles, and access levels.',
    path: '/admin/settings?tab=users&sub=accounts',
    icon: UsersIcon,
    group: 'users',
    // Genuinely admin-only: UsersController is class-level `[RequirePermission("users", "admin")]`.
    requiredRole: null,
    requiredPermission: { resource: 'users', action: 'admin' },
    keywords: ['user', 'account', 'role', 'permission', 'admin', 'staff'],
    isHubTile: true,
  },
  {
    id: 'users-audit',
    kind: 'operational',
    label: 'Login Audit',
    description: 'Authentication attempts and sign-in history.',
    path: '/admin/login-audit',
    icon: AlertIcon,
    group: 'users',
    // Backed by SecurityAuditController, `[RequirePermission("system_settings", "admin")]`.
    requiredRole: null,
    requiredPermission: { resource: 'system_settings', action: 'admin' },
    keywords: ['login', 'audit', 'history', 'security', 'log', 'sign-in'],
    isHubTile: true,
  },
  {
    id: 'users-roles',
    kind: 'configuration',
    settingsGroup: 'people',
    label: 'Roles & Permissions',
    description: 'Create custom roles and manage their permission grants.',
    path: '/admin/settings?tab=users&sub=roles',
    icon: ShieldIcon,
    group: 'users',
    requiredRole: null,
    requiredPermission: { resource: 'roles', action: 'admin' },
    keywords: ['role', 'permission', 'matrix', 'grant', 'deny', 'access control'],
    isHubTile: true,
  },

  // ── Data ──────────────────────────────────────────────────────────────
  {
    id: 'data-tags',
    kind: 'configuration',
    settingsGroup: 'organization',
    label: 'Tags',
    description: 'Reusable labels across models, printers, and jobs.',
    path: '/admin/settings?tab=data&sub=tags',
    icon: TagIcon,
    group: 'data',
    // Backed by TagsController, `[RequirePermission("tags", "admin")]`.
    requiredRole: null,
    requiredPermission: { resource: 'tags', action: 'admin' },
    keywords: ['tag', 'label', 'category', 'metadata'],
    isHubTile: true,
  },
  {
    id: 'data-management',
    kind: 'operational',
    label: 'Data Management',
    description: 'Export, import, backup, and cleanup workflows.',
    path: '/admin/data-management',
    icon: DatabaseIcon,
    group: 'data',
    // Backed by AdminDataController, `[RequirePermission("data_management", "admin")]`.
    requiredRole: null,
    requiredPermission: { resource: 'data_management', action: 'admin' },
    keywords: ['backup', 'export', 'import', 'cleanup', 'storage', 'restore'],
    isHubTile: true,
  },
  {
    id: 'data-catalog',
    kind: 'configuration',
    settingsGroup: 'organization',
    label: 'Catalog',
    description: 'Printer manufacturers, models, filaments, and reference data.',
    path: '/catalog',
    icon: LayersIcon,
    group: 'data',
    // Backed by CatalogController, class-level `[RequirePermission("catalog", "admin")]`.
    requiredRole: null,
    requiredPermission: { resource: 'catalog', action: 'admin' },
    keywords: ['catalog', 'manufacturer', 'model', 'filament', 'reference'],
    isHubTile: true,
  },

  // ── Hardware ──────────────────────────────────────────────────────────
  {
    id: 'hw-locations',
    kind: 'configuration',
    settingsGroup: 'hardware',
    label: 'Locations',
    description: 'Physical zones, rooms, and printer placement.',
    path: '/locations',
    icon: LocationIcon,
    group: 'hardware',
    // LocationsController gates writes on `[RequirePermission("locations", "admin")]`;
    // gate the nav entry the same way since this is the "manage" destination.
    requiredRole: null,
    requiredPermission: { resource: 'locations', action: 'admin' },
    keywords: ['location', 'zone', 'room', 'floor', 'placement', 'physical'],
    isHubTile: true,
  },
  {
    id: 'hw-printer-groups',
    kind: 'configuration',
    settingsGroup: 'hardware',
    label: 'Printer Groups',
    description: 'Organize printers into shared operational groups.',
    path: '/admin/settings?tab=hardware&sub=printer-groups',
    icon: PrinterIcon,
    group: 'hardware',
    // Backed by PrinterGroupsController, which uses the `printers` resource, not `printer_groups`.
    requiredRole: null,
    requiredPermission: { resource: 'printers', action: 'admin' },
    keywords: ['printer', 'group', 'grouping', 'cluster'],
    isHubTile: true,
  },
  {
    id: 'hw-cameras',
    kind: 'configuration',
    settingsGroup: 'hardware',
    label: 'Cameras',
    description: 'Camera feeds and monitoring views.',
    path: '/admin/settings?tab=hardware&sub=cameras',
    icon: CameraIcon,
    group: 'hardware',
    // Backed by CamerasController's manage endpoints, `[RequirePermission("cameras", "admin")]`.
    requiredRole: null,
    requiredPermission: { resource: 'cameras', action: 'admin' },
    keywords: ['camera', 'webcam', 'stream', 'video', 'monitoring'],
  },
  {
    id: 'hw-nfc',
    kind: 'configuration',
    settingsGroup: 'hardware',
    label: 'NFC Devices',
    description: 'Register and manage NFC readers and hardware.',
    path: '/admin/settings?tab=hardware&sub=nfc',
    icon: NfcIcon,
    group: 'hardware',
    // Backed by NfcDevicesController, `[RequirePermission("nfc_devices", "admin")]`.
    requiredRole: null,
    requiredPermission: { resource: 'nfc_devices', action: 'admin' },
    keywords: ['nfc', 'reader', 'rfid', 'device'],
  },
  {
    id: 'hw-nfc-bindings',
    kind: 'configuration',
    settingsGroup: 'hardware',
    label: 'NFC Bindings',
    description: 'Map NFC tags to printers, spools, and actions.',
    path: '/admin/settings?tab=hardware&sub=nfc-bindings',
    icon: NfcIcon,
    group: 'hardware',
    // Same NFC hardware surface as `hw-nfc`; gated on the same resource for consistency.
    requiredRole: null,
    requiredPermission: { resource: 'nfc_devices', action: 'admin' },
    keywords: ['nfc', 'binding', 'bind', 'tag', 'assignment'],
  },
  {
    id: 'hw-custom-fields',
    kind: 'configuration',
    settingsGroup: 'hardware',
    label: 'Custom Fields',
    description: 'Extend hardware records with custom metadata.',
    path: '/admin/settings?tab=hardware&sub=custom-fields',
    icon: LayersTripleOutlineIcon,
    group: 'hardware',
    // Backed by CustomFieldsController, `[RequirePermission("custom_fields", "admin")]`.
    requiredRole: null,
    requiredPermission: { resource: 'custom_fields', action: 'admin' },
    keywords: ['custom', 'field', 'attribute', 'metadata', 'extend'],
  },
  {
    id: 'hw-power-monitors',
    kind: 'configuration',
    settingsGroup: 'hardware',
    label: 'Power Monitors',
    description: 'Smart plug power monitors for printers and equipment.',
    path: '/admin/power-monitors',
    icon: ServerIcon,
    group: 'hardware',
    // Backed by AdminPowerMonitorsController, `[RequirePermission("power_monitors", "admin")]`.
    requiredRole: null,
    requiredPermission: { resource: 'power_monitors', action: 'admin' },
    keywords: ['power', 'monitor', 'plug', 'smart', 'energy'],
  },

  // ── Slicing ───────────────────────────────────────────────────────────
  {
    id: 'slicing-defaults',
    kind: 'configuration',
    settingsGroup: 'printing',
    label: 'Slicer Defaults',
    description: 'Farm-wide slicer defaults, process behavior, and plate settings.',
    path: '/admin/settings?tab=slicing&sub=defaults',
    icon: LayersTripleOutlineIcon,
    group: 'slicing',
    // Rendered from the generic settings-class editor backed by
    // UnifiedSettingsController, `[RequirePermission("system_settings", "admin")]`.
    requiredRole: null,
    requiredPermission: { resource: 'system_settings', action: 'admin' },
    keywords: ['slicer', 'default', 'process', 'print settings', 'nozzle', 'speed'],
    isHubTile: true,
  },
  {
    id: 'slicing-bed-types',
    kind: 'configuration',
    settingsGroup: 'printing',
    label: 'Bed Types',
    description: 'Bed surfaces and plate presets.',
    path: '/admin/settings?tab=slicing&sub=bed-types',
    icon: LayersIcon,
    group: 'slicing',
    // Backed by BedTypeController, `[RequirePermission("bed_type", "admin")]`.
    requiredRole: null,
    requiredPermission: { resource: 'bed_type', action: 'admin' },
    keywords: ['bed', 'type', 'surface', 'plate', 'build plate'],
  },
  {
    id: 'slicing-profiles',
    kind: 'configuration',
    settingsGroup: 'printing',
    label: 'Slicer Profiles',
    description: 'OrcaSlicer and PrusaSlicer profile libraries.',
    path: '/admin/settings?tab=slicing&sub=profiles',
    icon: FolderOpenIcon,
    group: 'slicing',
    // Genuinely admin-only, left on `requiredRole: 'farm_admin'` (the default): the
    // backing `Farm.Slicer.Module.Api` ProfilesController gates every mutating
    // endpoint with `[Authorize(Policy = "farm_admin")]` directly, not a resource
    // permission — there is no `slicing_profiles:admin`-shaped grant a custom role
    // could hold that the server would actually honour here. Gating the client on a
    // permission that doesn't exist server-side would show the nav entry to nobody
    // useful and mislead a custom-role user into a page that immediately 403s.
    keywords: ['profile', 'slicer', 'orcaslicer', 'prusaslicer', 'process', 'library'],
  },

  // ── Integrations ──────────────────────────────────────────────────────
  {
    id: 'int-connections',
    kind: 'configuration',
    settingsGroup: 'integrations',
    label: 'External Services',
    description: 'Spoolman, Home Assistant, Telegram, and slicer auth.',
    path: '/admin/settings?tab=integrations&sub=connections',
    icon: SettingsIcon,
    group: 'integrations',
    // Bundles three independently-permissioned integrations behind one settings
    // tab: SpoolmanController, AdminHomeAssistantController, and
    // AdminTelegramController each gate on `[RequirePermission("<resource>",
    // "admin")]` for their own resource. A custom role granted only one of the
    // three must still be able to reach this tab (#1457 — this was previously
    // left on `requiredRole: 'farm_admin'`, which reproduced the exact bug the
    // issue asks to fix). `requiredPermissionAnyOf` grants access to any one of
    // the three; the page itself still shows/hides each integration's controls
    // per-permission once loaded.
    requiredRole: null,
    requiredPermissionAnyOf: [
      { resource: 'system_settings', action: 'admin' },
      { resource: 'spoolman', action: 'admin' },
      { resource: 'home_assistant', action: 'admin' },
      { resource: 'telegram', action: 'admin' },
    ],
    keywords: ['spoolman', 'home assistant', 'telegram', 'slicer', 'octoprint', 'integration', 'external'],
    isHubTile: true,
  },
  {
    id: 'int-webhooks',
    kind: 'configuration',
    settingsGroup: 'integrations',
    label: 'Webhooks',
    description: 'Outgoing webhook endpoints for automation.',
    path: '/admin/settings?tab=integrations&sub=webhooks',
    icon: SettingsIcon,
    group: 'integrations',
    // Backed by WebhooksController, `[RequirePermission("webhooks", "admin")]`.
    requiredRole: null,
    requiredPermission: { resource: 'webhooks', action: 'admin' },
    keywords: ['webhook', 'endpoint', 'automation', 'callback'],
  },

  // ── General ───────────────────────────────────────────────────────────
  {
    id: 'gen-farm',
    kind: 'configuration',
    settingsGroup: 'farm',
    label: 'Farm Defaults',
    description: 'Farm identity, timezone, and appearance defaults.',
    path: '/admin/settings?tab=general&sub=farm',
    icon: GearIcon,
    group: 'general',
    // Rendered from the generic settings-class editor backed by
    // UnifiedSettingsController, `[RequirePermission("system_settings", "admin")]`.
    requiredRole: null,
    requiredPermission: { resource: 'system_settings', action: 'admin' },
    keywords: ['farm', 'name', 'identity', 'timezone', 'appearance', 'branding'],
    isHubTile: true,
  },
  {
    id: 'gen-system',
    kind: 'configuration',
    settingsGroup: 'system',
    label: 'System Config',
    description: 'Database, logging, network discovery, and file parameters.',
    path: '/admin/settings?tab=general&sub=system',
    icon: ServerIcon,
    group: 'general',
    // Same generic settings-class editor as `gen-farm`.
    requiredRole: null,
    requiredPermission: { resource: 'system_settings', action: 'admin' },
    keywords: ['database', 'logging', 'network', 'discovery', 'files', 'system'],
  },

  // ── Automation ────────────────────────────────────────────────────────
  {
    id: 'auto-costs',
    kind: 'configuration',
    settingsGroup: 'automation',
    label: 'Automation & Costs',
    description: 'Cost tracking, failure detection, and auto-tag rules.',
    path: '/admin/settings?tab=general&sub=automation',
    icon: KeyIcon,
    group: 'automation',
    // Same generic settings-class editor as `gen-farm`/`gen-system`.
    requiredRole: null,
    requiredPermission: { resource: 'system_settings', action: 'admin' },
    keywords: ['automation', 'cost', 'tracking', 'failure', 'obico', 'auto-tag', 'rule'],
    isHubTile: true,
  },

  // ── Quotas ────────────────────────────────────────────────────────────
  {
    id: 'quotas',
    kind: 'configuration',
    settingsGroup: 'organization',
    label: 'Quotas',
    description: 'Usage limits, allowance policies, and farm-wide constraints.',
    path: '/admin/settings?tab=quotas',
    icon: AlertIcon,
    group: 'quotas',
    // Backed by QuotaController, class-level `[RequirePermission("quota", "admin")]`.
    requiredRole: null,
    requiredPermission: { resource: 'quota', action: 'admin' },
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
 * True when the current user is allowed to reach a single destination.
 *
 * The default role is `farm_admin`; a destination with `requiredRole: null`
 * is treated as accessible to any authenticated user. Shared by
 * `filterDestinationsByAccess` (bulk filtering) and `SettingsShell`'s
 * per-tab gate (#1457), so both apply the exact same rule — including the
 * two role-only exceptions (`admin-home`, `slicing-profiles`) that have no
 * `requiredPermission`/`requiredPermissionAnyOf` at all, and `int-connections`,
 * which uses `requiredPermissionAnyOf` (any one of its three bundled
 * integration permissions unlocks the tab).
 */
export function canAccessDestination(
  destination: AdminDestination,
  access: AdminDestinationAccess,
): boolean {
  const hasPermission = access.hasPermission ?? (() => true);

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

  if (destination.requiredPermissionAnyOf
    && !destination.requiredPermissionAnyOf.some((permission) => hasPermission(permission.resource, permission.action))) {
    return false;
  }

  return true;
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
  return destinations.filter((destination) => canAccessDestination(destination, access));
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
 * True when at least one destination whose `path` starts with `pathPrefix` is
 * reachable by the current user.
 *
 * Used by `SettingsShell` (#1457) to decide whether the `system`
 * settings scopes should be offered at all — replacing a blanket
 * `hasRole('farm_admin')` scope gate with "does this user hold any permission
 * that unlocks something under this scope's path". Individual tabs within an
 * available scope are still gated on their own `requiredPermission` (see
 * `getDestinationForTab`), so this only controls whether the scope itself is
 * worth offering.
 */
export function hasAccessibleDestinationWithPrefix(
  access: AdminDestinationAccess,
  pathPrefix: string,
): boolean {
  return filterDestinationsByAccess(ADMIN_DESTINATIONS, access)
    .some((destination) => destination.path.startsWith(pathPrefix));
}

/**
 * Accessible `configuration` destinations that do **not** live inside the
 * `/admin/settings` shell, in registry order.
 *
 * The `/admin` Control Center collapses every `/admin/settings?...` destination
 * behind a single "Farm & Admin Settings" entry point, so those are deliberately
 * excluded here. A handful of configuration destinations sit outside that shell
 * (`data-catalog` → `/catalog`, `hw-locations` → `/locations`, and
 * `hw-power-monitors` → `/admin/power-monitors`) and have no other representation
 * on the dashboard.
 *
 * This exists to keep `hasAccessibleHubTile` — which lights up the Admin nav entry
 * for *any* accessible configuration destination — in step with what the hub
 * actually renders. Without it, a delegate whose sole grant is
 * `power_monitors:admin` is offered `/admin` and then shown "No operational tools
 * available", i.e. a dead end (#2508, Hicks review). `/admin/power-monitors` is the
 * sharpest case because, unlike `/catalog` and `/locations`, it is only reachable
 * through the admin surface.
 */
export function getStandaloneConfigurationDestinations(
  access: AdminDestinationAccess,
): AdminDestination[] {
  return filterDestinationsByAccess(ADMIN_DESTINATIONS, access).filter(
    (destination) =>
      destination.kind === 'configuration' &&
      !destination.path.startsWith('/admin/settings'),
  );
}

/**
 * Find the destination that backs a given `SettingsShell` tab/sub-page pair.
 *
 * Destinations under `/admin/settings` encode their tab
 * (and, when present, sub-page) as `tab=<category>&sub=<subPage>` query
 * parameters — see the path conventions documented above `ADMIN_DESTINATIONS`.
 * Returns `undefined` for categories with no matching destination (e.g. the
 * `user`-scope profile tabs, which aren't admin destinations at all).
 */
export function getDestinationForTab(categoryId: string, subPageId?: string): AdminDestination | undefined {
  return ADMIN_DESTINATIONS.find((destination) => {
    const [path, query] = destination.path.split('?');
    const params = new URLSearchParams(query);
    return path === '/admin/settings' && params.get('tab') === categoryId
      && (params.get('sub') ?? undefined) === subPageId;
  });
}

/**
 * True when the current user can reach a `SettingsShell` tab/sub-page pair.
 *
 * Looks up the backing destination via `getDestinationForTab` and applies
 * `canAccessDestination` to it. A tab/sub-page with **no** matching
 * destination (e.g. the `user`-scope profile tabs, which aren't admin
 * destinations at all) is treated as accessible — the server remains the
 * actual enforcement point for those either way.
 *
 * Used by `SettingsShell` (#1457) both to gate the rendered content of the
 * active tab AND to filter the sidebar categories / sub-tab lists it hands to
 * `SettingsSidebar`/`SettingsSubTabs`, so a partial-permission user only ever
 * sees nav entries they can actually open — not just a denial after clicking.
 */
export function canAccessSettingsTab(
  categoryId: string,
  subPageId: string | undefined,
  access: AdminDestinationAccess,
): boolean {
  const destination = getDestinationForTab(categoryId, subPageId);
  if (!destination) {
    if (categoryId === 'profile') return true;
    if (subPageId) return false;
    return ADMIN_DESTINATIONS.some((entry) => {
      const [path, query] = entry.path.split('?');
      return path === '/admin/settings' && new URLSearchParams(query).get('tab') === categoryId
        && canAccessDestination(entry, access);
    });
  }
  return canAccessDestination(destination, access);
}

/**
 * True when the current user can reach at least one hub or configuration destination —
 * i.e. the Admin Control Center page would render something for them.
 *
 * The hub renders `isHubTile` destinations regardless of their `path`
 * prefix (several, e.g. `/maintenance`, `/analytics`, `/locations`, live
 * outside `/admin` itself — see `getHubGroupedDestinations`). A path-prefix
 * check like `hasAccessibleDestinationWithPrefix(access, '/admin')` therefore
 * misses users whose only accessible hub tile is one of those: the hub page
 * itself would be genuinely useful to them, but the nav link pointing at it
 * would incorrectly hide (#1457 round-3, Bishop review — the inverse of the
 * round-2 signed-out-leak bug: usable but invisible instead of visible but
 * denied). Used by `Layout.tsx`'s Admin nav entry instead of a path-prefix
 * check.
 */
export function hasAccessibleHubTile(access: AdminDestinationAccess): boolean {
  return filterDestinationsByAccess(ADMIN_DESTINATIONS, access)
    .some((destination) => destination.isHubTile || destination.kind === 'configuration');
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
  const hubDestinations = accessible.filter((destination) => destination.isHubTile || destination.kind === 'configuration');

  return ADMIN_DESTINATION_GROUPS
    .map((group) => ({
      group,
      destinations: hubDestinations.filter((destination) => destination.group === group.id),
    }))
    .filter((entry) => entry.destinations.length > 0);
}
