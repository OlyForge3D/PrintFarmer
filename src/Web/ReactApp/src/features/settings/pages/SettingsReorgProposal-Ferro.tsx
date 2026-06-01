/* eslint-disable local/pf-no-raw-html-controls -- tab/zone rails use raw <button> with managed focus, matching SettingsSidebar.tsx */
import { useState } from 'react';
import clsx from 'clsx';
import { Badge, Card } from '@/common/components/ui';
import {
  HomeIcon,
  PrinterIcon,
  FolderOpenIcon,
  WrenchIcon,
  TrendingUpIcon,
  ChartIcon,
  KeyIcon,
  NfcIcon,
  GearIcon,
  UsersIcon,
  DatabaseIcon,
  CalendarIcon,
  ServerIcon,
  ActivityIcon,
  CheckCircleIcon,
  AlertIcon,
  InfoIcon,
  ChevronRightIcon,
} from '@/common/components/icons/MdiIcons';

/**
 * SettingsReorgProposal-Ferro
 * ============================
 * Ferro's COMPETING proof-of-concept for the Settings + Analytics reorganization.
 *
 * This is a static design mockup (no routing, no API). All data is mock data.
 * It renders four panels that you can switch between using the segmented control
 * at the top: "Mental Model", "Insights Hub", "System Pulse", and "Navigation".
 *
 * ─────────────────────────────────────────────────────────────────────────────
 * FERRO'S THESIS — diverging from the "just add more Settings tabs" approach:
 *
 *   1. Settings should be organized by USER JOURNEY ("what am I trying to do?"),
 *      not by technical domain ("which subsystem owns this table?"). Newt's likely
 *      approach folds the orphans into the existing 7 feature-typed categories.
 *      I instead collapse to 4 intent-based zones: Workspace, Connectivity,
 *      Governance, and Platform.
 *
 *   2. Analytics is not a set of sibling pages — it's ONE destination with a
 *      dashboard overview that progressively drills into detail. Tabs hide the
 *      cross-cutting story; a dashboard surfaces it.
 *
 *   3. "Printer Groups" is an ORGANIZATION concept, not a setting. It belongs to
 *      the operational data model (alongside Catalog), surfaced contextually —
 *      NOT buried in a settings form the admin visits once a quarter.
 *
 *   4. System Status is a PERSISTENT health widget (a "System Pulse" pill in the
 *      top bar that expands into a popover), not a page you have to navigate to.
 *      Health should be ambient, not hunted for.
 * ─────────────────────────────────────────────────────────────────────────────
 */

// ── Mock data ────────────────────────────────────────────────────────────────

type Health = 'healthy' | 'warning' | 'critical';

interface ResourceMetric {
  id: string;
  label: string;
  value: number; // percent 0–100
  detail: string;
  health: Health;
}

interface ServiceVersion {
  id: string;
  name: string;
  version: string;
  health: Health;
  detail: string;
}

interface AnalyticsKpi {
  id: string;
  label: string;
  value: string;
  delta: string;
  trend: 'up' | 'down' | 'flat';
  /** which of the 3 legacy pages this KPI was rescued from */
  source: 'Statistics' | 'Cost Analytics' | 'Analytics';
}

const RESOURCE_METRICS: ResourceMetric[] = [
  { id: 'cpu', label: 'CPU', value: 38, detail: '8 cores · load 3.1', health: 'healthy' },
  { id: 'memory', label: 'Memory', value: 71, detail: '11.4 / 16 GB', health: 'warning' },
  { id: 'disk', label: 'Disk', value: 54, detail: '432 / 800 GB', health: 'healthy' },
];

const SERVICE_VERSIONS: ServiceVersion[] = [
  { id: 'frontend', name: 'Frontend (React)', version: 'v2.14.0', health: 'healthy', detail: 'Build 2026.06.01' },
  { id: 'backend', name: 'Backend API', version: 'v2.14.0', health: 'healthy', detail: '.NET 10 · up 6d' },
  { id: 'slicer', name: 'Slicer Host', version: 'v2.13.2', health: 'warning', detail: 'OrcaSlicer 2.1.1 · 1 worker idle' },
  { id: 'signalr', name: 'Realtime (SignalR)', version: 'v2.14.0', health: 'healthy', detail: '42 live connections' },
];

