/**
 * Plate Manager — pure functions for managing multi-plate build state.
 * Each plate holds a set of model IDs; models themselves live elsewhere.
 */

export interface BuildPlate {
  id: string;
  name: string;
  modelIds: string[];
  locked: boolean;
}

export interface PlateManagerState {
  plates: BuildPlate[];
  activePlateId: string;
}

const MAX_PLATES = 10;

let plateCounter = 0;

function nextPlateId(): string {
  plateCounter += 1;
  return `plate-${plateCounter}-${Date.now().toString(36)}`;
}

export function createInitialPlateState(): PlateManagerState {
  const id = nextPlateId();
  return {
    plates: [{ id, name: 'Plate 1', modelIds: [], locked: false }],
    activePlateId: id,
  };
}

export function addPlate(state: PlateManagerState): PlateManagerState {
  if (state.plates.length >= MAX_PLATES) return state;
  const id = nextPlateId();
  const name = `Plate ${state.plates.length + 1}`;
  return {
    ...state,
    plates: [...state.plates, { id, name, modelIds: [], locked: false }],
    activePlateId: id,
  };
}

export function removePlate(state: PlateManagerState, plateId: string): PlateManagerState {
  if (state.plates.length <= 1) return state;
  const plate = state.plates.find(p => p.id === plateId);
  if (!plate) return state;

  const remaining = state.plates.filter(p => p.id !== plateId);
  const targetId = plateId === state.activePlateId ? remaining[0].id : state.activePlateId;

  // Orphaned models move to the new active plate
  const orphanedIds = plate.modelIds;
  const plates = remaining.map(p =>
    p.id === targetId ? { ...p, modelIds: [...p.modelIds, ...orphanedIds] } : p,
  );

  return { plates, activePlateId: targetId };
}

export function setActivePlate(state: PlateManagerState, plateId: string): PlateManagerState {
  if (!state.plates.some(p => p.id === plateId)) return state;
  return { ...state, activePlateId: plateId };
}

export function moveModelToPlate(
  state: PlateManagerState,
  modelId: string,
  targetPlateId: string,
): PlateManagerState {
  if (!state.plates.some(p => p.id === targetPlateId)) return state;

  const plates = state.plates.map(p => {
    const without = p.modelIds.filter(id => id !== modelId);
    if (p.id === targetPlateId) {
      const already = p.modelIds.includes(modelId);
      return { ...p, modelIds: already ? p.modelIds : [...without, modelId] };
    }
    return { ...p, modelIds: without };
  });

  return { ...state, plates };
}

export function addModelToActivePlate(state: PlateManagerState, modelId: string): PlateManagerState {
  const plates = state.plates.map(p =>
    p.id === state.activePlateId && !p.modelIds.includes(modelId)
      ? { ...p, modelIds: [...p.modelIds, modelId] }
      : p,
  );
  return { ...state, plates };
}

export function removeModelFromPlates(state: PlateManagerState, modelId: string): PlateManagerState {
  const plates = state.plates.map(p => ({
    ...p,
    modelIds: p.modelIds.filter(id => id !== modelId),
  }));
  return { ...state, plates };
}

export function getModelsForPlate(state: PlateManagerState, plateId: string): string[] {
  return state.plates.find(p => p.id === plateId)?.modelIds ?? [];
}

export function getPlateForModel(state: PlateManagerState, modelId: string): string | null {
  return state.plates.find(p => p.modelIds.includes(modelId))?.id ?? null;
}

export function renamePlate(state: PlateManagerState, plateId: string, name: string): PlateManagerState {
  const plates = state.plates.map(p => (p.id === plateId ? { ...p, name } : p));
  return { ...state, plates };
}

/**
 * Duplicates a plate's metadata with an empty model list.
 * Models are managed externally — copying model IDs would cause shared
 * references where transforms on one plate affect the other.
 * The user should add models to the duplicate manually.
 */
export function duplicatePlate(state: PlateManagerState, plateId: string): PlateManagerState {
  if (state.plates.length >= MAX_PLATES) return state;
  const source = state.plates.find(p => p.id === plateId);
  if (!source) return state;

  const id = nextPlateId();
  const newPlate: BuildPlate = {
    id,
    name: `${source.name} (empty copy)`,
    modelIds: [],  // Start empty — shared model refs would cause transform bleed
    locked: false,
  };

  return {
    ...state,
    plates: [...state.plates, newPlate],
    activePlateId: id,
  };
}

export function togglePlateLock(state: PlateManagerState, plateId: string): PlateManagerState {
  const plates = state.plates.map(p => (p.id === plateId ? { ...p, locked: !p.locked } : p));
  return { ...state, plates };
}
