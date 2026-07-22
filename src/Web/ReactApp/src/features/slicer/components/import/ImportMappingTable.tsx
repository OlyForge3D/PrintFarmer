import { useState } from 'react';
import clsx from 'clsx';
import { Badge, Button, Checkbox, Input, Card } from '@/common/components/ui';
import { ChevronDownIcon, ChevronRightIcon, SearchIcon } from '@/common/components/icons/MdiIcons';

export interface FieldMapping {
  sourceKey: string;
  targetKey: string;
  sourceValue: unknown;
  mappedValue: unknown;
  status: 'mapped' | 'unmapped' | 'transformed' | 'ignored';
  note?: string;
}

export interface ImportMappingTableProps {
  mappings: FieldMapping[];
  profileName: string;
  showUnmapped?: boolean;
  className?: string;
}

const statusConfig = {
  mapped: { label: 'Mapped', variant: 'success' as const },
  unmapped: { label: 'Unmapped', variant: 'error' as const },
  transformed: { label: 'Transformed', variant: 'warning' as const },
  ignored: { label: 'Ignored', variant: 'default' as const },
};

export function ImportMappingTable({
  mappings,
  profileName,
  showUnmapped: defaultShowUnmapped = false,
  className,
}: ImportMappingTableProps) {
  const [showUnmapped, setShowUnmapped] = useState(defaultShowUnmapped);
  const [searchQuery, setSearchQuery] = useState('');
  const [expandedRows, setExpandedRows] = useState<Set<number>>(new Set());
  const [statusFilter, setStatusFilter] = useState<string>('all');

  const toggleRow = (index: number) => {
    const newExpanded = new Set(expandedRows);
    if (newExpanded.has(index)) {
      newExpanded.delete(index);
    } else {
      newExpanded.add(index);
    }
    setExpandedRows(newExpanded);
  };

  const formatValue = (value: unknown): string => {
    if (value === null || value === undefined) return '—';
    if (typeof value === 'object') return JSON.stringify(value, null, 2);
    return String(value);
  };

  const filteredMappings = mappings.filter((mapping) => {
    if (!showUnmapped && mapping.status === 'unmapped') return false;
    if (statusFilter !== 'all' && mapping.status !== statusFilter) return false;
    if (searchQuery) {
      const query = searchQuery.toLowerCase();
      return (
        mapping.sourceKey.toLowerCase().includes(query) ||
        mapping.targetKey.toLowerCase().includes(query) ||
        formatValue(mapping.sourceValue).toLowerCase().includes(query)
      );
    }
    return true;
  });

  const statusCounts = mappings.reduce(
    (acc, mapping) => {
      acc[mapping.status]++;
      return acc;
    },
    { mapped: 0, unmapped: 0, transformed: 0, ignored: 0 }
  );

  return (
    <div className={clsx('space-y-4', className)}>
      {/* Header */}
      <div>
        <h3 className="text-lg font-semibold text-pf-text-primary">
          Field Mappings: {profileName}
        </h3>
        <p className="text-sm text-pf-text-secondary mt-1">
          {filteredMappings.length} of {mappings.length} fields
        </p>
      </div>

      {/* Filters */}
      <Card>
        <Card.Body className="p-4 space-y-3">
          <div className="flex items-center gap-4 flex-wrap">
            {/* Search */}
            <div className="flex-1 min-w-[200px]">
              <div className="relative">
                <SearchIcon className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-pf-text-tertiary" />
                <Input
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                  placeholder="Search fields..."
                  className="pl-9"
                />
              </div>
            </div>

            {/* Status filter */}
            <div className="flex items-center gap-2">
              <Button
                variant="unstyled"
                onClick={() => setStatusFilter('all')}
                className={clsx(
                  'px-3 py-1.5 text-sm rounded transition-colors',
                  statusFilter === 'all'
                    ? 'bg-pf-accent text-white'
                    : 'bg-pf-bg-1 text-pf-text-secondary hover:bg-pf-bg-2'
                )}
              >
                All ({mappings.length})
              </Button>
              {Object.entries(statusCounts).map(([status, count]) => (
                <Button
                  key={status}
                  variant="unstyled"
                  onClick={() => setStatusFilter(status)}
                  className={clsx(
                    'px-3 py-1.5 text-sm rounded transition-colors',
                    statusFilter === status
                      ? 'bg-pf-accent text-white'
                      : 'bg-pf-bg-1 text-pf-text-secondary hover:bg-pf-bg-2'
                  )}
                >
                  {statusConfig[status as keyof typeof statusConfig].label} ({count})
                </Button>
              ))}
            </div>

            {/* Show unmapped toggle */}
            <Checkbox
              label="Show unmapped"
              checked={showUnmapped}
              onChange={(e) => setShowUnmapped(e.target.checked)}
            />
          </div>
        </Card.Body>
      </Card>

      {/* Table */}
      <div className="border border-pf-border rounded-lg overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full">
            <thead className="bg-pf-bg-1 border-b border-pf-border">
              <tr>
                <th className="w-8 px-4 py-3"></th>
                <th className="px-4 py-3 text-left text-sm font-medium text-pf-text-primary">
                  Source Key
                </th>
                <th className="px-4 py-3 text-left text-sm font-medium text-pf-text-primary">
                  Source Value
                </th>
                <th className="w-12 px-4 py-3 text-center">→</th>
                <th className="px-4 py-3 text-left text-sm font-medium text-pf-text-primary">
                  Target Key
                </th>
                <th className="px-4 py-3 text-left text-sm font-medium text-pf-text-primary">
                  Mapped Value
                </th>
                <th className="px-4 py-3 text-left text-sm font-medium text-pf-text-primary">
                  Status
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-pf-border">
              {filteredMappings.length === 0 ? (
                <tr>
                  <td colSpan={7} className="px-4 py-8 text-center text-sm text-pf-text-tertiary">
                    No fields match the current filters
                  </td>
                </tr>
              ) : (
                filteredMappings.map((mapping, index) => {
                  const isExpanded = expandedRows.has(index);
                  const hasNote = mapping.note || mapping.status === 'transformed';

                  return (
                    <>
                      <tr
                        key={index}
                        className={clsx(
                          'bg-pf-bg-0 hover:bg-pf-bg-1 transition-colors',
                          hasNote && 'cursor-pointer'
                        )}
                        onClick={hasNote ? () => toggleRow(index) : undefined}
                      >
                        <td className="px-4 py-3">
                          {hasNote && (
                            <Button variant="ghost" size="sm" className="!p-0 text-pf-text-secondary hover:text-pf-text-primary">
                              {isExpanded ? (
                                <ChevronDownIcon className="w-4 h-4" />
                              ) : (
                                <ChevronRightIcon className="w-4 h-4" />
                              )}
                            </Button>
                          )}
                        </td>
                        <td className="px-4 py-3 text-sm font-mono text-pf-text-primary">
                          {mapping.sourceKey}
                        </td>
                        <td className="px-4 py-3 text-sm text-pf-text-secondary max-w-xs truncate">
                          {formatValue(mapping.sourceValue)}
                        </td>
                        <td className="px-4 py-3 text-center text-pf-text-tertiary">→</td>
                        <td className="px-4 py-3 text-sm font-mono text-pf-text-primary">
                          {mapping.targetKey || '—'}
                        </td>
                        <td className="px-4 py-3 text-sm text-pf-text-secondary max-w-xs truncate">
                          {formatValue(mapping.mappedValue)}
                        </td>
                        <td className="px-4 py-3">
                          <Badge
                            variant={statusConfig[mapping.status].variant}
                            size="sm"
                          >
                            {statusConfig[mapping.status].label}
                          </Badge>
                        </td>
                      </tr>
                      {isExpanded && mapping.note && (
                        <tr>
                          <td colSpan={7} className="px-4 py-3 bg-pf-bg-1">
                            <div className="text-sm text-pf-text-secondary">
                              <strong className="font-medium">Note:</strong> {mapping.note}
                            </div>
                          </td>
                        </tr>
                      )}
                    </>
                  );
                })
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
