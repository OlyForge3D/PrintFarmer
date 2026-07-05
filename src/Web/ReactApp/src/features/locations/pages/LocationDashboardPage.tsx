import React, { useCallback, useMemo, useState } from 'react';
import clsx from 'clsx';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Badge, Button, Card, Spinner, Tabs } from '@/common/components/ui';
import {
  AlertIcon,
  EditIcon,
  LocationIcon,
  PlusIcon,
  PrinterIcon,
} from '@/common/components/icons/MdiIcons';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { LocationHierarchyPanel } from '@/features/locations/components/LocationHierarchyPanel';
import { LocationManagement } from '@/features/locations/components/LocationManagement';
import { LocationPrinterList, type LocationPrinterListPrinter } from '@/features/locations/components/LocationPrinterList';
import { PrinterLocationDragDrop } from '@/features/printers/components/PrinterLocationDragDrop';
import {
  useLocationTree,
  useLocationPrinters,
  useLocationStats,
  useSignalRPrinterUpdates,
  findNode,
  isActiveJob,
} from '@/features/locations/hooks/useLocationDashboard';
import type { Location, LocationSubtreePrinter, LocationTreeNode } from '@/types/api';

function toLocation(node: LocationTreeNode): Location {
  return {
    id: node.id,
    name: node.name,
    description: node.description,
    parentId: node.parentId,
    path: node.path,
    depth: node.depth,
    sortOrder: node.sortOrder,
    printerCount: node.printerCount,
    totalPrinterCount: node.totalPrinterCount,
    createdAt: '',
    modifiedAt: '',
    isActive: true,
  };
}

function flattenLocations(nodes: LocationTreeNode[]): Location[] {
  const flattened: Location[] = [];
  const visit = (node: LocationTreeNode) => {
    flattened.push(toLocation(node));
    node.children.forEach(visit);
  };
  nodes.forEach(visit);
  return flattened;
}

function getLocationPath(node: LocationTreeNode | null): string {
  if (!node) return 'All Locations';
  return node.path?.replace(/^\/+/, '').replaceAll('/', ' / ') || node.name;
}

function toPrinterListItem(printer: LocationSubtreePrinter): LocationPrinterListPrinter {
  return {
    id: printer.printerId,
    name: printer.printerName,
    isOnline: printer.isOnline,
    status: printer.status,
    currentJobName: printer.currentJobName,
  };
}

function statusVariant(value: number): 'success' | 'warning' | 'error' | 'default' | 'primary' {
  if (value === 0) return 'default';
  return 'success';
}

interface SummaryCardProps {
  title: string;
  children: React.ReactNode;
  tone?: 'neutral' | 'success' | 'warning' | 'accent';
}

function SummaryCard({ title, children, tone = 'neutral' }: SummaryCardProps) {
  const toneClass = {
    neutral: 'border-pf-border',
    success: 'border-pf-success/40',
    warning: 'border-pf-warning/50',
    accent: 'border-pf-accent/40',
  }[tone];

  return (
    <Card className={clsx('border-l-4', toneClass)}>
      <Card.Body className="space-y-3 p-4">
        <h2 className="text-sm font-semibold uppercase tracking-wide text-pf-text-secondary">{title}</h2>
        {children}
      </Card.Body>
    </Card>
  );
}

function StatLine({ label, value, variant = 'default' }: { label: string; value: number; variant?: 'success' | 'warning' | 'error' | 'default' | 'primary' }) {
  return (
    <div className="flex items-center justify-between gap-3">
      <span className="text-sm text-pf-text-secondary">{label}</span>
      <Badge variant={variant} size="md">{value}</Badge>
    </div>
  );
}

