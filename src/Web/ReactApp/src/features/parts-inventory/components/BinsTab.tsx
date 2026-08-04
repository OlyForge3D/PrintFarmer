import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { Badge, Button, EmptyState, Input, Spinner, Toggle } from '@/common/components/ui';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import {
  PlusIcon,
  EditIcon,
  DeleteIcon,
  SearchIcon,
  DatabaseIcon,
  BarcodeScanIcon,
} from '@/common/components/icons/MdiIcons';
import { useBins, useDeleteBin } from '../hooks/usePartsInventory';
import type { BinDto } from '@/types/partsInventory';
import { getErrorMessage } from '../utils/problemDetails';
import { BinFormModal } from './BinFormModal';
import { RegisterBinModal } from './RegisterBinModal';

/**
 * BinsTab — CRUD list of storage bins. Codes double as scannable
 * barcodes; the {@link RegisterBinModal} auto-creates a bin from a
 * previously-unknown code so operators can label new locations without
 * leaving the scan flow.
 */
export function BinsTab() {
  const [includeInactive, setIncludeInactive] = useState(false);
  const [filter, setFilter] = useState('');
  const [editing, setEditing] = useState<BinDto | null>(null);
  const [isCreating, setIsCreating] = useState(false);
  const [isRegistering, setIsRegistering] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState<BinDto | null>(null);

  const { data: bins = [], isLoading, error } = useBins({ includeInactive });
  const deleteBin = useDeleteBin();

  const filtered = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    if (!needle) return bins;
    return bins.filter((bin) => {
      const haystack = [bin.code, bin.name, bin.location ?? '', bin.notes ?? '']
        .join(' ')
        .toLowerCase();
      return haystack.includes(needle);
    });
  }, [bins, filter]);

  const handleDelete = async () => {
    if (!confirmDelete) return;
    try {
      await deleteBin.mutateAsync(confirmDelete.code);
      toast.success(`Deactivated bin ${confirmDelete.code}`);
      setConfirmDelete(null);
    } catch (err) {
      toast.error(getErrorMessage(err, 'Failed to deactivate bin'));
    }
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-col sm:flex-row items-stretch sm:items-center gap-2">
        <div className="relative flex-1">
          <label htmlFor="bins-filter" className="sr-only">
            Search bins
          </label>
          <SearchIcon
            className="w-4 h-4 absolute left-2 top-1/2 -translate-y-1/2 text-pf-text-secondary pointer-events-none"
            ariaLabel="Search"
          />
          <Input
            id="bins-filter"
            value={filter}
            onChange={(event) => setFilter(event.target.value)}
            placeholder="Filter by code, name, location, or notes"
            className="pl-8"
          />
        </div>
        <div className="flex items-center gap-3">
          <Toggle
            checked={includeInactive}
            onChange={(event) => setIncludeInactive(event.target.checked)}
            label="Show inactive"
          />
          <Button
            variant="secondary"
            size="sm"
            iconLeft={<BarcodeScanIcon className="w-4 h-4" ariaLabel="Scan" />}
            onClick={() => setIsRegistering(true)}
          >
            Register barcode
          </Button>
          <Button
            variant="primary"
            size="sm"
            iconLeft={<PlusIcon className="w-4 h-4" ariaLabel="Add" />}
            onClick={() => setIsCreating(true)}
          >
            Add bin
          </Button>
        </div>
      </div>

      {isLoading && (
        <div className="flex items-center gap-2 py-8 justify-center text-pf-text-secondary">
          <Spinner size="md" />
          <span>Loading bins…</span>
        </div>
      )}

      {error && (
        <div className="p-3 border border-pf-error-border bg-pf-error-bg rounded-sm text-pf-error-text text-sm" role="alert">
          Failed to load bins.
        </div>
      )}

      {!isLoading && !error && filtered.length === 0 && (
        <EmptyState
          icon={<DatabaseIcon className="w-8 h-8 text-pf-text-secondary" ariaLabel="Empty" />}
          title={filter ? 'No bins match your filter' : 'No bins registered yet'}
          description={
            filter
              ? 'Try a different search term.'
              : 'Register a bin barcode or add a bin manually to start.'
          }
          action={
            !filter ? (
              <Button variant="primary" size="sm" onClick={() => setIsCreating(true)}>
                Add first bin
              </Button>
            ) : undefined
          }
        />
      )}

      {filtered.length > 0 && (
        <div className="overflow-x-auto">
          <table className="min-w-full text-sm">
            <thead className="bg-pf-bg-1 text-pf-text-secondary">
              <tr>
                <th scope="col" className="px-3 py-2 text-left font-medium">Code</th>
                <th scope="col" className="px-3 py-2 text-left font-medium">Name</th>
                <th scope="col" className="px-3 py-2 text-left font-medium">Location</th>
                <th scope="col" className="px-3 py-2 text-left font-medium">Notes</th>
                <th scope="col" className="px-3 py-2 text-left font-medium">Status</th>
                <th scope="col" className="px-3 py-2 text-right font-medium">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-pf-border">
              {filtered.map((bin) => (
                <tr key={bin.id} className={bin.isActive ? '' : 'opacity-60'}>
                  <td className="px-3 py-2 font-mono text-pf-text-primary">{bin.code}</td>
                  <td className="px-3 py-2">{bin.name}</td>
                  <td className="px-3 py-2 text-pf-text-secondary">{bin.location ?? '—'}</td>
                  <td className="px-3 py-2 text-pf-text-secondary">{bin.notes ?? '—'}</td>
                  <td className="px-3 py-2">
                    {bin.isActive ? (
                      <Badge variant="success" size="sm">Active</Badge>
                    ) : (
                      <Badge variant="default" size="sm">Inactive</Badge>
                    )}
                  </td>
                  <td className="px-3 py-2 text-right">
                    <div className="inline-flex gap-1" role="group" aria-label={`Actions for bin ${bin.code}`}>
                      <Button
                        variant="ghost"
                        size="sm"
                        iconCenter={<EditIcon className="w-4 h-4" ariaLabel={`Edit bin ${bin.code}`} />}
                        onClick={() => setEditing(bin)}
                        aria-label={`Edit bin ${bin.code}`}
                      />
                      {bin.isActive && (
                        <Button
                          variant="ghost"
                          size="sm"
                          iconCenter={
                            <DeleteIcon
                              className="w-4 h-4"
                              ariaLabel={`Deactivate bin ${bin.code}`}
                            />
                          }
                          onClick={() => setConfirmDelete(bin)}
                          aria-label={`Deactivate bin ${bin.code}`}
                        />
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <BinFormModal
        isOpen={isCreating || Boolean(editing)}
        onClose={() => {
          setIsCreating(false);
          setEditing(null);
        }}
        bin={editing}
      />
      <RegisterBinModal isOpen={isRegistering} onClose={() => setIsRegistering(false)} />
      <ConfirmationModal
        isOpen={Boolean(confirmDelete)}
        title="Deactivate bin"
        message={
          confirmDelete
            ? `Deactivate bin ${confirmDelete.code}? Historical ledger entries and scan logs are retained.`
            : ''
        }
        confirmButtonText="Deactivate"
        isDangerous
        isConfirming={deleteBin.isPending}
        onCancel={() => setConfirmDelete(null)}
        onConfirm={handleDelete}
      />
    </div>
  );
}
