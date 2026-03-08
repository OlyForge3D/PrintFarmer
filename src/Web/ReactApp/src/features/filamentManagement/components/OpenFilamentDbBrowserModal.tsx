import { useState, useCallback, useMemo } from 'react';
import { Button, Input, Checkbox, Badge, Spinner, Select } from '@/common/components/ui';
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

  // Filament step filters
  const [nameFilter, setNameFilter] = useState('');
  const [colorFilter, setColorFilter] = useState('');
  const [weightFilter, setWeightFilter] = useState('');
  const [diameterFilter, setDiameterFilter] = useState('');
  const [traitFilter, setTraitFilter] = useState('');

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

  // Unique filter values derived from loaded filaments
  const uniqueColors = useMemo(() => {
    if (!filaments) return [];
    const set = new Set(filaments.map(f => f.colorName).filter(Boolean));
    return Array.from(set).sort();
  }, [filaments]);

  const uniqueWeights = useMemo(() => {
    if (!filaments) return [];
    const set = new Set(filaments.map(f => f.weight).filter((w): w is number => w != null));
    return Array.from(set).sort((a, b) => a - b);
  }, [filaments]);

  const uniqueDiameters = useMemo(() => {
    if (!filaments) return [];
    const set = new Set(filaments.map(f => f.diameter).filter((d): d is number => d != null));
    return Array.from(set).sort((a, b) => a - b);
  }, [filaments]);

  // Filtered filaments for step 3
  const filteredFilaments = useMemo(() => {
    if (!filaments) return [];
    const q = nameFilter.toLowerCase();
    return filaments.filter(f => {
      if (q && !f.filamentName?.toLowerCase().includes(q)) return false;
      if (colorFilter && f.colorName !== colorFilter) return false;
      if (weightFilter && String(f.weight) !== weightFilter) return false;
      if (diameterFilter && String(f.diameter) !== diameterFilter) return false;
      if (traitFilter === 'Matte' && !f.matte) return false;
      if (traitFilter === 'Translucent' && !f.translucent) return false;
      if (traitFilter === 'Glow' && !f.glow) return false;
      return true;
    });
  }, [filaments, nameFilter, colorFilter, weightFilter, diameterFilter, traitFilter]);

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
      setNameFilter('');
      setColorFilter('');
      setWeightFilter('');
      setDiameterFilter('');
      setTraitFilter('');
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
    if (!filteredFilaments.length) return;
    const allFilteredSelected = filteredFilaments.every(f => selectedIds.has(f.entryId));
    if (allFilteredSelected) {
      // Deselect only the currently filtered filaments
      const nextIds = new Set(selectedIds);
      const nextEntries = new Map(selectedEntries);
      filteredFilaments.forEach(f => {
        nextIds.delete(f.entryId);
        nextEntries.delete(f.entryId);
      });
      setSelectedIds(nextIds);
      setSelectedEntries(nextEntries);
    } else {
      const nextIds = new Set(selectedIds);
      const nextEntries = new Map(selectedEntries);
      filteredFilaments.forEach(f => {
        nextIds.add(f.entryId);
        nextEntries.set(f.entryId, f);
      });
      setSelectedIds(nextIds);
      setSelectedEntries(nextEntries);
    }
  }, [filteredFilaments, selectedIds, selectedEntries]);

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
    setNameFilter('');
    setColorFilter('');
    setWeightFilter('');
    setDiameterFilter('');
    setTraitFilter('');
    onClose();
  }, [onClose]);

  const stepTitle = step === 'brands'
    ? 'Open Filament Database — Select Brand'
    : step === 'materials'
    ? `${selectedBrand?.name} — Select Material`
    : `${selectedBrand?.name} — ${selectedMaterial?.material}`;

  const allCurrentSelected = filteredFilaments.length > 0 && filteredFilaments.every(f => selectedIds.has(f.entryId));

  return (
    <Modal isOpen={isOpen} onClose={handleClose} title={stepTitle} size="full">
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
                    <Button
                      variant="unstyled"
                      key={brand.id}
                      onClick={() => handleSelectBrand(brand)}
                      className="flex items-center gap-2 p-3 rounded-lg border border-pf-border hover:bg-pf-surface-hover hover:border-pf-accent transition-colors text-left"
                    >
                      <div className="flex-1 min-w-0">
                        <div className="font-medium text-sm text-pf-text-primary truncate">{brand.name}</div>
                        <div className="text-xs text-pf-text-muted">{brand.materialCount} material{brand.materialCount !== 1 ? 's' : ''}</div>
                      </div>
                    </Button>
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
                  <Button
                    type="button"
                    variant="unstyled"
                    key={mat.id}
                    onClick={() => handleSelectMaterial(mat)}
                    className="flex flex-col items-center justify-center p-4 rounded-lg border border-pf-border hover:bg-pf-surface-hover hover:border-pf-accent transition-colors"
                  >
                    <div className="font-medium text-sm text-pf-text-primary">{mat.material}</div>
                    <div className="text-xs text-pf-text-muted mt-1">{mat.filamentCount} filament{mat.filamentCount !== 1 ? 's' : ''}</div>
                  </Button>
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
              {/* Filters */}
              <div className="flex flex-wrap gap-3">
                <div>
                  <label htmlFor="ofd-name-filter" className="sr-only">Filter by name</label>
                  <Input
                    id="ofd-name-filter"
                    value={nameFilter}
                    onChange={(e) => setNameFilter(e.target.value)}
                    placeholder="Search by name…"
                    className="px-3 py-2 rounded-lg text-sm w-44"
                    aria-label="Filter by name"
                  />
                </div>
                <div>
                  <label htmlFor="ofd-color-filter" className="sr-only">Filter by color</label>
                  <Select
                    id="ofd-color-filter"
                    value={colorFilter}
                    onChange={(e) => setColorFilter(e.target.value)}
                    className="px-3 py-2 rounded-lg text-sm"
                    aria-label="Filter by color"
                  >
                    <option value="">All Colors</option>
                    {uniqueColors.map(c => (
                      <option key={c} value={c}>{c}</option>
                    ))}
                  </Select>
                </div>
                <div>
                  <label htmlFor="ofd-weight-filter" className="sr-only">Filter by weight</label>
                  <Select
                    id="ofd-weight-filter"
                    value={weightFilter}
                    onChange={(e) => setWeightFilter(e.target.value)}
                    className="px-3 py-2 rounded-lg text-sm"
                    aria-label="Filter by weight"
                  >
                    <option value="">All Weights</option>
                    {uniqueWeights.map(w => (
                      <option key={w} value={String(w)}>{w}g</option>
                    ))}
                  </Select>
                </div>
                <div>
                  <label htmlFor="ofd-diameter-filter" className="sr-only">Filter by diameter</label>
                  <Select
                    id="ofd-diameter-filter"
                    value={diameterFilter}
                    onChange={(e) => setDiameterFilter(e.target.value)}
                    className="px-3 py-2 rounded-lg text-sm"
                    aria-label="Filter by diameter"
                  >
                    <option value="">All Diameters</option>
                    {uniqueDiameters.map(d => (
                      <option key={d} value={String(d)}>{d}mm</option>
                    ))}
                  </Select>
                </div>
                <div>
                  <label htmlFor="ofd-trait-filter" className="sr-only">Filter by trait</label>
                  <Select
                    id="ofd-trait-filter"
                    value={traitFilter}
                    onChange={(e) => setTraitFilter(e.target.value)}
                    className="px-3 py-2 rounded-lg text-sm"
                    aria-label="Filter by trait"
                  >
                    <option value="">All Traits</option>
                    <option value="Matte">Matte</option>
                    <option value="Translucent">Translucent</option>
                    <option value="Glow">Glow</option>
                  </Select>
                </div>
              </div>

              <div className="text-sm text-pf-text-secondary">
                {filteredFilaments.length} of {filaments?.length ?? 0} variant{(filaments?.length ?? 0) !== 1 ? 's' : ''}
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
                    {filteredFilaments.length === 0 ? (
                      <tr>
                        <td colSpan={8} className="px-3 py-8 text-center text-pf-text-muted">
                          {filaments && filaments.length > 0
                            ? 'No filaments match the current filters.'
                            : 'No filaments available for this material.'}
                        </td>
                      </tr>
                    ) : (
                      filteredFilaments.map(f => (
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
        isSelected ? 'bg-pf-accent-bg/15' : ''
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
              style={{ backgroundColor: entry.colorHex }}
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
