import { Button } from '@/common/components/ui';
import { Checkbox } from '@/common/components/ui/Checkbox';
import { ArrowUpIcon, ArrowDownIcon, EditIcon, CopyIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
import { SelectableRow } from '@/common/components/Table/SelectableRow';
import type { SpoolmanFilament } from '@/types/api';
import type { FilamentTableColumn } from '@/features/filamentManagement/types';

type SortField = string;

interface FilamentTableViewProps {
  filaments: SpoolmanFilament[];
  selectedIds: Set<number>;
  allSelected: boolean;
  sortField: SortField;
  sortDir: 'asc' | 'desc';
  tableColumns: FilamentTableColumn[];
  onToggleSelect: (id: number) => void;
  onToggleSelectAll: () => void;
  onSort: (field: string, dir: 'asc' | 'desc') => void;
  onEdit: (f: SpoolmanFilament) => void;
  onClone: (f: SpoolmanFilament) => void;
  onDelete: (f: SpoolmanFilament) => void;
}

/** Table view for Spoolman filament product definitions with dynamic columns. */
export function FilamentTableView({
  filaments,
  selectedIds,
  allSelected,
  sortField,
  sortDir,
  tableColumns,
  onToggleSelect,
  onToggleSelectAll,
  onSort,
  onEdit,
  onClone,
  onDelete,
}: FilamentTableViewProps) {
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
                aria-label={allSelected ? 'Deselect all filaments' : 'Select all filaments'}
              />
            </th>
            {visibleColumns.map(col => {
              const isSorted = sortField === col.id;
              const ariaSort: 'ascending' | 'descending' | undefined = isSorted ? (sortDir === 'asc' ? 'ascending' : 'descending') : undefined;
              return (
                <th
                  key={col.id}
                  className={`px-3 py-2 font-medium ${col.sortable ? 'cursor-pointer select-none' : ''}`}
                  onClick={() => {
                    if (!col.sortable) return;
                    onSort(col.id, isSorted ? (sortDir === 'asc' ? 'desc' : 'asc') : 'asc');
                  }}
                  {...(ariaSort ? { 'aria-sort': ariaSort } : {})}
                >
                  <span className="inline-flex items-center gap-1">
                    {col.label}
                    {col.sortable && isSorted && (
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
          {filaments.map(f => (
            <SelectableRow key={f.id} className="border-t border-pf-border" isSelected={selectedIds.has(f.id)}>
              <td className="px-3 py-2">
                <Checkbox
                  checked={selectedIds.has(f.id)}
                  onChange={() => onToggleSelect(f.id)}
                  aria-label={`Select ${f.name || 'filament'}`}
                />
              </td>
              {visibleColumns.map(col => (
                <td key={col.id} className="px-3 py-2">{col.render(f)}</td>
              ))}
              <td className="px-3 py-2">
                <div className="flex gap-1">
                  <Button variant="subtle" size="sm" onClick={() => onEdit(f)} aria-label={`Edit ${f.name || 'filament'}`} title="Edit filament">
                    <EditIcon className="h-3.5 w-3.5" />
                  </Button>
                  <Button variant="subtle" size="sm" onClick={() => onClone(f)} aria-label={`Clone ${f.name || 'filament'}`} title="Clone filament">
                    <CopyIcon className="h-3.5 w-3.5" />
                  </Button>
                  <Button variant="subtle" size="sm" onClick={() => onDelete(f)} aria-label={`Delete ${f.name || 'filament'}`} title="Delete filament">
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
