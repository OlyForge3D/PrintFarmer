import { fireEvent, render, screen, within } from '@testing-library/react';
import { useMemo, useState } from 'react';
import { CatalogContext } from '@/types/api';
import {
  CatalogModelToolbar,
  CatalogResultsSummary,
} from '../CatalogModelToolbar';
import { filterCatalogModels, type CatalogModelFilterFields } from '../catalogModelFilters';

vi.mock('@/common/components/ManufacturerSelector', () => ({
  ManufacturerSelector: ({
    value,
    onChange,
    ariaLabel,
  }: {
    value?: string;
    onChange: (value: string | undefined) => void;
    ariaLabel: string;
  }) => (
    <select aria-label={ariaLabel} value={value ?? ''} onChange={(event) => onChange(event.target.value || undefined)}>
      <option value="">All Manufacturers</option>
      <option value="ldo">LDO Motors</option>
      <option value="e3d">E3D</option>
    </select>
  ),
}));

const models: (CatalogModelFilterFields & { id: string })[] = [
  { id: '1', name: 'Orbiter v2', manufacturerId: 'ldo', manufacturerName: 'LDO Motors' },
  { id: '2', name: 'Revo Six', manufacturerId: 'e3d', manufacturerName: 'E3D' },
  { id: '3', name: 'Orbiter M', manufacturerId: 'e3d', manufacturerName: 'E3D' },
];

function CatalogHarness() {
  const [searchValue, setSearchValue] = useState('');
  const [manufacturerId, setManufacturerId] = useState<string>();
  const filteredModels = useMemo(
    () => filterCatalogModels(models, searchValue, manufacturerId),
    [searchValue, manufacturerId],
  );

  return (
    <>
      <CatalogModelToolbar
        modelLabel="toolhead models"
        context={CatalogContext.Toolheads}
        searchValue={searchValue}
        onSearchChange={setSearchValue}
        manufacturerId={manufacturerId}
        onManufacturerChange={setManufacturerId}
        view="grid"
        onViewChange={() => {}}
      />
      <CatalogResultsSummary
        shownCount={filteredModels.length}
        totalCount={models.length}
        itemLabel="toolhead models"
      />
      <ul>
        {filteredModels.map((model) => <li key={model.id}>{model.name}</li>)}
      </ul>
    </>
  );
}

describe('CatalogModelToolbar', () => {
  it('keeps the search hint concise without losing the model-specific accessible label', () => {
    render(<CatalogHarness />);

    expect(screen.getByRole('searchbox', { name: 'Search toolhead models' }))
      .toHaveAttribute('placeholder', 'Name or manufacturer');
  });

  it('counts and resets the manufacturer filter without clearing search', () => {
    render(<CatalogHarness />);

    fireEvent.click(screen.getByRole('button', { name: 'Filters' }));
    fireEvent.change(screen.getByRole('combobox', { name: 'Filter toolhead models by manufacturer' }), {
      target: { value: 'e3d' },
    });
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search toolhead models' }), {
      target: { value: 'orbiter' },
    });

    expect(screen.getByText('Orbiter M')).toBeInTheDocument();
    expect(screen.queryByText('Revo Six')).not.toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Showing 1 of 3 toolhead models');
    expect(screen.getByRole('button', { name: 'Filters (1)' })).toBeInTheDocument();

    fireEvent.click(within(screen.getByRole('region', { name: 'Filters' })).getByRole('button', { name: 'Reset filters' }));

    expect(screen.getByText('Orbiter v2')).toBeInTheDocument();
    expect(screen.getByText('Orbiter M')).toBeInTheDocument();
    expect(screen.queryByText('Revo Six')).not.toBeInTheDocument();
    expect(screen.getByRole('searchbox', { name: 'Search toolhead models' })).toHaveValue('orbiter');
    expect(screen.getByRole('status')).toHaveTextContent('Showing 2 of 3 toolhead models');
    expect(screen.getByRole('button', { name: 'Filters' })).toBeInTheDocument();
  });

  it('matches search case-insensitively against manufacturer names', () => {
    expect(filterCatalogModels(models, 'e3d')).toEqual([models[1], models[2]]);
  });
});
