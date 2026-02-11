import { useState, useCallback, useEffect, useMemo } from 'react';
import { Button, Input, Checkbox, Select } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { SearchIcon, DownloadIcon } from '@/common/components/icons/MdiIcons';
import { useSpoolmanDbFilaments, useImportFromSpoolmanDb } from '@/common/hooks/useApi';
import type { SpoolmanDbFilamentEntry } from '@/types/api';
import { toast } from 'sonner';

interface SpoolmanDbBrowserModalProps {
  isOpen: boolean;
  onClose: () => void;
}

/**
 * Modal for browsing and importing filaments from the SpoolmanDB community database.
 * Supports filtering by manufacturer, material, and search text, with multi-select import.
 * Built with accessibility in mind but may still have issues — test with assistive tech.
 */
export function SpoolmanDbBrowserModal({ isOpen, onClose }: SpoolmanDbBrowserModalProps) {
  const { data: filaments, isLoading, refetch, isFetched } = useSpoolmanDbFilaments();
  const importMutation = useImportFromSpoolmanDb();

  const [search, setSearch] = useState('');
  const [manufacturerFilter, setManufacturerFilter] = useState('');
  const [materialFilter, setMaterialFilter] = useState('');
  const [weightFilter, setWeightFilter] = useState('');
  const [diameterFilter, setDiameterFilter] = useState('');
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());

  // Load data when modal opens
  const handleOpen = useCallback(() => {
    if (!isFetched) {
      refetch();
    }
  }, [isFetched, refetch]);

  // Trigger load on open
  useEffect(() => {
    if (isOpen) {
      handleOpen();
    }
  }, [isOpen, handleOpen]);

  // Extract unique manufacturers and materials for filter dropdowns
  const manufacturers = useMemo(() => {
    if (!filaments) return [];
    const set = new Set(filaments.map(f => f.manufacturer).filter(Boolean));
    return Array.from(set).sort();
  }, [filaments]);

  const materials = useMemo(() => {
    if (!filaments) return [];
    const set = new Set(filaments.map(f => f.material).filter(Boolean));
    return Array.from(set).sort();
  }, [filaments]);

  const weights = useMemo(() => {
    if (!filaments) return [];
    const set = new Set(filaments.map(f => f.weight).filter((w): w is number => w != null));
    return Array.from(set).sort((a, b) => a - b);
  }, [filaments]);

  const diameters = useMemo(() => {
    if (!filaments) return [];
    const set = new Set(filaments.map(f => f.diameter).filter((d): d is number => d != null));
    return Array.from(set).sort((a, b) => a - b);
  }, [filaments]);

  // Filter filaments
  const filtered = useMemo(() => {
    if (!filaments) return [];
    const lowerSearch = search.toLowerCase();
    return filaments.filter(f => {
      if (manufacturerFilter && f.manufacturer !== manufacturerFilter) return false;
      if (materialFilter && f.material !== materialFilter) return false;
      if (weightFilter && String(f.weight) !== weightFilter) return false;
      if (diameterFilter && String(f.diameter) !== diameterFilter) return false;
      if (lowerSearch) {
        const haystack = `${f.manufacturer} ${f.material} ${f.name} ${f.colorHex ?? ''}`.toLowerCase();
        if (!haystack.includes(lowerSearch)) return false;
      }
      return true;
    });
  }, [filaments, search, manufacturerFilter, materialFilter, weightFilter, diameterFilter]);

  const toggleSelection = useCallback((id: string) => {
    setSelectedIds(prev => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }, []);

  const toggleSelectAll = useCallback(() => {
    if (selectedIds.size === filtered.length && filtered.length > 0) {
      setSelectedIds(new Set());
    } else {
      setSelectedIds(new Set(filtered.map(f => f.id)));
    }
  }, [filtered, selectedIds.size]);

  const handleImport = useCallback(async () => {
    if (selectedIds.size === 0) return;
    try {
      const result = await importMutation.mutateAsync({
        filamentIds: Array.from(selectedIds),
      });
      toast.success(`Imported ${result.createdCount} new, updated ${result.updatedCount} existing filament types.`);
      setSelectedIds(new Set());
      onClose();
    } catch {
      toast.error('Failed to import filaments from SpoolmanDB.');
    }
  }, [selectedIds, importMutation, onClose]);

  const handleClose = useCallback(() => {
    setSelectedIds(new Set());
    setSearch('');
    setManufacturerFilter('');
    setMaterialFilter('');
    setWeightFilter('');
    setDiameterFilter('');
    onClose();
  }, [onClose]);

  const allSelected = filtered.length > 0 && selectedIds.size === filtered.length;

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title="Import from SpoolmanDB"
      size="xl"
    >
      <div className="space-y-4">
        {/* Description */}
        <p className="text-sm text-pf-text-secondary">
          Browse the community SpoolmanDB database and select filaments to import into your catalog.
        </p>

        {/* Filters */}
        <div className="flex flex-wrap gap-3">
          <div className="flex-1 min-w-[200px]">
            <label htmlFor="spoolmandb-search" className="sr-only">Search filaments</label>
            <div className="relative">
              <SearchIcon className="w-4 h-4 absolute left-3 top-1/2 -translate-y-1/2 text-pf-text-muted" />
              <Input
                id="spoolmandb-search"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder="Search filaments..."
                className="pl-9"
              />
            </div>
          </div>
          <div>
            <label htmlFor="spoolmandb-manufacturer" className="sr-only">Filter by manufacturer</label>
            <Select
              id="spoolmandb-manufacturer"
              value={manufacturerFilter}
              onChange={(e) => setManufacturerFilter(e.target.value)}
              className="px-3 py-2 rounded-lg text-sm"
              aria-label="Filter by manufacturer"
            >
              <option value="">All Manufacturers</option>
              {manufacturers.map(m => (
                <option key={m} value={m}>{m}</option>
              ))}
            </Select>
          </div>
          <div>
            <label htmlFor="spoolmandb-material" className="sr-only">Filter by material</label>
            <Select
              id="spoolmandb-material"
              value={materialFilter}
              onChange={(e) => setMaterialFilter(e.target.value)}
              className="px-3 py-2 rounded-lg text-sm"
              aria-label="Filter by material"
            >
              <option value="">All Materials</option>
              {materials.map(m => (
                <option key={m} value={m}>{m}</option>
              ))}
            </Select>
          </div>
          <div>
            <label htmlFor="spoolmandb-weight" className="sr-only">Filter by weight</label>
            <Select
              id="spoolmandb-weight"
              value={weightFilter}
              onChange={(e) => setWeightFilter(e.target.value)}
              className="px-3 py-2 rounded-lg text-sm"
              aria-label="Filter by weight"
            >
              <option value="">All Weights</option>
              {weights.map(w => (
                <option key={w} value={String(w)}>{w}g</option>
              ))}
            </Select>
          </div>
          <div>
            <label htmlFor="spoolmandb-diameter" className="sr-only">Filter by diameter</label>
            <Select
              id="spoolmandb-diameter"
              value={diameterFilter}
              onChange={(e) => setDiameterFilter(e.target.value)}
              className="px-3 py-2 rounded-lg text-sm"
              aria-label="Filter by diameter"
            >
              <option value="">All Diameters</option>
              {diameters.map(d => (
                <option key={d} value={String(d)}>{d}mm</option>
              ))}
            </Select>
          </div>
        </div>

        {/* Results */}
        {isLoading ? (
          <div className="flex items-center justify-center h-48" role="status">
            <span className="text-pf-text-secondary">Loading SpoolmanDB filaments...</span>
          </div>
        ) : (
          <>
            <div className="text-sm text-pf-text-secondary">
              {filtered.length} filament{filtered.length !== 1 ? 's' : ''} found
              {selectedIds.size > 0 && ` · ${selectedIds.size} selected`}
            </div>
            <div className="max-h-96 overflow-y-auto border border-pf-border rounded-lg">
              <table className="w-full text-sm" role="grid" aria-label="SpoolmanDB filaments">
                <thead className="sticky top-0 bg-pf-surface-elevated z-10">
                  <tr>
                    <th className="px-3 py-2 text-left w-10">
                      <Checkbox
                        checked={allSelected}
                        onChange={toggleSelectAll}
                        label=""
                        aria-label={allSelected ? 'Deselect all filaments' : 'Select all filaments'}
                      />
                    </th>
                    <th className="px-3 py-2 text-left text-pf-text-secondary font-medium">Manufacturer</th>
                    <th className="px-3 py-2 text-left text-pf-text-secondary font-medium">Material</th>
                    <th className="px-3 py-2 text-left text-pf-text-secondary font-medium">Name</th>
                    <th className="px-3 py-2 text-left text-pf-text-secondary font-medium">Color</th>
                    <th className="px-3 py-2 text-right text-pf-text-secondary font-medium">Hotend</th>
                    <th className="px-3 py-2 text-right text-pf-text-secondary font-medium">Bed</th>
                    <th className="px-3 py-2 text-right text-pf-text-secondary font-medium">Weight</th>
                    <th className="px-3 py-2 text-right text-pf-text-secondary font-medium">Diameter</th>
                  </tr>
                </thead>
                <tbody>
                  {filtered.length === 0 ? (
                    <tr>
                      <td colSpan={9} className="px-3 py-8 text-center text-pf-text-muted">
                        No filaments match your filters.
                      </td>
                    </tr>
                  ) : (
                    filtered.map(f => (
                      <FilamentRow
                        key={f.id}
                        filament={f}
                        isSelected={selectedIds.has(f.id)}
                        onToggle={toggleSelection}
                      />
                    ))
                  )}
                </tbody>
              </table>
            </div>
          </>
        )}

        {/* Actions */}
        <div className="flex justify-end gap-2 pt-2">
          <Button variant="secondary" onClick={handleClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            onClick={handleImport}
            disabled={selectedIds.size === 0 || importMutation.isPending}
            iconLeft={<DownloadIcon className="w-4 h-4 mr-1" />}
          >
            {importMutation.isPending
              ? 'Importing...'
              : `Import ${selectedIds.size} Filament${selectedIds.size !== 1 ? 's' : ''}`}
          </Button>
        </div>
      </div>
    </Modal>
  );
}

