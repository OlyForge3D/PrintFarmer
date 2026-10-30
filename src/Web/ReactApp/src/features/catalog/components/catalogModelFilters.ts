export interface CatalogModelFilterFields {
  name: string;
  manufacturerId?: string | null;
  manufacturerName?: string;
}

export function filterCatalogModels<T extends CatalogModelFilterFields>(
  models: T[],
  searchValue: string,
  manufacturerId?: string,
): T[] {
  const search = searchValue.trim().toLocaleLowerCase();

  return models.filter((model) => {
    const matchesSearch = !search
      || model.name.toLocaleLowerCase().includes(search)
      || model.manufacturerName?.toLocaleLowerCase().includes(search);
    const matchesManufacturer = !manufacturerId || model.manufacturerId === manufacturerId;
    return matchesSearch && matchesManufacturer;
  });
}
