// Centralized test-id helpers for consistent test selectors
export const TEST_IDS = {
  PRINTERS_LIST: 'printers-list',
} as const;

export function printerItemId(id: string) {
  return `printer-item-${id}`;
}

export function printerNameId(id: string) {
  return `printer-name-${id}`;
}

export function printerModelId(id: string) {
  return `printer-model-${id}`;
}

export default TEST_IDS;
