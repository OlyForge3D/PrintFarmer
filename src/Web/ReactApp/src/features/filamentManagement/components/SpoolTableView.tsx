import { Button } from '@/common/components/ui';
import { Checkbox } from '@/common/components/ui/Checkbox';
import { ArrowUpIcon, ArrowDownIcon, EditIcon, CopyIcon, DeleteIcon, TagIcon } from '@/common/components/icons/MdiIcons';
import { SelectableRow } from '@/common/components/Table/SelectableRow';
import type { SpoolmanSpoolDto, SpoolTableColumn } from '@/features/filamentManagement/types';

interface SpoolTableViewProps {
  spools: SpoolmanSpoolDto[];
  tableColumns: SpoolTableColumn[];
  selectedIds: Set<number>;
  allSelected: boolean;
  sortField: string;
  sortDir: 'asc' | 'desc';
  onSort: (field: string, dir: 'asc' | 'desc') => void;
  onToggleSelect: (id: number) => void;
  onToggleSelectAll: () => void;
  onEdit: (s: SpoolmanSpoolDto) => void;
  onClone: (s: SpoolmanSpoolDto) => void;
  onDelete: (s: SpoolmanSpoolDto) => void;
  onPrintLabel: (s: SpoolmanSpoolDto) => void;
}

/** Table view for Spoolman physical spools with dynamic column configuration. */
export function SpoolTableView({
  spools,
  tableColumns,
  selectedIds,
  allSelected,
  sortField,
  sortDir,
  onSort,
  onToggleSelect,
  onToggleSelectAll,
  onEdit,
  onClone,
  onDelete,
  onPrintLabel,
}: SpoolTableViewProps) {
  const visibleColumns = tableColumns.filter(c => c.visible);

  return (
    <div className="overflow-x-auto relative">
      <table className="min-w-full text-sm">
        <thead>
          <tr className="text-left bg-pf-bg-2">
            <th className="px-3 py-2 w-10">
              <Checkbox
                checked={allSelected}
                onChange={onToggleSelectAll}
                aria-label={allSelected ? 'Deselect all spools' : 'Select all spools'}
              />
            </th>
            {visibleColumns.map(c => {
              const isSorted = sortField === c.id;
              const ariaSort: 'ascending' | 'descending' | undefined = isSorted ? (sortDir === 'asc' ? 'ascending' : 'descending') : undefined;
              return (
                <th
                  key={c.id}
                  data-col-id={c.id}
                  className={`px-3 py-2 font-medium ${c.sortable ? 'cursor-pointer select-none' : ''}`}
                  onClick={() => {
                    if (!c.sortable) return;
                    onSort(c.id, isSorted ? (sortDir === 'asc' ? 'desc' : 'asc') : 'asc');
                  }}
                  {...(ariaSort ? { 'aria-sort': ariaSort } : {})}
                >
                  <span className="inline-flex items-center gap-1">
                    {c.label}
                    {c.sortable && isSorted && (
                      sortDir === 'asc' ? <ArrowUpIcon className="h-3 w-3" /> : <ArrowDownIcon className="h-3 w-3" />
                    )}
                  </span>
                </th>
              );
            })}
            <th className="px-3 py-2 font-medium w-16">Actions</th>
          </tr>
        </thead>
        <tbody>
          {spools.map(spool => (
            <SelectableRow key={spool.id} className="border-t border-pf-border" isSelected={selectedIds.has(spool.id)}>
              <td className="px-3 py-2">
                <Checkbox
                  checked={selectedIds.has(spool.id)}
                  onChange={() => onToggleSelect(spool.id)}
                  aria-label={`Select spool #${spool.id}`}
                />
              </td>
              {visibleColumns.map(c => (
                <td key={c.id} className="px-3 py-2" data-col-id={c.id}>{c.render(spool)}</td>
              ))}
              <td className="px-3 py-2">
                <div className="flex gap-1">
                  <Button variant="subtle" size="sm" onClick={() => onEdit(spool)} aria-label={`Edit spool #${spool.id}`} title="Edit spool">
                    <EditIcon className="h-3.5 w-3.5" />
                  </Button>
                  <Button variant="subtle" size="sm" onClick={() => onPrintLabel(spool)} aria-label={`Print label for spool #${spool.id}`} title="Print label">
                    <TagIcon className="h-3.5 w-3.5" />
                  </Button>
                  <Button variant="subtle" size="sm" onClick={() => onClone(spool)} aria-label={`Clone spool #${spool.id}`} title="Clone spool">
                    <CopyIcon className="h-3.5 w-3.5" />
                  </Button>
                  <Button variant="subtle" size="sm" onClick={() => onDelete(spool)} aria-label={`Delete spool #${spool.id}`} title="Delete spool">
                    <DeleteIcon className="h-3.5 w-3.5" />
                  </Button>
                </div>
              </td>
            </SelectableRow>
          ))}
        </tbody>
      </table>
    </div>
  );
}
