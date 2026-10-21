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
