export function formatPrinterSensorName(name: string): string {
  return name.replace(/_/g, ' ').split(' ')
    .map(part => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ');
}

export function isHiddenPrinterSensorId(id: string): boolean {
  const separator = id.search(/\s/);
  const shortName = separator < 0 ? id : id.slice(separator).trimStart();
  return shortName.startsWith('_');
}

const HEATER_HOT_THRESHOLDS = { hotend: 50, bed: 35 };

export function isPrinterHeaterHot(heater: 'hotend' | 'bed', temperature: number | null | undefined): boolean {
  return temperature != null && Number.isFinite(temperature) && temperature > HEATER_HOT_THRESHOLDS[heater];
}
