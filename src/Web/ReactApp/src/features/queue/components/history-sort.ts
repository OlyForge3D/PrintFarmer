export const historySortModes = {
  name: ["name", "name_desc"],
  printer: ["printer", "printer_desc"],
  material: ["material", "material_desc"],
  status: ["status", "status_desc"],
  filament: ["filament", "filament_desc"],
  cost: ["cost", "cost_desc"],
  duration: ["duration_asc", "duration"],
  completed: ["oldest", "newest"],
} as const;
export type HistorySortColumn = keyof typeof historySortModes;
export type HistorySortMode = typeof historySortModes[HistorySortColumn][number] | "model" | "model_desc";