const ANALYTICS_KPIS: AnalyticsKpi[] = [
  { id: 'jobs', label: 'Jobs Completed', value: '1,284', delta: '+12%', trend: 'up', source: 'Statistics' },
  { id: 'success', label: 'Success Rate', value: '94.2%', delta: '+1.8%', trend: 'up', source: 'Statistics' },
  { id: 'cost', label: 'Cost / Print', value: '$1.37', delta: '-6%', trend: 'down', source: 'Cost Analytics' },
  { id: 'filament', label: 'Filament Spend', value: '$842', delta: '+4%', trend: 'up', source: 'Cost Analytics' },
  { id: 'utilization', label: 'Fleet Utilization', value: '68%', delta: '+5%', trend: 'up', source: 'Analytics' },
  { id: 'throughput', label: 'Avg Throughput', value: '17 / day', delta: 'flat', trend: 'flat', source: 'Analytics' },
];

// ── Proposed intent-based Settings model ─────────────────────────────────────
// Each "zone" answers a user question rather than naming a subsystem.

interface SettingsZone {
  id: string;
  label: string;
  /** The user intent this zone answers — shown as the zone's subtitle. */
  intent: string;
  icon: React.ReactNode;
  groups: {
    label: string;
    items: string[];
    /** marks items relocated from a standalone nav link (the "orphans") */
    rescuedFromNav?: string[];
  }[];
}

const SETTINGS_ZONES: SettingsZone[] = [
  {
    id: 'workspace',
    label: 'Workspace',
    intent: '“Set up how my farm prints.”',
    icon: <GearIcon className="w-5 h-5" />,
    groups: [
      { label: 'Farm Identity', items: ['Name & timezone', 'Appearance / theme', 'Locations'] },
      { label: 'Slicing', items: ['Bed types', 'Slicer profiles'] },
      { label: 'Hardware', items: ['Cameras', 'Custom fields'] },
    ],
  },
  {
    id: 'connectivity',
    label: 'Connectivity',
    intent: '“Connect my farm to devices & the outside world.”',
    icon: <NfcIcon className="w-5 h-5" />,
    // RATIONALE: NFC Bindings + API Keys + Webhooks are all "how PrintFarmer talks
    // to other things." Grouping by the *connection* mental model — not by whether
    // it's hardware vs integration — is far more discoverable than splitting NFC
    // under Hardware and API Keys under Users.
    groups: [
      {
        label: 'Devices',
        items: ['NFC readers', 'NFC bindings'],
        rescuedFromNav: ['NFC Bindings'],
      },
      {
        label: 'Programmatic Access',
        items: ['API keys', 'Webhooks'],
        rescuedFromNav: ['API Keys'],
      },
      { label: 'Notifications', items: ['Email / push', 'Discord / Telegram'] },
    ],
  },
  {
    id: 'governance',
    label: 'Governance',
    intent: '“Control who can do what, and keep the data tidy.”',
    icon: <UsersIcon className="w-5 h-5" />,
    groups: [
      { label: 'People', items: ['User accounts', 'Roles & permissions', 'Login audit'] },
      { label: 'Data Stewardship', items: ['Tags', 'Quotas', 'Backup / export / cleanup'] },
    ],
  },
  {
    id: 'platform',
    label: 'Platform',
    intent: '“Operate the server itself.”',
    icon: <ServerIcon className="w-5 h-5" />,
    // RATIONALE: System status, worker management, and update channels are
    // operator concerns distinct from configuring print behavior. Splitting them
    // into their own zone keeps everyday config uncluttered (progressive disclosure).
    groups: [
      { label: 'Health', items: ['System Pulse (full view)', 'Service versions', 'Diagnostics & logs'] },
      { label: 'Slicing Infrastructure', items: ['Workers', 'Update channel'] },
    ],
  },
];

// ── Proposed navigation ──────────────────────────────────────────────────────
// Note the renamed groups and the removed orphans (now inside Settings) and the
// single "Insights" link replacing three analytics links.