export const LocationDashboardPage: React.FC = () => {
  const [selectedLocationId, setSelectedLocationId] = useState<string | null>(null);
  const [manageMode, setManageMode] = useState(false);
  const [createToken, setCreateToken] = useState(0);
  const { hasRole } = useAuth();
  const isFarmAdmin = hasRole('farm_admin');
  const { data: tree = [], isLoading: treeLoading, error: treeError } = useLocationTree();
  const { stats, isLoading: statsLoading } = useLocationStats(selectedLocationId);
  const { data: subtreePrinters = [], isLoading: printersLoading } = useLocationPrinters(selectedLocationId);

  useSignalRPrinterUpdates();

  const selectedNode = selectedLocationId ? findNode(tree, selectedLocationId) ?? null : null;
  const selectedPath = getLocationPath(selectedNode);
  const allLocations = useMemo(() => flattenLocations(tree), [tree]);
  const childLocations = selectedNode?.children ?? tree;
  const printerList = useMemo(
    () => subtreePrinters.map(toPrinterListItem),
    [subtreePrinters],
  );
  const activeJobs = useMemo(
    () => subtreePrinters.filter(isActiveJob),
    [subtreePrinters],
  );
  const directPrinterCount = selectedNode
    ? selectedNode.printerCount
    : tree.reduce((sum, node) => sum + node.printerCount, 0);

  const handlePrinterClick = useCallback((printerId: string) => {
    window.location.href = `/printers?selected=${printerId}`;
  }, []);

  const handleAddLocation = () => {
    setManageMode(true);
    setCreateToken((value) => value + 1);
  };

  if (treeLoading) {
    return (
      <PageTemplate title="Locations" icon={LocationIcon}>
        <div className="flex items-center justify-center py-12" role="status" aria-label="Loading locations">
          <Spinner size="lg" />
        </div>
      </PageTemplate>
    );
  }

  if (treeError) {
    return (
      <PageTemplate title="Locations" icon={LocationIcon}>
        <div className="rounded-lg border border-pf-error bg-pf-error-bg p-4 text-pf-error">
          Failed to load locations: {String(treeError)}
        </div>
      </PageTemplate>
    );
  }

  return (
    <PageTemplate
      title="Locations"
      subtitle={selectedPath}
      icon={LocationIcon}
      actions={isFarmAdmin ? (
        <div className="flex flex-wrap items-center gap-2">
          <Button
            type="button"
            variant={manageMode ? 'secondary' : 'subtle'}
            onClick={() => setManageMode((value) => !value)}
            iconLeft={<EditIcon />}
            aria-pressed={manageMode}
          >
            Manage
          </Button>
          <Button type="button" variant="primary" onClick={handleAddLocation} iconLeft={<PlusIcon />}>
            Add location
          </Button>
        </div>
      ) : undefined}
    >
      <div className="flex flex-col gap-6 lg:flex-row">
        <LocationHierarchyPanel
          tree={tree}
          selectedId={selectedLocationId}
          onSelect={setSelectedLocationId}
        />

        <div className="min-w-0 flex-1 space-y-6">
          <section aria-labelledby="location-summary-heading" className="space-y-3">
            <h2 id="location-summary-heading" className="sr-only">At-a-glance summary</h2>
            <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
              <SummaryCard title="Fleet health" tone={stats.attention > 0 ? 'warning' : 'success'}>
                {statsLoading ? (
                  <div className="h-20 rounded pf-skeleton pf-animate-skeleton" />
                ) : (
                  <>
                    <StatLine label="Online" value={stats.online} variant="success" />
                    <StatLine label="Offline" value={stats.offline} variant={stats.offline > 0 ? 'error' : 'default'} />
                    <StatLine label="Attention" value={stats.attention} variant={stats.attention > 0 ? 'warning' : 'default'} />
                  </>
                )}
              </SummaryCard>
              <SummaryCard title="Activity" tone="accent">
                {statsLoading ? (
                  <div className="h-20 rounded pf-skeleton pf-animate-skeleton" />
                ) : (
                  <>
                    <StatLine label="Printing" value={stats.printing} variant="primary" />
                    <StatLine label="Idle" value={stats.idle} variant={statusVariant(stats.idle)} />
                    <StatLine label="Active jobs" value={stats.activeJobs} variant={stats.activeJobs > 0 ? 'primary' : 'default'} />
                  </>
                )}
              </SummaryCard>
              <SummaryCard title="Placement">
                {statsLoading ? (
                  <div className="h-20 rounded pf-skeleton pf-animate-skeleton" />
                ) : (
                  <>
                    <StatLine label="Direct printers" value={directPrinterCount} variant="default" />
                    <StatLine label="Subtree printers" value={stats.totalPrinters} variant="primary" />
                    <p className="text-xs text-pf-text-tertiary">
                      Counts include the selected node and all nested child locations.
                    </p>
                  </>
                )}
              </SummaryCard>
            </div>
          </section>

          {manageMode && isFarmAdmin ? (
            <Tabs defaultTab="locations">
              <Tabs.List aria-label="Location management sections" className="rounded-t-xl border border-pf-border border-b-0 bg-pf-bg-1">
                <Tabs.Tab id="locations" icon={<LocationIcon className="h-4 w-4" />}>Locations</Tabs.Tab>
                <Tabs.Tab id="assignments" icon={<PrinterIcon className="h-4 w-4" />}>Assignments</Tabs.Tab>
              </Tabs.List>
              <Tabs.Panels className="rounded-b-xl">
                <Tabs.Panel id="locations">
                  <LocationManagement
                    embedded
                    showAssignments={false}
                    autoOpenCreateToken={createToken}
                    initialParentId={selectedLocationId}
                  />
                </Tabs.Panel>
                <Tabs.Panel id="assignments">
                  <div className="mb-4 rounded-lg border border-pf-warning/40 bg-pf-warning-bg px-4 py-3 text-sm text-pf-warning-text">
                    <AlertIcon className="mr-2 inline h-4 w-4" />
                    Printer assignment is admin-only in the UI. Backend endpoint hardening is tracked separately.
                  </div>
                  <PrinterLocationDragDrop locations={allLocations} />
                </Tabs.Panel>
              </Tabs.Panels>
            </Tabs>
          ) : (
            <section aria-labelledby="location-detail-heading" className="space-y-6">
              <Card>
                <Card.Header>
                  <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
                    <div>
                      <h2 id="location-detail-heading" className="text-xl font-semibold text-pf-text-primary">
                        {selectedNode?.name ?? 'All Locations'}
                      </h2>
                      <p className="mt-1 text-sm text-pf-text-secondary">
                        {selectedNode?.description || 'Rollup view across every top-level location.'}
                      </p>
                    </div>
                    <Badge variant="primary" size="md">{selectedPath}</Badge>
                  </div>
                </Card.Header>
                <Card.Body className="grid gap-4 md:grid-cols-3">
                  <div className="rounded-lg bg-pf-bg-1 p-4">
                    <p className="text-sm text-pf-text-secondary">Child locations</p>
                    <p className="mt-2 text-3xl font-semibold text-pf-text-primary">{childLocations.length}</p>
                  </div>
                  <div className="rounded-lg bg-pf-bg-1 p-4">
                    <p className="text-sm text-pf-text-secondary">Status rollup</p>
                    <p className="mt-2 text-3xl font-semibold text-pf-text-primary">
                      {stats.online}/{stats.totalPrinters}
                    </p>
                    <p className="text-xs text-pf-text-tertiary">online printers</p>
                  </div>
                  <div className="rounded-lg bg-pf-bg-1 p-4">
                    <p className="text-sm text-pf-text-secondary">Needs attention</p>
                    <p className={clsx('mt-2 text-3xl font-semibold', stats.attention > 0 ? 'text-pf-warning' : 'text-pf-success')}>
                      {stats.attention}
                    </p>
                  </div>
                </Card.Body>
              </Card>

              <Card>
                <Card.Header>
                  <h2 className="text-lg font-semibold text-pf-text-primary">Child locations</h2>
                </Card.Header>
                <Card.Body>
                  {childLocations.length === 0 ? (
                    <p className="text-sm text-pf-text-secondary">No child locations under this selection.</p>
                  ) : (
                    <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
                      {childLocations.map((child) => (
                        <Button
                          key={child.id}
                          type="button"
                          variant="unstyled"
                          className="rounded-lg border border-pf-border bg-pf-bg-1 p-4 text-left transition-colors hover:border-pf-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent"
                          onClick={() => setSelectedLocationId(child.id)}
                        >
                          <span className="block font-semibold text-pf-text-primary">{child.name}</span>
                          <span className="mt-1 block text-sm text-pf-text-secondary">
                            {child.description || child.path || 'No description'}
                          </span>
                          <span className="mt-3 flex gap-2">
                            <Badge variant="default">{child.printerCount} direct</Badge>
                            <Badge variant="primary">{child.totalPrinterCount} total</Badge>
                          </span>
                        </Button>
                      ))}
                    </div>
                  )}
                </Card.Body>
              </Card>

              <Card>
                <Card.Header>
                  <h2 className="text-lg font-semibold text-pf-text-primary">Active jobs</h2>
                </Card.Header>
                <Card.Body>
                  {activeJobs.length === 0 ? (
                    <p className="text-sm text-pf-text-secondary">No active jobs in this location selection.</p>
                  ) : (
                    <div className="space-y-3">
                      {activeJobs.map((printer) => (
                        <div key={printer.printerId} className="rounded-lg border border-pf-border bg-pf-bg-1 p-4">
                          <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                            <div>
                              <p className="font-medium text-pf-text-primary">{printer.printerName}</p>
                              <p className="text-sm text-pf-text-secondary">
                               {printer.currentJobName || 'Printing'} · {printer.locationName ?? 'Unassigned'}
                              </p>
                            </div>
                            <Badge variant="primary">{printer.status}</Badge>
                          </div>
                        </div>
                      ))}
                    </div>
                  )}
                </Card.Body>
              </Card>

              <div>
                <h2 className="mb-3 text-lg font-semibold text-pf-text-primary">Printers</h2>
                <LocationPrinterList
                  printers={printerList}
                  isLoading={printersLoading}
                  onPrinterClick={handlePrinterClick}
                />
              </div>
            </section>
          )}
        </div>
      </div>
    </PageTemplate>
  );
};