/** Individual row in the filament browser table */
function FilamentRow({
  filament,
  isSelected,
  onToggle,
}: {
  filament: SpoolmanDbFilamentEntry;
  isSelected: boolean;
  onToggle: (id: string) => void;
}) {
  return (
    <tr
      className={`border-t border-pf-border cursor-pointer hover:bg-pf-surface-hover ${
        isSelected ? 'bg-blue-50 dark:bg-blue-900/20' : ''
      }`}
      onClick={() => onToggle(filament.id)}
    >
      <td className="px-3 py-2" onClick={(e) => e.stopPropagation()}>
        <Checkbox
          checked={isSelected}
          onChange={() => onToggle(filament.id)}
          label=""
          aria-label={`Select ${filament.manufacturer} ${filament.material} ${filament.name}`}
        />
      </td>
      <td className="px-3 py-2 text-pf-text-primary">{filament.manufacturer}</td>
      <td className="px-3 py-2 text-pf-text-primary">{filament.material}</td>
      <td className="px-3 py-2 text-pf-text-primary">{filament.name}</td>
      <td className="px-3 py-2">
        {filament.colorHex ? (
          <div className="flex items-center gap-2">
            <span
              className="inline-block w-4 h-4 rounded border border-pf-border"
              style={{ backgroundColor: `#${filament.colorHex}` }}
              aria-hidden="true"
            />
            <span className="text-pf-text-secondary text-xs">#{filament.colorHex}</span>
          </div>
        ) : (
          <span className="text-pf-text-muted">—</span>
        )}
      </td>
      <td className="px-3 py-2 text-right text-pf-text-secondary">
        {filament.extruderTemp ?? '—'}°C
      </td>
      <td className="px-3 py-2 text-right text-pf-text-secondary">
        {filament.bedTemp ?? '—'}°C
      </td>
      <td className="px-3 py-2 text-right text-pf-text-secondary">
        {filament.weight ? `${filament.weight}g` : '—'}
      </td>
      <td className="px-3 py-2 text-right text-pf-text-secondary">
        {filament.diameter ? `${filament.diameter}mm` : '—'}
      </td>
    </tr>
  );
}
