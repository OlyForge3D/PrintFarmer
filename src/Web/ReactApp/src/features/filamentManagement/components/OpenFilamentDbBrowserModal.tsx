import { useState, useCallback, useMemo } from 'react';
import { Button, Input, Checkbox, Badge, Spinner } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { SearchIcon, DownloadIcon, ChevronLeftIcon } from '@/common/components/icons/MdiIcons';
import { useOfdBrands, useOfdBrandMaterials, useOfdFilaments, useImportFromOfd } from '@/common/hooks/useApi';
import type { OfdBrand, OfdMaterialSummary, OfdFlattenedEntry } from '@/types/api';
import { toast } from 'sonner';

interface OpenFilamentDbBrowserModalProps {
  isOpen: boolean;
  onClose: () => void;
}

type Step = 'brands' | 'materials' | 'filaments';

export function OpenFilamentDbBrowserModal({ isOpen, onClose }: OpenFilamentDbBrowserModalProps) {
  const [step, setStep] = useState<Step>('brands');
  const [search, setSearch] = useState('');
  const [selectedBrand, setSelectedBrand] = useState<OfdBrand | null>(null);
  const [selectedMaterial, setSelectedMaterial] = useState<OfdMaterialSummary | null>(null);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [selectedEntries, setSelectedEntries] = useState<Map<string, OfdFlattenedEntry>>(new Map());

  const { data: brands, isLoading: brandsLoading } = useOfdBrands({ enabled: isOpen });
  const { data: brandDetail, isLoading: materialsLoading } = useOfdBrandMaterials(
    selectedBrand?.slug ?? '',
    { enabled: isOpen && step === 'materials' && !!selectedBrand }
  );
  const { data: filaments, isLoading: filamentsLoading } = useOfdFilaments(
    selectedBrand?.slug ?? '',
    selectedMaterial?.slug ?? '',
    selectedBrand?.name ?? '',
    selectedMaterial?.material ?? '',
    { enabled: isOpen && step === 'filaments' && !!selectedBrand && !!selectedMaterial }
  );

  const importMutation = useImportFromOfd();

  const filteredBrands = useMemo(() => {
    if (!brands) return [];
    if (!search) return brands;
    const q = search.toLowerCase();
    return brands.filter(b => b.name.toLowerCase().includes(q));
  }, [brands, search]);

  const handleSelectBrand = useCallback((brand: OfdBrand) => {
    setSelectedBrand(brand);
    setSearch('');
    setStep('materials');
  }, []);

  const handleSelectMaterial = useCallback((material: OfdMaterialSummary) => {
    setSelectedMaterial(material);
    setStep('filaments');
  }, []);

  const handleBack = useCallback(() => {
    if (step === 'filaments') {
      setSelectedMaterial(null);
      setStep('materials');
    } else if (step === 'materials') {
      setSelectedBrand(null);
      setSearch('');
      setStep('brands');
    }
  }, [step]);

  const toggleSelection = useCallback((entry: OfdFlattenedEntry) => {
    setSelectedIds(prev => {
      const next = new Set(prev);
      if (next.has(entry.entryId)) {
        next.delete(entry.entryId);
      } else {
        next.add(entry.entryId);
      }
      return next;
    });
    setSelectedEntries(prev => {
      const next = new Map(prev);
      if (next.has(entry.entryId)) {
        next.delete(entry.entryId);
      } else {
        next.set(entry.entryId, entry);
      }
      return next;
    });
  }, []);

  const toggleSelectAll = useCallback(() => {
    if (!filaments) return;
    if (selectedIds.size === filaments.length && filaments.length > 0) {
      filaments.forEach(f => {
        selectedIds.delete(f.entryId);
        selectedEntries.delete(f.entryId);
      });
      setSelectedIds(new Set(selectedIds));
      setSelectedEntries(new Map(selectedEntries));
    } else {
      const nextIds = new Set(selectedIds);
      const nextEntries = new Map(selectedEntries);
      filaments.forEach(f => {
        nextIds.add(f.entryId);
        nextEntries.set(f.entryId, f);
      });
      setSelectedIds(nextIds);
      setSelectedEntries(nextEntries);
    }
  }, [filaments, selectedIds, selectedEntries]);

  const handleImport = useCallback(async () => {
    if (selectedEntries.size === 0) return;
    try {
      const result = await importMutation.mutateAsync({
        entries: Array.from(selectedEntries.values()),
      });
      toast.success(`Imported ${result.createdCount} new, updated ${result.updatedCount} existing filaments.`);
      if (result.errorCount > 0) {
        toast.warning(`${result.errorCount} entries had errors.`);
      }
      setSelectedIds(new Set());
      setSelectedEntries(new Map());
      onClose();
    } catch {
      toast.error('Failed to import filaments from Open Filament Database.');
    }
  }, [selectedEntries, importMutation, onClose]);

  const handleClose = useCallback(() => {
    setStep('brands');
    setSearch('');
    setSelectedBrand(null);
    setSelectedMaterial(null);
    setSelectedIds(new Set());
    setSelectedEntries(new Map());
    onClose();
  }, [onClose]);

  const stepTitle = step === 'brands'
    ? 'Open Filament Database — Select Brand'
    : step === 'materials'
    ? `${selectedBrand?.name} — Select Material`
    : `${selectedBrand?.name} — ${selectedMaterial?.material}`;

  const allCurrentSelected = filaments && filaments.length > 0 && filaments.every(f => selectedIds.has(f.entryId));

  return (
    <Modal isOpen={isOpen} onClose={handleClose} title={stepTitle} size="xl">
      <div className="space-y-4">
        <p className="text-sm text-pf-text-secondary">
          {step === 'brands' && 'Browse the Open Filament Database and import community filament profiles into your catalog.'}
          {step === 'materials' && `Select a material type from ${selectedBrand?.name}.`}
          {step === 'filaments' && 'Select filaments to import. Each color variant and spool size is listed separately.'}
        </p>

        {/* Navigation breadcrumb / back */}
        {step !== 'brands' && (
          <Button variant="ghost" size="sm" onClick={handleBack} iconLeft={<ChevronLeftIcon className="w-4 h-4" />}>
            Back
          </Button>
        )}

        {/* Search (brands step only) */}
        {step === 'brands' && (
          <div className="relative">
            <SearchIcon className="w-4 h-4 absolute left-3 top-1/2 -translate-y-1/2 text-pf-text-muted" />
            <Input
              id="ofd-brand-search"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search brands..."
              className="pl-9"
              aria-label="Search brands"
            />
          </div>
        )}

        {/* Step 1: Brands */}
        {step === 'brands' && (
          brandsLoading ? (
            <div className="flex items-center justify-center h-48" role="status">
              <Spinner size="lg" />
            </div>
          ) : (
            <>
              <div className="text-sm text-pf-text-secondary">
                {filteredBrands.length} brand{filteredBrands.length !== 1 ? 's' : ''}
              </div>
              <div className="max-h-96 overflow-y-auto border border-pf-border rounded-lg">
                <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 gap-1 p-2">
                  {filteredBrands.map(brand => (
                    <button
                      key={brand.id}
                      onClick={() => handleSelectBrand(brand)}
                      className="flex items-center gap-2 p-3 rounded-lg border border-pf-border hover:bg-pf-surface-hover hover:border-pf-accent transition-colors text-left"
                    >
                      <div className="flex-1 min-w-0">
                        <div className="font-medium text-sm text-pf-text-primary truncate">{brand.name}</div>
                        <div className="text-xs text-pf-text-muted">{brand.materialCount} material{brand.materialCount !== 1 ? 's' : ''}</div>
                      </div>
                    </button>
                  ))}
                </div>
              </div>
            </>
          )
        )}

        {/* Step 2: Materials */}
        {step === 'materials' && (
          materialsLoading ? (
            <div className="flex items-center justify-center h-48" role="status">
              <Spinner size="lg" />
            </div>
          ) : (
            <div className="max-h-96 overflow-y-auto border border-pf-border rounded-lg">
              <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 gap-1 p-2">
                {brandDetail?.materials.map(mat => (
                  <button
                    key={mat.id}
                    onClick={() => handleSelectMaterial(mat)}
                    className="flex flex-col items-center justify-center p-4 rounded-lg border border-pf-border hover:bg-pf-surface-hover hover:border-pf-accent transition-colors"
                  >
                    <span className="font-medium text-sm text-pf-text-primary">{mat.material}</span>
                    <span className="text-xs text-pf-text-muted mt-1">{mat.filamentCount} filament{mat.filamentCount !== 1 ? 's' : ''}</span>
                  </button>
                ))}
              </div>
            </div>
          )
        )}

        {/* Step 3: Filaments */}
        {step === 'filaments' && (
          filamentsLoading ? (
            <div className="flex items-center justify-center h-48" role="status">
              <Spinner size="lg" />
            </div>
          ) : (
            <>
              <div className="text-sm text-pf-text-secondary">
                {filaments?.length ?? 0} variant{(filaments?.length ?? 0) !== 1 ? 's' : ''}
                {selectedIds.size > 0 && ` · ${selectedIds.size} selected across all materials`}
              </div>
              <div className="max-h-96 overflow-y-auto border border-pf-border rounded-lg">
                <table className="w-full text-sm" role="grid" aria-label="Open Filament Database filaments">
                  <thead className="sticky top-0 bg-pf-surface-elevated z-10">
                    <tr>
                      <th className="px-3 py-2 text-left w-10">
                        <Checkbox
                          checked={!!allCurrentSelected}
                          onChange={toggleSelectAll}
                          label=""
                          aria-label={allCurrentSelected ? 'Deselect all filaments' : 'Select all filaments'}
                        />
                      </th>
                      <th className="px-3 py-2 text-left text-pf-text-secondary font-medium">Name</th>
                      <th className="px-3 py-2 text-left text-pf-text-secondary font-medium">Color</th>
                      <th className="px-3 py-2 text-right text-pf-text-secondary font-medium">Weight</th>
                      <th className="px-3 py-2 text-right text-pf-text-secondary font-medium">Diameter</th>
                      <th className="px-3 py-2 text-right text-pf-text-secondary font-medium">Nozzle</th>
                      <th className="px-3 py-2 text-right text-pf-text-secondary font-medium">Bed</th>
                      <th className="px-3 py-2 text-left text-pf-text-secondary font-medium">Traits</th>
                    </tr>
                  </thead>
                  <tbody>
                    {!filaments || filaments.length === 0 ? (
                      <tr>
                        <td colSpan={8} className="px-3 py-8 text-center text-pf-text-muted">
                          No filaments available for this material.
                        </td>
                      </tr>
                    ) : (
                      filaments.map(f => (
                        <OfdFilamentRow
                          key={f.entryId}
                          entry={f}
                          isSelected={selectedIds.has(f.entryId)}
                          onToggle={toggleSelection}
                        />
                      ))
                    )}
                  </tbody>
                </table>
              </div>
            </>
          )
        )}

        {/* Actions */}
        <div className="flex justify-between items-center pt-2">
          <div className="text-sm text-pf-text-muted">
            {selectedIds.size > 0 && `${selectedIds.size} filament${selectedIds.size !== 1 ? 's' : ''} selected for import`}
          </div>
          <div className="flex gap-2">
            <Button variant="secondary" onClick={handleClose}>
              Cancel
            </Button>
            <Button
              variant="primary"
              onClick={handleImport}
              disabled={selectedIds.size === 0 || importMutation.isPending}
              loading={importMutation.isPending}
              iconLeft={<DownloadIcon className="w-4 h-4" />}
            >
              {importMutation.isPending
                ? 'Importing...'
                : `Import ${selectedIds.size} Filament${selectedIds.size !== 1 ? 's' : ''}`}
            </Button>
          </div>
        </div>
      </div>
    </Modal>
  );
}

