import React, { useState, useCallback } from 'react';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Spinner, Card, Button } from '@/common/components/ui';
import { LocationIcon } from '@/common/components/icons/MdiIcons';
import { LocationStats } from '@/features/locations/components/LocationStats';
import { LocationPrinterList } from '@/features/locations/components/LocationPrinterList';
import {
  useLocationTree,
  useLocationPrinters,
  useLocationStats,
  useSignalRPrinterUpdates,
  findNode,
} from '@/features/locations/hooks/useLocationDashboard';
import type { LocationTreeNode } from '@/services/locationService';
import clsx from 'clsx';

interface TreeNodeProps {
  node: LocationTreeNode;
  selectedId: string | null;
  onSelect: (id: string) => void;
  depth?: number;
}

function TreeNode({ node, selectedId, onSelect, depth = 0 }: TreeNodeProps) {
  const [expanded, setExpanded] = useState(depth < 2);
  const hasChildren = node.children.length > 0;
  const isSelected = node.id === selectedId;

  return (
    <div>
      <Button
        variant="unstyled"
        className={clsx(
          'w-full text-left px-3 py-2 rounded-md text-sm flex items-center gap-2 transition-colors',
          isSelected
            ? 'bg-pf-accent-bg text-pf-accent font-medium'
            : 'hover:bg-pf-bg-1 text-pf-text-primary',
        )}
        style={{ paddingLeft: `${depth * 16 + 12}px` }}
        onClick={() => onSelect(node.id)}
        aria-current={isSelected ? 'true' : undefined}
      >
        {hasChildren && (
          <span
            className="w-4 h-4 flex items-center justify-center text-pf-text-secondary cursor-pointer"
            onClick={e => {
              e.stopPropagation();
              setExpanded(!expanded);
            }}
            role="button"
            tabIndex={0}
            onKeyDown={e => {
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                e.stopPropagation();
                setExpanded(!expanded);
              }
            }}
            aria-label={expanded ? 'Collapse' : 'Expand'}
          >
            {expanded ? '▾' : '▸'}
          </span>
        )}
        {!hasChildren && <span className="w-4" />}
        <span className="truncate">{node.name}</span>
        {node.totalPrinterCount > 0 && (
          <span className="ml-auto text-xs text-pf-text-secondary">
            {node.totalPrinterCount}
          </span>
        )}
      </Button>
      {expanded && hasChildren && (
        <div>
          {node.children.map(child => (
            <TreeNode
              key={child.id}
              node={child}
              selectedId={selectedId}
              onSelect={onSelect}
              depth={depth + 1}
            />
          ))}
        </div>
      )}
    </div>
  );
}

export const LocationDashboardPage: React.FC = () => {
  const [selectedLocationId, setSelectedLocationId] = useState<string | null>(null);
  const { data: tree = [], isLoading: treeLoading, error: treeError } = useLocationTree();
  const { stats, isLoading: statsLoading } = useLocationStats(selectedLocationId);
  const { data: printers = [], isLoading: printersLoading } = useLocationPrinters(selectedLocationId);

  useSignalRPrinterUpdates();

  const selectedNode = selectedLocationId ? findNode(tree, selectedLocationId) : null;
  const locationName = selectedNode?.name ?? 'All Locations';

  const handlePrinterClick = useCallback((printerId: string) => {
    window.location.href = `/printers?selected=${printerId}`;
  }, []);

  if (treeLoading) {
    return (
      <PageTemplate title="Location Dashboard" icon={LocationIcon}>
        <div className="flex items-center justify-center py-12">
          <Spinner size="lg" />
        </div>
      </PageTemplate>
    );
  }

  if (treeError) {
    return (
      <PageTemplate title="Location Dashboard" icon={LocationIcon}>
        <div className="p-4 text-pf-error">
          Failed to load locations: {String(treeError)}
        </div>
      </PageTemplate>
    );
  }

  return (
    <PageTemplate title="Location Dashboard" icon={LocationIcon}>
      <div className="flex flex-col lg:flex-row gap-6">
        {/* Tree picker sidebar */}
        <Card className="lg:w-72 flex-shrink-0">
          <Card.Header>
            <h2 className="text-sm font-semibold text-pf-text-primary">Locations</h2>
          </Card.Header>
          <Card.Body className="p-2 max-h-[60vh] overflow-y-auto">
            <Button
              variant="unstyled"
              className={clsx(
                'w-full text-left px-3 py-2 rounded-md text-sm transition-colors',
                selectedLocationId === null
                  ? 'bg-pf-accent-bg text-pf-accent font-medium'
                  : 'hover:bg-pf-bg-1 text-pf-text-primary',
              )}
              onClick={() => setSelectedLocationId(null)}
              aria-current={selectedLocationId === null ? 'true' : undefined}
            >
              All Locations
            </Button>
            {tree.map(node => (
              <TreeNode
                key={node.id}
                node={node}
                selectedId={selectedLocationId}
                onSelect={setSelectedLocationId}
              />
            ))}
          </Card.Body>
        </Card>

        {/* Dashboard content */}
        <div className="flex-1 space-y-6">
          <LocationStats
            stats={stats}
            locationName={locationName}
            isLoading={statsLoading}
          />
          <div>
            <h3 className="text-lg font-semibold text-pf-text-primary mb-3">
              Printers
            </h3>
            <LocationPrinterList
              printers={printers}
              isLoading={printersLoading}
              onPrinterClick={handlePrinterClick}
            />
          </div>
        </div>
      </div>
    </PageTemplate>
  );
};
