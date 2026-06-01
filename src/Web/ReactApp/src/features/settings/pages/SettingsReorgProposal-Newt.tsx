import { Badge, Card, Tabs } from '@/common/components/ui';
import { PageTemplate } from '@/common/components/PageTemplate';
import {
  ChartIcon,
  DatabaseIcon,
  GearIcon,
  KeyIcon,
  LayersIcon,
  NetworkIcon,
  SettingsIcon,
  TrendingUpIcon,
  UsersIcon,
  WrenchIcon,
} from '@/common/components/icons/MdiIcons';
import type { SettingsCategory } from '@/features/settings/types';

const proposedSettingsCategories: SettingsCategory[] = [
  {
    id: 'general',
    label: 'General',
    keywords: ['general', 'farm', 'identity', 'timezone', 'appearance', 'theme', 'system'],
    subPages: [],
  },
  {
    id: 'slicing',
    label: 'Slicing',
    keywords: ['slicer', 'slice', 'profile', 'orcaslicer', 'prusaslicer', 'bed type', 'process'],
    subPages: [
      { id: 'bed-types', label: 'Bed Types', keywords: ['bed', 'type', 'surface', 'plate'] },
      { id: 'profiles', label: 'Slicer Profiles', keywords: ['profile', 'slicer', 'orcaslicer', 'prusaslicer'] },
    ],
  },
  {
    id: 'hardware',
    label: 'Hardware',
    keywords: ['printer', 'hardware', 'camera', 'nfc', 'bindings', 'location', 'custom field', 'device'],
    subPages: [
      { id: 'printer-groups', label: 'Printer Groups', keywords: ['printer', 'group', 'cell', 'fleet'] },
      { id: 'cameras', label: 'Cameras', keywords: ['camera', 'webcam', 'stream', 'video'] },
      { id: 'nfc-devices', label: 'NFC Devices', keywords: ['nfc', 'reader', 'rfid', 'device'] },
      { id: 'nfc-bindings', label: 'NFC Bindings', keywords: ['nfc', 'binding', 'tag', 'spool'] },
      { id: 'locations', label: 'Locations', keywords: ['location', 'room', 'area', 'zone'] },
      { id: 'custom-fields', label: 'Custom Fields', keywords: ['custom', 'field', 'attribute', 'metadata'] },
    ],
  },
  {
    id: 'notifications',
    label: 'Notifications',
    keywords: ['notification', 'email', 'alert', 'push', 'telegram', 'discord'],
    subPages: [],
  },
  {
    id: 'integrations',
    label: 'Integrations',
    keywords: ['integration', 'api', 'key', 'external', 'webhook', 'automation', 'endpoint'],
    subPages: [
      { id: 'webhooks', label: 'Webhooks', keywords: ['webhook', 'automation', 'endpoint'] },
      { id: 'api-keys', label: 'API Keys', keywords: ['api', 'key', 'token', 'access'] },
    ],
  },
  {
    id: 'data',
    label: 'Data',
    keywords: ['data', 'backup', 'export', 'import', 'storage', 'tag', 'quota', 'cleanup'],
    subPages: [
      { id: 'tags', label: 'Tags', keywords: ['tag', 'label', 'category'] },
      { id: 'quotas', label: 'Quotas', keywords: ['quota', 'limit', 'allowance', 'budget'] },
      { id: 'management', label: 'Data Management', keywords: ['backup', 'export', 'import', 'cleanup'] },
    ],
  },
  {
    id: 'users',
    label: 'Users',
    keywords: ['user', 'role', 'permission', 'account', 'admin', 'login', 'audit', 'security'],
    subPages: [
      { id: 'accounts', label: 'User Accounts', keywords: ['user', 'account', 'role', 'permission'] },
      { id: 'audit', label: 'Login Audit', keywords: ['login', 'audit', 'history', 'security'] },
    ],
  },
  {
    id: 'system',
    label: 'System',
    keywords: ['system', 'health', 'cpu', 'memory', 'disk', 'version', 'service'],
    subPages: [
      { id: 'status', label: 'Status', keywords: ['health', 'cpu', 'memory', 'disk', 'version'] },
      { id: 'workers', label: 'Workers', keywords: ['worker', 'slicer', 'service'] },
    ],
  },
];

const proposedNavigation = [
  {
    section: 'Operations',
    items: ['Dashboard', 'Printers', 'Files', 'Projects', 'Slice', 'Print Queue', 'Auto-Dispatch'],
  },
  {
    section: 'Hardware',
    items: ['Filament Inventory'],
  },
  {
    section: 'Management',
    items: ['Maintenance', 'Analytics', 'Scheduling'],
  },
  {
    section: 'Admin',
    items: ['Catalog', 'Workers', 'System', 'Settings'],
  },
];

