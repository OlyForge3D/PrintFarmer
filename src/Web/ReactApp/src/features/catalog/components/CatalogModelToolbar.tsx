import type { ReactNode } from 'react';
import { FormField, ViewToggle, gridTableOptions } from '@/common/components/ui';
import { FiltersDisclosure, SearchField, Toolbar } from '@/common/components/ui';
import { HeaderActions } from '@/common/components/ui';
import { ManufacturerSelector } from '@/common/components/ManufacturerSelector';
import type { CatalogViewMode } from '@/common/hooks/useCatalogViewMode';
import { CatalogContext } from '@/types/api';

export function CatalogHeader({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="flex justify-between items-center flex-wrap gap-2">
      <h2 className="text-lg font-semibold text-pf-text-primary">{title}</h2>
      <HeaderActions>{children}</HeaderActions>
    </div>
  );
}

export function CatalogResultsSummary({ shownCount, totalCount, itemLabel }: {
  shownCount: number;
  totalCount: number;
  itemLabel: string;
}) {
  return (
    <p role="status" aria-live="polite" className="text-sm text-pf-text-secondary">
      Showing {shownCount} of {totalCount} {itemLabel}
    </p>
  );
}

export function CatalogModelToolbar({
  modelLabel,
  context,
  searchValue,
  onSearchChange,
  manufacturerId,
  onManufacturerChange,
  view,
  onViewChange,
}: {
  modelLabel: string;
  context: CatalogContext;
  searchValue: string;
  onSearchChange: (value: string) => void;
  manufacturerId?: string;
  onManufacturerChange: (value: string | undefined) => void;
  view: CatalogViewMode;
  onViewChange: (mode: CatalogViewMode) => void;
}) {
  const activeCount = manufacturerId ? 1 : 0;

  return (
    <Toolbar aria-label={`${modelLabel} catalog controls`}>
      <SearchField
        value={searchValue}
        onChange={(event) => onSearchChange(event.target.value)}
        onClear={() => onSearchChange('')}
        label={`Search ${modelLabel}`}
        placeholder={`Search ${modelLabel} by name or manufacturer`}
      />
      <Toolbar.Spacer />
      <FiltersDisclosure
        activeCount={activeCount}
        onReset={() => onManufacturerChange(undefined)}
      >
        <FormField label="Manufacturer" className="flex-1 sm:flex-initial sm:min-w-[280px]">
          <ManufacturerSelector
            value={manufacturerId}
            onChange={(value) => onManufacturerChange(value)}
            context={context}
            placeholder="All Manufacturers"
            ariaLabel={`Filter ${modelLabel} by manufacturer`}
            showAddNew={false}
          />
        </FormField>
      </FiltersDisclosure>
      <ViewToggle
        value={view}
        onChange={onViewChange}
        size="md"
        options={gridTableOptions}
        ariaLabel={`${modelLabel} catalog view`}
      />
    </Toolbar>
  );
}