interface NavGroup {
  header: string;
  items: { label: string; icon: React.ReactNode; note?: string }[];
}

const PROPOSED_NAV: NavGroup[] = [
  {
    header: 'Operate',
    items: [
      { label: 'Dashboard', icon: <HomeIcon className="w-4 h-4" /> },
      { label: 'Printers', icon: <PrinterIcon className="w-4 h-4" /> },
      { label: 'Files', icon: <FolderOpenIcon className="w-4 h-4" /> },
      { label: 'Projects', icon: <FolderOpenIcon className="w-4 h-4" /> },
      { label: 'Slice', icon: <WrenchIcon className="w-4 h-4" /> },
      { label: 'Print Queue', icon: <CalendarIcon className="w-4 h-4" /> },
      { label: 'Auto-Dispatch', icon: <ActivityIcon className="w-4 h-4" /> },
    ],
  },
  {
    header: 'Inventory',
    items: [
      // Filament Inventory deliberately stays a top-level link (explicit user directive).
      { label: 'Filament Inventory', icon: <DatabaseIcon className="w-4 h-4" />, note: 'stays top-level (directive)' },
    ],
  },
  {
    header: 'Insights',
    items: [
      // Single destination replaces Statistics + Cost Analytics + Analytics.
      { label: 'Insights Hub', icon: <ChartIcon className="w-4 h-4" />, note: 'merges 3 old links' },
      { label: 'Maintenance', icon: <WrenchIcon className="w-4 h-4" /> },
      { label: 'Scheduling', icon: <CalendarIcon className="w-4 h-4" /> },
    ],
  },
  {
    header: 'Organization',
    items: [
      // Printer Groups reframed as an organization concept, sitting beside Catalog —
      // NOT inside Settings. It's operational structure, used while working, not config.
      { label: 'Printer Groups', icon: <PrinterIcon className="w-4 h-4" />, note: 'moved out of Admin, kept operational' },
      { label: 'Catalog', icon: <DatabaseIcon className="w-4 h-4" /> },
    ],
  },
  {
    header: 'Admin',
    items: [
      // Workers, System, API Keys all absorbed into Settings zones (Platform / Connectivity).
      { label: 'Settings', icon: <GearIcon className="w-4 h-4" />, note: 'now hosts API Keys, NFC Bindings, Workers, System' },
    ],
  },
];

// ── Shared helpers ───────────────────────────────────────────────────────────

const HEALTH_BADGE: Record<Health, { variant: 'success' | 'warning' | 'error'; label: string }> = {
  healthy: { variant: 'success', label: 'Healthy' },
  warning: { variant: 'warning', label: 'Degraded' },
  critical: { variant: 'error', label: 'Critical' },
};

function healthIcon(health: Health) {
  if (health === 'healthy') return <CheckCircleIcon className="w-4 h-4 text-pf-success" ariaLabel="Healthy" />;
  if (health === 'warning') return <AlertIcon className="w-4 h-4 text-pf-warning" ariaLabel="Degraded" />;
  return <AlertIcon className="w-4 h-4 text-pf-error" ariaLabel="Critical" />;
}

function meterColor(health: Health) {
  if (health === 'healthy') return 'bg-pf-success';
  if (health === 'warning') return 'bg-pf-warning';
  return 'bg-pf-error';
}

// ── Panel: Mental Model (intent-based Settings zones) ────────────────────────