const analyticsPillars = [
  {
    id: 'statistics',
    label: 'Statistics',
    summary: 'Operational throughput, job outcomes, material use, and utilization.',
    source: 'Former /statistics',
    metrics: ['1,284 jobs', '93.8% success', '417 print hours', '82 kg filament'],
  },
  {
    id: 'costs',
    label: 'Cost Analytics',
    summary: 'Cost by printer, material, job, labor, machine time, and energy.',
    source: 'Former /statistics/costs',
    metrics: ['$3,418 total', '$2.66/job', '61% material', '14% energy'],
  },
  {
    id: 'insights',
    label: 'Business Insights',
    summary: 'Correlations, maintenance forecasts, predictive alerts, and exports.',
    source: 'Former /analytics',
    metrics: ['6 alerts', '4 forecasts', '11 correlations', 'CSV/PDF export'],
  },
];

const systemResources = [
  { label: 'CPU', value: '42%', detail: '8 cores · 2.9 GHz avg', tone: 'Nominal', percent: 42 },
  { label: 'Memory', value: '11.6 / 32 GB', detail: '36% used', tone: 'Nominal', percent: 36 },
  { label: 'Disk', value: '412 / 960 GB', detail: '548 GB free', tone: 'Watch', percent: 43 },
];

const serviceVersions = [
  { name: 'Frontend', version: '2026.6.1-poc', status: 'Current' },
  { name: 'Backend API', version: '10.0.0-preview', status: 'Current' },
  { name: 'Slicer Host', version: '2.3.0', status: 'Current' },
  { name: 'OrcaSlicer Worker', version: '2.3.1', status: 'Update ready' },
];

const categoryIconMap: Record<string, React.ReactNode> = {
  general: <GearIcon className="h-5 w-5" />,
  slicing: <LayersIcon className="h-5 w-5" />,
  hardware: <WrenchIcon className="h-5 w-5" />,
  notifications: <NetworkIcon className="h-5 w-5" />,
  integrations: <KeyIcon className="h-5 w-5" />,
  data: <DatabaseIcon className="h-5 w-5" />,
  users: <UsersIcon className="h-5 w-5" />,
  system: <SettingsIcon className="h-5 w-5" />,
};

const highlightedMoves = [
  'API Keys move from Management into Settings → Integrations.',
  'NFC Bindings move from Hardware navigation into Settings → Hardware.',
  'Printer Groups move from Admin navigation into Settings → Hardware.',
  'Statistics, Cost Analytics, and Analytics collapse to one Management → Analytics entry.',
];