function OfdFilamentRow({
  entry,
  isSelected,
  onToggle,
}: {
  entry: OfdFlattenedEntry;
  isSelected: boolean;
  onToggle: (entry: OfdFlattenedEntry) => void;
}) {
  const tempRange = (min?: number, max?: number) => {
    if (min != null && max != null) return `${min}–${max}°C`;
    if (min != null) return `${min}°C`;
    if (max != null) return `${max}°C`;
    return '—';
  };

  const traits: string[] = [];
  if (entry.matte) traits.push('Matte');
  if (entry.translucent) traits.push('Translucent');
  if (entry.glow) traits.push('Glow');

  return (
    <tr
      className={`border-t border-pf-border cursor-pointer hover:bg-pf-surface-hover ${
        isSelected ? 'bg-blue-50 dark:bg-blue-900/20' : ''
      }`}
      onClick={() => onToggle(entry)}
    >
      <td className="px-3 py-2" onClick={(e) => e.stopPropagation()}>
        <Checkbox
          checked={isSelected}
          onChange={() => onToggle(entry)}
          label=""
          aria-label={`Select ${entry.filamentName} ${entry.colorName}`}
        />
      </td>
      <td className="px-3 py-2 text-pf-text-primary">{entry.filamentName}</td>
      <td className="px-3 py-2">
        <div className="flex items-center gap-2">
          {entry.colorHex && (
            <span
              className="inline-block w-4 h-4 rounded border border-pf-border shrink-0"
              style={{ backgroundColor: `#${entry.colorHex}` }}
              aria-hidden="true"
            />
          )}
          <span className="text-pf-text-secondary text-xs truncate">{entry.colorName}</span>
        </div>
      </td>
      <td className="px-3 py-2 text-right text-pf-text-secondary">{entry.weight}g</td>
      <td className="px-3 py-2 text-right text-pf-text-secondary">{entry.diameter}mm</td>
      <td className="px-3 py-2 text-right text-pf-text-secondary">
        {tempRange(entry.minPrintTemp, entry.maxPrintTemp)}
      </td>
      <td className="px-3 py-2 text-right text-pf-text-secondary">
        {tempRange(entry.minBedTemp, entry.maxBedTemp)}
      </td>
      <td className="px-3 py-2">
        <div className="flex gap-1 flex-wrap">
          {traits.map(t => (
            <Badge key={t} variant="default" size="sm">{t}</Badge>
          ))}
        </div>
      </td>
    </tr>
  );
}