function MentalModelPanel() {
  const [activeZone, setActiveZone] = useState(SETTINGS_ZONES[0].id);
  const zone = SETTINGS_ZONES.find((z) => z.id === activeZone) ?? SETTINGS_ZONES[0];

  return (
    <div className="space-y-4">
      <p className="text-sm text-pf-text-secondary">
        Four intent-based zones replace seven feature-typed categories. The sidebar still uses the existing
        vertical-rail + content pattern, so this is a re-labeling of structure, not a new component.
      </p>

      <div className="flex flex-col md:flex-row min-h-[420px] border border-pf-border rounded-lg overflow-hidden bg-pf-bg-0">
        {/* Zone rail — reuses the SettingsSidebar visual language */}
        <nav className="md:w-64 shrink-0 border-b md:border-b-0 md:border-r border-pf-border" aria-label="Settings zones">
          <ul className="py-2">
            {SETTINGS_ZONES.map((z) => {
              const isActive = z.id === activeZone;
              return (
                <li key={z.id}>
                  <button
                    type="button"
                    onClick={() => setActiveZone(z.id)}
                    aria-current={isActive ? 'page' : undefined}
                    className={clsx(
                      'w-full flex items-start gap-3 px-4 py-3 text-left transition-colors',
                      'focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-inset',
                      isActive
                        ? 'bg-pf-accent-bg text-pf-text-primary border-l-2 border-pf-accent'
                        : 'text-pf-text-secondary hover:bg-pf-bg-1 hover:text-pf-text-primary border-l-2 border-transparent'
                    )}
                  >
                    <span className="shrink-0 mt-0.5" aria-hidden="true">{z.icon}</span>
                    <span className="min-w-0">
                      <span className="block text-sm font-medium">{z.label}</span>
                      <span className="block text-xs text-pf-text-secondary mt-0.5">{z.intent}</span>
                    </span>
                  </button>
                </li>
              );
            })}
          </ul>
        </nav>

        {/* Zone content — grouped cards instead of a flat sub-tab strip */}
        <div className="flex-1 p-4 md:p-6">
          <div className="flex items-center gap-2 mb-1">
            <span aria-hidden="true">{zone.icon}</span>
            <h3 className="text-lg font-semibold text-pf-text-primary">{zone.label}</h3>
          </div>
          <p className="text-sm text-pf-text-secondary mb-4">{zone.intent}</p>

          <div className="grid gap-4 sm:grid-cols-2">
            {zone.groups.map((group) => (
              <Card key={group.label}>
                <Card.Body>
                  <h4 className="text-sm font-semibold text-pf-text-primary mb-2">{group.label}</h4>
                  <ul className="space-y-1.5">
                    {group.items.map((item) => {
                      const rescued = group.rescuedFromNav?.includes(item);
                      return (
                        <li key={item} className="flex items-center justify-between gap-2 text-sm text-pf-text-secondary">
                          <span className="flex items-center gap-2">
                            <ChevronRightIcon className="w-3.5 h-3.5 text-pf-text-secondary" />
                            {item}
                          </span>
                          {rescued ? <Badge variant="primary" size="sm">moved from nav</Badge> : null}
                        </li>
                      );
                    })}
                  </ul>
                </Card.Body>
              </Card>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

// ── Panel: Insights Hub (unified analytics dashboard) ────────────────────────

function InsightsHubPanel() {
  // RATIONALE: Instead of three peer pages behind tabs, one "Insights Hub" shows a
  // KPI overview first (the cross-cutting story), then offers drill-down lenses.
  // Each KPI is tagged with the legacy page it came from, proving full coverage.
  const [lens, setLens] = useState<'overview' | 'Statistics' | 'Cost Analytics' | 'Analytics'>('overview');

  const visibleKpis =
    lens === 'overview' ? ANALYTICS_KPIS : ANALYTICS_KPIS.filter((k) => k.source === lens);

  const lenses: { id: typeof lens; label: string }[] = [
    { id: 'overview', label: 'Overview' },
    { id: 'Statistics', label: 'Production' },
    { id: 'Cost Analytics', label: 'Cost' },
    { id: 'Analytics', label: 'Fleet' },
  ];

  return (
    <div className="space-y-4">
      <p className="text-sm text-pf-text-secondary">
        One destination. The <span className="text-pf-text-primary font-medium">Overview</span> answers “how is the
        farm doing?” at a glance; the lenses below are drill-downs, not separate pages. Statistics → Production,
        Cost Analytics → Cost, Analytics → Fleet.
      </p>

      {/* Lens switcher — drill-down, not navigation */}
      <div role="tablist" aria-label="Insights lenses" className="flex flex-wrap gap-1 border-b border-pf-border">
        {lenses.map((l) => {
          const isActive = lens === l.id;
          return (
            <button
              key={l.id}
              role="tab"
              type="button"
              aria-selected={isActive}
              onClick={() => setLens(l.id)}
              className={clsx(
                'px-4 py-2 text-sm font-medium -mb-px border-b-2 transition-colors',
                'focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent',
                isActive
                  ? 'border-pf-accent text-pf-text-primary'
                  : 'border-transparent text-pf-text-secondary hover:text-pf-text-primary'
              )}
            >
              {l.label}
            </button>
          );
        })}
      </div>

      {/* KPI grid — dashboard overview */}
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {visibleKpis.map((kpi) => (
          <Card key={kpi.id}>
            <Card.Body>
              <div className="flex items-start justify-between gap-2">
                <span className="text-xs uppercase tracking-wide text-pf-text-secondary">{kpi.label}</span>
                <Badge variant="default" size="sm">{kpi.source}</Badge>
              </div>
              <div className="mt-2 flex items-baseline gap-2">
                <span className="text-2xl font-semibold text-pf-text-primary">{kpi.value}</span>
                <span
                  className={clsx(
                    'text-xs font-medium',
                    kpi.trend === 'up' && 'text-pf-success',
                    kpi.trend === 'down' && 'text-pf-success',
                    kpi.trend === 'flat' && 'text-pf-text-secondary'
                  )}
                >
                  {kpi.delta !== 'flat' ? kpi.delta : '—'}
                </span>
              </div>
            </Card.Body>
          </Card>
        ))}
      </div>

      {/* Placeholder for the charting region that would sit under the KPIs */}
      <Card>
        <Card.Body>
          <div className="flex items-center gap-2 mb-3">
            <ChartIcon className="w-4 h-4 text-pf-text-secondary" />
            <h4 className="text-sm font-semibold text-pf-text-primary">
              {lens === 'overview' ? 'Trend (all metrics)' : `${lens} detail`}
            </h4>
          </div>
          {/* Mock sparkline-style bars so the layout reads as a real dashboard */}
          <div
            className="flex items-end gap-1.5 h-28"
            role="img"
            aria-label="Mock 14-day trend chart"
          >
            {[42, 55, 38, 61, 70, 48, 66, 72, 58, 80, 63, 75, 68, 84].map((h, i) => (
              <div
                key={i}
                className="flex-1 rounded-t bg-pf-accent/70"
                style={{ height: `${h}%` }}
              />
            ))}
          </div>
        </Card.Body>
      </Card>
    </div>
  );
}

// ── Panel: System Pulse (persistent health widget) ───────────────────────────

function SystemPulsePanel() {
  const [open, setOpen] = useState(true);

  // Worst service health drives the top-bar pill color.
  const overall: Health = SERVICE_VERSIONS.concat(
    RESOURCE_METRICS.map((m) => ({ id: m.id, name: m.label, version: '', health: m.health, detail: m.detail }))
  ).some((s) => s.health === 'critical')
    ? 'critical'
    : RESOURCE_METRICS.concat(
        SERVICE_VERSIONS.map((s) => ({ id: s.id, label: s.name, value: 0, detail: s.detail, health: s.health }))
      ).some((s) => s.health === 'warning')
    ? 'warning'
    : 'healthy';

  return (
    <div className="space-y-4">
      <p className="text-sm text-pf-text-secondary">
        System status is ambient, not a page you hunt for. A compact{' '}
        <span className="text-pf-text-primary font-medium">System Pulse</span> pill lives in the top bar and expands
        into this popover. The full breakdown also has a permanent home under{' '}
        <span className="text-pf-text-primary font-medium">Settings → Platform → Health</span> for deep dives.
      </p>

      {/* The persistent top-bar pill (mock) */}
      <div className="flex items-center gap-3">
        <button
          type="button"
          onClick={() => setOpen((v) => !v)}
          aria-expanded={open}
          className={clsx(
            'inline-flex items-center gap-2 px-3 py-1.5 rounded-full border text-sm font-medium transition-colors',
            'focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent',
            'border-pf-border bg-pf-bg-1 text-pf-text-primary hover:bg-pf-bg-0'
          )}
        >
          <span aria-hidden="true">{healthIcon(overall)}</span>
          System Pulse
          <Badge variant={HEALTH_BADGE[overall].variant} size="sm">{HEALTH_BADGE[overall].label}</Badge>
        </button>
        <span className="text-xs text-pf-text-secondary">← lives in the top bar, expands on click</span>
      </div>

      {open ? (
        <div className="grid gap-4 lg:grid-cols-2">
          {/* Resource meters */}
          <Card>
            <Card.Body>
              <div className="flex items-center gap-2 mb-3">
                <ActivityIcon className="w-4 h-4 text-pf-text-secondary" />
                <h4 className="text-sm font-semibold text-pf-text-primary">Resources</h4>
              </div>
              <ul className="space-y-3">
                {RESOURCE_METRICS.map((m) => (
                  <li key={m.id}>
                    <div className="flex items-center justify-between text-sm">
                      <span className="flex items-center gap-2 text-pf-text-primary">
                        {healthIcon(m.health)}
                        {m.label}
                      </span>
                      <span className="text-pf-text-secondary">{m.detail}</span>
                    </div>
                    <div
                      className="mt-1.5 h-2 rounded-full bg-pf-bg-1 overflow-hidden"
                      role="progressbar"
                      aria-label={`${m.label} usage`}
                      aria-valuenow={m.value}
                      aria-valuemin={0}
                      aria-valuemax={100}
                    >
                      <div className={clsx('h-full rounded-full', meterColor(m.health))} style={{ width: `${m.value}%` }} />
                    </div>
                  </li>
                ))}
              </ul>
            </Card.Body>
          </Card>

          {/* Service versions */}
          <Card>
            <Card.Body>
              <div className="flex items-center gap-2 mb-3">
                <ServerIcon className="w-4 h-4 text-pf-text-secondary" />
                <h4 className="text-sm font-semibold text-pf-text-primary">Services & Versions</h4>
              </div>
              <ul className="divide-y divide-pf-border">
                {SERVICE_VERSIONS.map((s) => (
                  <li key={s.id} className="flex items-center justify-between gap-3 py-2">
                    <span className="flex items-center gap-2 text-sm text-pf-text-primary">
                      {healthIcon(s.health)}
                      {s.name}
                    </span>
                    <span className="flex items-center gap-2">
                      <span className="text-xs text-pf-text-secondary">{s.detail}</span>
                      <Badge variant="default" size="sm">{s.version}</Badge>
                    </span>
                  </li>
                ))}
              </ul>
            </Card.Body>
          </Card>
        </div>
      ) : null}
    </div>
  );
}

// ── Panel: Navigation (proposed nav structure) ───────────────────────────────

function NavigationPanel() {
  return (
    <div className="space-y-4">
      <p className="text-sm text-pf-text-secondary">
        Verbs over nouns: <span className="text-pf-text-primary font-medium">Operate</span> /{' '}
        <span className="text-pf-text-primary font-medium">Insights</span> /{' '}
        <span className="text-pf-text-primary font-medium">Organization</span> name what you’re doing.
        Three analytics links collapse to one; the orphans move into Settings; Printer Groups becomes an
        Organization concept.
      </p>

      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {PROPOSED_NAV.map((group) => (
          <Card key={group.header}>
            <Card.Body>
              <h4 className="text-xs font-semibold uppercase tracking-wide text-pf-text-secondary mb-2">
                {group.header}
              </h4>
              <ul className="space-y-1">
                {group.items.map((item) => (
                  <li
                    key={item.label}
                    className="flex items-center justify-between gap-2 px-2 py-1.5 rounded-md hover:bg-pf-bg-1"
                  >
                    <span className="flex items-center gap-2 text-sm text-pf-text-primary">
                      <span aria-hidden="true">{item.icon}</span>
                      {item.label}
                    </span>
                    {item.note ? (
                      <span className="text-[11px] text-pf-text-secondary text-right">{item.note}</span>
                    ) : null}
                  </li>
                ))}
              </ul>
            </Card.Body>
          </Card>
        ))}
      </div>

      <Card>
        <Card.Body>
          <div className="flex items-center gap-2 mb-2">
            <InfoIcon className="w-4 h-4 text-pf-text-secondary" />
            <h4 className="text-sm font-semibold text-pf-text-primary">What changed vs. today</h4>
          </div>
          <ul className="space-y-1.5 text-sm text-pf-text-secondary">
            <li className="flex items-center gap-2">
              <KeyIcon className="w-4 h-4 text-pf-text-secondary" /> Statistics + Cost Analytics + Analytics → one
              <span className="text-pf-text-primary">Insights Hub</span>
            </li>
            <li className="flex items-center gap-2">
              <NfcIcon className="w-4 h-4 text-pf-text-secondary" /> NFC Bindings, API Keys → into{' '}
              <span className="text-pf-text-primary">Settings · Connectivity</span>
            </li>
            <li className="flex items-center gap-2">
              <PrinterIcon className="w-4 h-4 text-pf-text-secondary" /> Printer Groups → into a new{' '}
              <span className="text-pf-text-primary">Organization</span> group (stays operational, not config)
            </li>
            <li className="flex items-center gap-2">
              <ServerIcon className="w-4 h-4 text-pf-text-secondary" /> Workers + System → into{' '}
              <span className="text-pf-text-primary">Settings · Platform</span>; health surfaces as System Pulse
            </li>
            <li className="flex items-center gap-2">
              <DatabaseIcon className="w-4 h-4 text-pf-text-secondary" /> Filament Inventory{' '}
              <span className="text-pf-text-primary">stays top-level</span> (explicit directive — never moved into Settings)
            </li>
          </ul>
        </Card.Body>
      </Card>
    </div>
  );
}

// ── Top-level PoC shell ──────────────────────────────────────────────────────

type PanelId = 'mental-model' | 'insights' | 'pulse' | 'navigation';

const PANELS: { id: PanelId; label: string; icon: React.ReactNode }[] = [
  { id: 'mental-model', label: 'Mental Model', icon: <GearIcon className="w-4 h-4" /> },
  { id: 'insights', label: 'Insights Hub', icon: <TrendingUpIcon className="w-4 h-4" /> },
  { id: 'pulse', label: 'System Pulse', icon: <ActivityIcon className="w-4 h-4" /> },
  { id: 'navigation', label: 'Navigation', icon: <HomeIcon className="w-4 h-4" /> },
];

export const SettingsReorgProposalFerro: React.FC = () => {
  const [panel, setPanel] = useState<PanelId>('mental-model');

  return (
    <div className="space-y-5 p-4 md:p-6 bg-pf-bg-0 text-pf-text-primary">
      <header className="space-y-1">
        <div className="flex items-center gap-2">
          <h1 className="text-xl font-semibold">Settings &amp; Analytics Reorg — Ferro’s PoC</h1>
          <Badge variant="primary" size="sm">Alternative</Badge>
        </div>
        <p className="text-sm text-pf-text-secondary max-w-3xl">
          A competing perspective: organize Settings by <strong>user intent</strong> (4 zones), turn analytics into a
          single <strong>Insights Hub</strong> dashboard, and make system health an <strong>ambient pulse</strong>
          {' '}rather than a buried page.
        </p>
      </header>

      {/* Panel switcher */}
      <div role="tablist" aria-label="Proposal panels" className="flex flex-wrap gap-1">
        {PANELS.map((p) => {
          const isActive = panel === p.id;
          return (
            <button
              key={p.id}
              role="tab"
              type="button"
              aria-selected={isActive}
              onClick={() => setPanel(p.id)}
              className={clsx(
                'inline-flex items-center gap-2 px-3 py-2 rounded-md text-sm font-medium transition-colors',
                'focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent',
                isActive
                  ? 'bg-pf-accent-bg text-pf-text-primary border border-pf-accent'
                  : 'text-pf-text-secondary hover:bg-pf-bg-1 hover:text-pf-text-primary border border-transparent'
              )}
            >
              <span aria-hidden="true">{p.icon}</span>
              {p.label}
            </button>
          );
        })}
      </div>

      <section>
        {panel === 'mental-model' ? <MentalModelPanel /> : null}
        {panel === 'insights' ? <InsightsHubPanel /> : null}
        {panel === 'pulse' ? <SystemPulsePanel /> : null}
        {panel === 'navigation' ? <NavigationPanel /> : null}
      </section>
    </div>
  );
};

export default SettingsReorgProposalFerro;
