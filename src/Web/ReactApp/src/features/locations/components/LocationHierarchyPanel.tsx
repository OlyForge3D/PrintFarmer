import React, { useMemo, useState } from 'react';
import clsx from 'clsx';
import { Button, Card, Input, Badge } from '@/common/components/ui';
import { SearchIcon } from '@/common/components/icons/MdiIcons';
import type { LocationTreeNode } from '@/types/api';

interface LocationHierarchyPanelProps {
  tree: LocationTreeNode[];
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  className?: string;
}

interface TreeRowProps {
  node: LocationTreeNode;
  depth: number;
  selectedId: string | null;
  expandedIds: Set<string>;
  searchTerm: string;
  onSelect: (id: string) => void;
  onToggle: (id: string) => void;
}

function collectDefaultExpandedIds(nodes: LocationTreeNode[]): Set<string> {
  const ids = new Set<string>();
  const visit = (node: LocationTreeNode) => {
    if (node.depth < 2 || node.children.length > 0) {
      ids.add(node.id);
    }
    node.children.forEach(visit);
  };
  nodes.forEach(visit);
  return ids;
}

function matchesSearch(node: LocationTreeNode, term: string): boolean {
  if (!term) return true;
  const normalized = term.toLowerCase();
  return (
    node.name.toLowerCase().includes(normalized)
    || (node.description?.toLowerCase().includes(normalized) ?? false)
    || node.children.some((child) => matchesSearch(child, term))
  );
}

function LocationTreeRow({
  node,
  depth,
  selectedId,
  expandedIds,
  searchTerm,
  onSelect,
  onToggle,
}: TreeRowProps) {
  const hasChildren = node.children.length > 0;
  const isExpanded = expandedIds.has(node.id);
  const isSelected = selectedId === node.id;

  if (!matchesSearch(node, searchTerm)) {
    return null;
  }

  return (
    <li>
      <div className="flex items-center gap-1" style={{ paddingLeft: `${depth * 18 + 4}px` }}>
        {hasChildren ? (
          <Button
            type="button"
            variant="unstyled"
            className="flex h-8 w-8 shrink-0 items-center justify-center rounded text-pf-text-secondary hover:bg-pf-bg-2 hover:text-pf-text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent"
            onClick={() => onToggle(node.id)}
            aria-label={`${isExpanded ? 'Collapse' : 'Expand'} ${node.name}`}
            aria-expanded={isExpanded}
          >
            {isExpanded ? '▾' : '▸'}
          </Button>
        ) : (
          <span className="flex h-8 w-8 shrink-0 items-center justify-center text-pf-text-tertiary" aria-hidden="true">
            ·
          </span>
        )}
        <Button
          type="button"
          variant="unstyled"
          className={clsx(
            'flex min-w-0 flex-1 items-center gap-2 rounded-lg px-3 py-2 text-left text-sm transition-colors',
            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent',
            isSelected
              ? 'bg-pf-accent-bg text-pf-accent font-semibold'
              : 'text-pf-text-primary hover:bg-pf-bg-1',
          )}
          onClick={() => onSelect(node.id)}
          aria-current={isSelected ? 'true' : undefined}
        >
          <span className="min-w-0 flex-1 truncate">{node.name}</span>
          <span className="flex shrink-0 items-center gap-1">
            {node.printerCount > 0 && (
              <span title="Direct printers">
                <Badge size="sm" variant="default">{node.printerCount}</Badge>
              </span>
            )}
            {node.totalPrinterCount !== node.printerCount && (
              <span title="Total subtree printers">
                <Badge size="sm" variant="primary">{node.totalPrinterCount}</Badge>
              </span>
            )}
          </span>
        </Button>
      </div>
      {hasChildren && isExpanded && (
        <ul className="mt-1 space-y-1">
          {node.children.map((child) => (
            <LocationTreeRow
              key={child.id}
              node={child}
              depth={depth + 1}
              selectedId={selectedId}
              expandedIds={expandedIds}
              searchTerm={searchTerm}
              onSelect={onSelect}
              onToggle={onToggle}
            />
          ))}
        </ul>
      )}
    </li>
  );
}

export function LocationHierarchyPanel({
  tree,
  selectedId,
  onSelect,
  className,
}: LocationHierarchyPanelProps) {
  const [searchTerm, setSearchTerm] = useState('');
  const [expandedIds, setExpandedIds] = useState<Set<string>>(() => collectDefaultExpandedIds(tree));

  const totalPrinters = useMemo(
    () => tree.reduce((sum, node) => sum + node.totalPrinterCount, 0),
    [tree],
  );

  const visibleTree = useMemo(
    () => tree.filter((node) => matchesSearch(node, searchTerm.trim())),
    [searchTerm, tree],
  );

  const toggleNode = (id: string) => {
    setExpandedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  return (
    <Card className={clsx('lg:w-80 lg:shrink-0', className)}>
      <Card.Header>
        <div className="space-y-3">
          <div>
            <h2 className="text-base font-semibold text-pf-text-primary">Hierarchy navigator</h2>
            <p className="text-sm text-pf-text-secondary">
              Search rooms, zones, and placement areas.
            </p>
          </div>
          <div className="relative">
            <SearchIcon className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-pf-text-secondary" />
            <Input
              value={searchTerm}
              onChange={(event) => setSearchTerm(event.target.value)}
              placeholder="Search locations"
              aria-label="Search locations"
              className="pl-9"
            />
          </div>
        </div>
      </Card.Header>
      <Card.Body className="max-h-[68vh] overflow-y-auto p-2">
        <Button
          type="button"
          variant="unstyled"
          className={clsx(
            'mb-2 flex w-full items-center justify-between rounded-lg px-3 py-2 text-left text-sm transition-colors',
            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent',
            selectedId === null
              ? 'bg-pf-accent-bg text-pf-accent font-semibold'
              : 'text-pf-text-primary hover:bg-pf-bg-1',
          )}
          onClick={() => onSelect(null)}
          aria-current={selectedId === null ? 'true' : undefined}
        >
          <span>All Locations</span>
          {totalPrinters > 0 && (
            <span title="Total printers">
              <Badge size="sm" variant="primary">{totalPrinters}</Badge>
            </span>
          )}
        </Button>
        {visibleTree.length === 0 ? (
          <p className="px-3 py-6 text-center text-sm text-pf-text-secondary">No locations match that search.</p>
        ) : (
          <ul className="space-y-1" aria-label="Location hierarchy">
            {visibleTree.map((node) => (
              <LocationTreeRow
                key={node.id}
                node={node}
                depth={0}
                selectedId={selectedId}
                expandedIds={expandedIds}
                searchTerm={searchTerm.trim()}
                onSelect={onSelect}
                onToggle={toggleNode}
              />
            ))}
          </ul>
        )}
      </Card.Body>
    </Card>
  );
}