export function SettingsReorgProposalNewt() {
  return (
    <PageTemplate
      title="Settings Reorganization Proposal"
      subtitle="Proof of concept for consolidating analytics and moving admin-like configuration into Settings."
      icon={SettingsIcon}
    >
      <div className="space-y-6 text-pf-text-primary" aria-labelledby="settings-reorg-heading">
        <section
          className="relative overflow-hidden rounded-xl border border-pf-border bg-pf-bg-0 p-5 shadow-sm"
          aria-labelledby="settings-reorg-heading"
        >
          <div
            className="pointer-events-none absolute inset-x-0 top-0 h-1 bg-linear-to-r from-pf-accent via-pf-warning to-pf-success"
            aria-hidden="true"
          />
          <div className="grid gap-5 lg:grid-cols-[1.35fr_0.65fr]">
            <div>
              <p className="text-xs font-semibold uppercase tracking-[0.22em] text-pf-accent">
                Industrial IA proof of concept
              </p>
              <h1 id="settings-reorg-heading" className="mt-2 text-2xl font-semibold text-pf-text-primary">
                Fewer doors, clearer control rooms
              </h1>
              <p className="mt-3 max-w-3xl text-sm leading-6 text-pf-text-secondary">
                The proposal keeps operational work visible, keeps Filament Inventory in Hardware, and moves
                configuration-heavy surfaces behind the existing Settings sidebar plus sub-tab pattern.
              </p>
            </div>
            <Card className="bg-pf-bg-1">
              <Card.Body>
                <h2 className="text-sm font-semibold text-pf-text-primary">Navigation reduction</h2>
                <dl className="mt-4 grid grid-cols-2 gap-3">
                  <div>
                    <dt className="text-xs uppercase tracking-wide text-pf-text-secondary">Before</dt>
                    <dd className="text-2xl font-semibold text-pf-warning">23</dd>
                  </div>
                  <div>
                    <dt className="text-xs uppercase tracking-wide text-pf-text-secondary">After</dt>
                    <dd className="text-2xl font-semibold text-pf-success">18</dd>
                  </div>
                </dl>
                <p className="mt-3 text-xs text-pf-text-secondary">
                  Main nav gets calmer without hiding discoverability inside Settings search.
                </p>
              </Card.Body>
            </Card>
          </div>
        </section>

        <section className="grid gap-6 xl:grid-cols-[0.9fr_1.1fr]" aria-labelledby="nav-proposal-heading">
          <Card>
            <Card.Header>
              <h2 id="nav-proposal-heading" className="text-base font-semibold">
                Proposed main navigation
              </h2>
            </Card.Header>
            <Card.Body>
              <nav aria-label="Proposed PrintFarmer navigation">
                <ul className="space-y-4">
                  {proposedNavigation.map((group) => (
                    <li key={group.section}>
                      <h3 className="text-xs font-semibold uppercase tracking-[0.2em] text-pf-text-secondary">
                        {group.section}
                      </h3>
                      <ul className="mt-2 grid gap-2 sm:grid-cols-2">
                        {group.items.map((item) => (
                          <li
                            key={`${group.section}-${item}`}
                            className="rounded-md border border-pf-border bg-pf-bg-1 px-3 py-2 text-sm text-pf-text-primary"
                          >
                            {item}
                          </li>
                        ))}
                      </ul>
                    </li>
                  ))}
                </ul>
              </nav>
              <p className="mt-4 rounded-md border border-pf-border bg-pf-bg-1 p-3 text-xs text-pf-text-secondary">
                JSX sketch: Management now exposes one Analytics link; API Keys, NFC Bindings, and Printer
                Groups become Settings sub-tabs. Filament Inventory remains in Hardware.
              </p>
            </Card.Body>
          </Card>

          <Card>
            <Card.Header>
              <h2 className="text-base font-semibold">Moved items and rationale</h2>
            </Card.Header>
            <Card.Body>
              <ul className="grid gap-3 md:grid-cols-2">
                {highlightedMoves.map((move) => (
                  <li key={move} className="flex gap-3 rounded-lg border border-pf-border bg-pf-bg-1 p-3">
                    <Badge variant="primary">
                      Move
                    </Badge>
                    <span className="text-sm text-pf-text-secondary">{move}</span>
                  </li>
                ))}
              </ul>
            </Card.Body>
          </Card>
        </section>

        <section className="grid gap-6 xl:grid-cols-[0.82fr_1.18fr]" aria-labelledby="settings-map-heading">
          <Card>
            <Card.Header>
              <h2 id="settings-map-heading" className="text-base font-semibold">
                Proposed Settings categories
              </h2>
            </Card.Header>
            <Card.Body>
              <div className="space-y-2">
                {proposedSettingsCategories.map((category) => (
                  <div
                    key={category.id}
                    className="rounded-lg border border-pf-border bg-pf-bg-1 p-3"
                  >
                    <div className="flex items-center gap-3">
                      <span className="text-pf-accent" aria-hidden="true">
                        {categoryIconMap[category.id] ?? <GearIcon className="h-5 w-5" />}
                      </span>
                      <h3 className="text-sm font-semibold text-pf-text-primary">{category.label}</h3>
                      <Badge variant={category.subPages.length > 0 ? 'info' : 'default'}>
                        {category.subPages.length > 0 ? `${category.subPages.length} tabs` : 'single page'}
                      </Badge>
                    </div>
                    {category.subPages.length > 0 ? (
                      <ul className="mt-3 flex flex-wrap gap-2">
                        {category.subPages.map((subPage) => (
                          <li key={`${category.id}-${subPage.id}`}>
                            <span className="rounded-full border border-pf-border bg-pf-bg-0 px-2.5 py-1 text-xs text-pf-text-secondary">
                              {subPage.label}
                            </span>
                          </li>
                        ))}
                      </ul>
                    ) : null}
                  </div>
                ))}
              </div>
            </Card.Body>
          </Card>

          <div className="space-y-6">
            <UnifiedAnalyticsPreview />
            <SystemStatusCard />
          </div>
        </section>
      </div>
    </PageTemplate>
  );
}

