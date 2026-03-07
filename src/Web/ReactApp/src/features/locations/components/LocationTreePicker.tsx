import React, { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';
import type { LocationTreeNode } from '@/types/api';
import { locationService } from '@/services/locationService';

export interface LocationTreePickerProps {
  value?: string | null;
  onChange: (locationId: string | null) => void;
  label?: string;
  required?: boolean;
  disabled?: boolean;
  excludeId?: string;
  placeholder?: string;
}

interface TreeRowProps {
  node: LocationTreeNode;
  depth: number;
  selectedId: string | null;
  expandedIds: Set<string>;
  onSelect: (id: string) => void;
  onToggle: (id: string) => void;
  searchTerm: string;
}

function matchesSearch(node: LocationTreeNode, term: string): boolean {
  if (!term) return true;
  const lower = term.toLowerCase();
  if (node.name.toLowerCase().includes(lower)) return true;
  return node.children.some((c) => matchesSearch(c, term));
}

const TreeRow: React.FC<TreeRowProps> = ({
  node,
  depth,
  selectedId,
  expandedIds,
  onSelect,
  onToggle,
  searchTerm,
}) => {
  const isExpanded = expandedIds.has(node.id);
  const hasChildren = node.children.length > 0;
  const isSelected = selectedId === node.id;

  if (searchTerm && !matchesSearch(node, searchTerm)) {
    return null;
  }

  return (
    <>
      <Button
        variant="unstyled"
        className={clsx(
          'flex items-center w-full px-3 py-2 text-sm text-left hover:bg-pf-bg-2 transition-colors',
          isSelected && 'bg-pf-accent-bg/20 text-pf-accent font-medium',
        )}
        style={{ paddingLeft: `${depth * 20 + 12}px` }}
        onClick={() => onSelect(node.id)}
        role="treeitem"
        aria-selected={isSelected}
        aria-expanded={hasChildren ? isExpanded : undefined}
      >
        {hasChildren ? (
          <span
            className="mr-1.5 w-4 h-4 flex items-center justify-center cursor-pointer text-pf-text-secondary hover:text-pf-text-primary"
            onClick={(e) => {
              e.stopPropagation();
              onToggle(node.id);
            }}
            role="button"
            aria-label={isExpanded ? 'Collapse' : 'Expand'}
            tabIndex={-1}
          >
            {isExpanded ? '▾' : '▸'}
          </span>
        ) : (
          <span className="mr-1.5 w-4 h-4 flex items-center justify-center text-pf-text-tertiary">
            ·
          </span>
        )}
        <span className="truncate flex-1">{node.name}</span>
        {node.totalPrinterCount > 0 && (
          <span className="ml-2 text-xs text-pf-text-tertiary">{node.totalPrinterCount}</span>
        )}
      </Button>
      {hasChildren && isExpanded &&
        node.children.map((child) => (
          <TreeRow
            key={child.id}
            node={child}
            depth={depth + 1}
            selectedId={selectedId}
            expandedIds={expandedIds}
            onSelect={onSelect}
            onToggle={onToggle}
            searchTerm={searchTerm}
          />
        ))}
    </>
  );
};

function findNodeName(nodes: LocationTreeNode[], id: string): string | undefined {
  for (const node of nodes) {
    if (node.id === id) return node.name;
    const found = findNodeName(node.children, id);
    if (found) return found;
  }
  return undefined;
}

function findNodePath(nodes: LocationTreeNode[], id: string, ancestors: string[] = []): string | undefined {
  for (const node of nodes) {
    if (node.id === id) return [...ancestors, node.name].join(' / ');
    const found = findNodePath(node.children, id, [...ancestors, node.name]);
    if (found) return found;
  }
  return undefined;
}

function filterTree(nodes: LocationTreeNode[], excludeId?: string): LocationTreeNode[] {
  if (!excludeId) return nodes;
  return nodes
    .filter((n) => n.id !== excludeId)
    .map((n) => ({ ...n, children: filterTree(n.children, excludeId) }));
}

export const LocationTreePicker: React.FC<LocationTreePickerProps> = ({
  value,
  onChange,
  label = 'Location',
  required = false,
  disabled = false,
  excludeId,
  placeholder = 'Select a location...',
}) => {
  const [tree, setTree] = useState<LocationTreeNode[]>([]);
  const [loading, setLoading] = useState(false);
  const [isOpen, setIsOpen] = useState(false);
  const [searchTerm, setSearchTerm] = useState('');
  const [expandedIds, setExpandedIds] = useState<Set<string>>(new Set());
  const containerRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const load = async () => {
      try {
        setLoading(true);
        const data = await locationService.getLocationTree();
        setTree(data);
        // Auto-expand first level
        setExpandedIds(new Set(data.map((n) => n.id)));
      } catch {
        // Silently fail — dropdown just shows empty
      } finally {
        setLoading(false);
      }
    };
    load();
  }, []);

  // Close on outside click
  useEffect(() => {
    const handleClick = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setIsOpen(false);
        setSearchTerm('');
      }
    };
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, []);

  // Focus search on open
  useEffect(() => {
    if (isOpen && searchRef.current) {
      searchRef.current.focus();
    }
  }, [isOpen]);

  const handleToggle = useCallback((id: string) => {
    setExpandedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);

  const handleSelect = useCallback(
    (id: string) => {
      onChange(id);
      setIsOpen(false);
      setSearchTerm('');
    },
    [onChange],
  );

  const handleClear = useCallback(
    (e: React.MouseEvent) => {
      e.stopPropagation();
      onChange(null);
    },
    [onChange],
  );

  const filteredTree = useMemo(() => filterTree(tree, excludeId), [tree, excludeId]);

  const displayText = value ? (findNodePath(filteredTree, value) ?? findNodeName(filteredTree, value) ?? 'Unknown') : '';

  return (
    <div ref={containerRef} className="relative">
      {label && (
        <label className="block text-sm font-medium text-pf-text-primary mb-1">
          {label}
          {required && <span className="ml-1 text-pf-error">*</span>}
        </label>
      )}
      <Button
        variant="unstyled"
        disabled={disabled || loading}
        onClick={() => setIsOpen(!isOpen)}
        className={clsx(
          'flex items-center w-full px-3 py-2 text-sm text-left rounded-md border transition-colors',
          'bg-pf-bg-0 border-pf-border hover:border-pf-accent focus:outline-none focus:ring-2 focus:ring-pf-accent/50',
          disabled && 'opacity-50 cursor-not-allowed',
        )}
        aria-haspopup="tree"
        aria-expanded={isOpen}
      >
        <span className={clsx('flex-1 truncate', !value && 'text-pf-text-tertiary')}>
          {loading ? 'Loading...' : value ? displayText : placeholder}
        </span>
        {value && !disabled && (
          <span
            className="ml-1 text-pf-text-tertiary hover:text-pf-text-primary cursor-pointer"
            onClick={handleClear}
            role="button"
            aria-label="Clear selection"
            tabIndex={-1}
          >
            ✕
          </span>
        )}
        <span className="ml-2 text-pf-text-tertiary">▾</span>
      </Button>

      {isOpen && (
        <div
          className="absolute z-50 mt-1 w-full rounded-md border border-pf-border bg-pf-bg-1 shadow-lg max-h-72 overflow-hidden flex flex-col"
          role="tree"
        >
          <div className="p-2 border-b border-pf-border">
            <input
              ref={searchRef}
              type="text"
              value={searchTerm}
              onChange={(e) => setSearchTerm(e.target.value)}
              placeholder="Search locations..."
              className="w-full px-2 py-1.5 text-sm rounded border border-pf-border bg-pf-bg-0 text-pf-text-primary placeholder:text-pf-text-tertiary focus:outline-none focus:ring-1 focus:ring-pf-accent/50"
            />
          </div>
          {!required && (
            <Button
              variant="unstyled"
              className={clsx(
                'flex items-center w-full px-3 py-2 text-sm text-left hover:bg-pf-bg-2 text-pf-text-secondary italic',
                !value && 'bg-pf-accent-bg/20 font-medium',
              )}
              onClick={() => {
                onChange(null);
                setIsOpen(false);
                setSearchTerm('');
              }}
            >
              No location (unassigned)
            </Button>
          )}
          <div className="overflow-y-auto flex-1">
            {filteredTree.length === 0 ? (
              <div className="px-3 py-4 text-sm text-pf-text-tertiary text-center">
                No locations found
              </div>
            ) : (
              filteredTree.map((node) => (
                <TreeRow
                  key={node.id}
                  node={node}
                  depth={0}
                  selectedId={value ?? null}
                  expandedIds={expandedIds}
                  onSelect={handleSelect}
                  onToggle={handleToggle}
                  searchTerm={searchTerm}
                />
              ))
            )}
          </div>
        </div>
      )}
    </div>
  );
};