function UnifiedAnalyticsPreview() {
  return (
    <Card>
      <Card.Header
        actions={
          <Badge variant="success" size="md">
            One nav link
          </Badge>
        }
      >
        <div className="flex items-center gap-2">
          <TrendingUpIcon className="h-5 w-5 text-pf-accent" />
          <h2 className="text-base font-semibold">Unified Analytics page</h2>
        </div>
      </Card.Header>
      <Card.Body>
        <p className="mb-4 text-sm leading-6 text-pf-text-secondary">
          Route proposal: <span className="font-medium text-pf-text-primary">/analytics</span> becomes the
          single destination, with tabs preserving the mental model of the three existing analytics surfaces.
        </p>
        <Tabs defaultTab="statistics">
          <Tabs.List className="overflow-x-auto">
            <Tabs.Tab id="statistics" icon={<ChartIcon className="h-4 w-4" />}>
              Statistics
            </Tabs.Tab>
            <Tabs.Tab id="costs" icon={<TrendingUpIcon className="h-4 w-4" />}>
              Costs
            </Tabs.Tab>
            <Tabs.Tab id="insights" icon={<NetworkIcon className="h-4 w-4" />}>
              Insights
            </Tabs.Tab>
          </Tabs.List>
          <Tabs.Panels>
            {analyticsPillars.map((pillar) => (
              <Tabs.Panel key={pillar.id} id={pillar.id}>
                <article className="space-y-4">
                  <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                    <div>
                      <h3 className="text-lg font-semibold text-pf-text-primary">{pillar.label}</h3>
                      <p className="mt-1 text-sm text-pf-text-secondary">{pillar.summary}</p>
                    </div>
                    <Badge variant="info">{pillar.source}</Badge>
                  </div>
                  <dl className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
                    {pillar.metrics.map((metric) => {
                      const [value, ...labelParts] = metric.split(' ');
                      return (
                        <div key={metric} className="rounded-lg border border-pf-border bg-pf-bg-1 p-3">
                          <dt className="text-xs uppercase tracking-wide text-pf-text-secondary">
                            {labelParts.join(' ') || pillar.label}
                          </dt>
                          <dd className="mt-1 text-xl font-semibold text-pf-text-primary">{value}</dd>
                        </div>
                      );
                    })}
                  </dl>
                </article>
              </Tabs.Panel>
            ))}
          </Tabs.Panels>
        </Tabs>
      </Card.Body>
    </Card>
  );
}

function SystemStatusCard() {
  return (
    <Card>
      <Card.Header
        actions={
          <Badge variant="warning" size="md">
            1 update
          </Badge>
        }
      >
        <div className="flex items-center gap-2">
          <SettingsIcon className="h-5 w-5 text-pf-accent" />
          <h2 className="text-base font-semibold">System Status card</h2>
        </div>
      </Card.Header>
      <Card.Body>
        <div className="grid gap-4 lg:grid-cols-[0.95fr_1.05fr]">
          <section aria-labelledby="resources-heading">
            <h3 id="resources-heading" className="text-sm font-semibold text-pf-text-primary">
              Host resources
            </h3>
            <div className="mt-3 space-y-3">
              {systemResources.map((resource) => (
                <div key={resource.label} className="rounded-lg border border-pf-border bg-pf-bg-1 p-3">
                  <div className="flex items-start justify-between gap-3">
                    <div>
                      <p className="text-sm font-semibold text-pf-text-primary">{resource.label}</p>
                      <p className="text-xs text-pf-text-secondary">{resource.detail}</p>
                    </div>
                    <div className="text-right">
                      <p className="text-sm font-semibold text-pf-text-primary">{resource.value}</p>
                      <p className="text-xs text-pf-text-secondary">{resource.tone}</p>
                    </div>
                  </div>
                  <div
                    className="mt-3 h-2 rounded-full bg-pf-bg-2"
                    role="meter"
                    aria-label={`${resource.label} usage`}
                    aria-valuemin={0}
                    aria-valuemax={100}
                    aria-valuenow={resource.percent}
                  >
                    <div
                      className="h-2 rounded-full bg-pf-accent"
                      style={{ width: `${resource.percent}%` }}
                    />
                  </div>
                </div>
              ))}
            </div>
          </section>

          <section aria-labelledby="versions-heading">
            <h3 id="versions-heading" className="text-sm font-semibold text-pf-text-primary">
              Service versions
            </h3>
            <div className="mt-3 overflow-hidden rounded-lg border border-pf-border">
              <table className="w-full text-left text-sm">
                <thead className="bg-pf-bg-1 text-xs uppercase tracking-wide text-pf-text-secondary">
                  <tr>
                    <th scope="col" className="px-3 py-2">
                      Service
                    </th>
                    <th scope="col" className="px-3 py-2">
                      Version
                    </th>
                    <th scope="col" className="px-3 py-2">
                      Status
                    </th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-pf-border bg-pf-bg-0">
                  {serviceVersions.map((service) => (
                    <tr key={service.name}>
                      <th scope="row" className="px-3 py-2 font-medium text-pf-text-primary">
                        {service.name}
                      </th>
                      <td className="px-3 py-2 text-pf-text-secondary">{service.version}</td>
                      <td className="px-3 py-2">
                        <Badge variant={service.status === 'Current' ? 'success' : 'warning'}>
                          {service.status}
                        </Badge>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="mt-3 text-xs text-pf-text-secondary">
              Recommended placement: Settings → System → Status, with a compact read-only version on Admin → System.
            </p>
          </section>
        </div>
      </Card.Body>
    </Card>
  );
}

export default SettingsReorgProposalNewt;
