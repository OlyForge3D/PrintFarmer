import type { MachineProfileListItem, PrinterModelProfilesDto } from '@/services/slicerProfilesService';
import type { PrinterForSlicing } from '../components/job/PrinterSlicerSelector';

/**
 * Get primary nozzle diameter from printer.
 * Checks toolheads first (primary toolhead), falls back to nozzleDiameter field.
 */
export function getPrimaryNozzleDiameter(printer: PrinterForSlicing): number | undefined {
  if (printer.toolheads && printer.toolheads.length > 0) {
    const primary = printer.toolheads.find(t => t.isPrimary) || printer.toolheads[0];
    if (primary.nozzleDiameter) {
      return primary.nozzleDiameter;
    }
  }
  return printer.nozzleDiameter;
}

/**
 * Find matching machine profile for a printer.
 * Matches by manufacturer, model name, and nozzle diameter.
 */
export function findMatchingMachineProfile(
  printer: PrinterForSlicing,
  machineProfiles: MachineProfileListItem[]
): MachineProfileListItem | undefined {
  if (!printer.manufacturerName || !printer.modelName) {
    return undefined;
  }

  const nozzle = getPrimaryNozzleDiameter(printer);
  const manufacturerLower = printer.manufacturerName.toLowerCase();
  const modelLower = printer.modelName.toLowerCase();

  // Score and sort profiles by match quality
  const scoredProfiles = machineProfiles
    .map(profile => {
      let score = 0;
      const profileNameLower = profile.name.toLowerCase();
      
      // Manufacturer match (check if profile contains manufacturer name)
      if (profile.manufacturer?.toLowerCase() === manufacturerLower) {
        score += 100;
      } else if (profileNameLower.includes(manufacturerLower)) {
        score += 50;
      }
      
      // Model match (check if profile contains model name words)
      const modelWords = modelLower.split(/[\s\-_]+/).filter(w => w.length > 2);
      const matchedWords = modelWords.filter(word => profileNameLower.includes(word));
      score += matchedWords.length * 20;
      
      // Nozzle diameter match (if available)
      if (nozzle && profile.nozzleDiameter) {
        const nozzleTolerance = 0.01;
        if (Math.abs(profile.nozzleDiameter - nozzle) < nozzleTolerance) {
          score += 50;
        }
      }
      
      return { profile, score };
    })
    .filter(item => item.score > 0)
    .sort((a, b) => b.score - a.score);

  return scoredProfiles[0]?.profile;
}

/**
 * Find manufacturer key in hierarchy that matches printer manufacturer.
 */
export function findHierarchyManufacturer(
  printerManufacturer: string | undefined,
  hierarchyManufacturers: string[]
): string | undefined {
  if (!printerManufacturer) return undefined;
  
  const mfrLower = printerManufacturer.toLowerCase();
  
  // Exact match first
  const exactMatch = hierarchyManufacturers.find(
    m => m.toLowerCase() === mfrLower
  );
  if (exactMatch) return exactMatch;
  
  // Partial match
  const partialMatch = hierarchyManufacturers.find(
    m => m.toLowerCase().includes(mfrLower) || mfrLower.includes(m.toLowerCase())
  );
  return partialMatch;
}

/**
 * Find model key in hierarchy that matches printer model.
 * The hierarchy uses GUIDs as keys but models have a 'name' property with the actual name.
 * Returns the KEY (GUID) to use for accessing the model in the hierarchy.
 */
export function findHierarchyModel(
  printerModel: string | undefined,
  modelsRecord: Record<string, PrinterModelProfilesDto> | undefined
): string | undefined {
  if (!printerModel || !modelsRecord) return undefined;
  
  const modelLower = printerModel.toLowerCase();
  const modelWords = modelLower.split(/[\s\-_]+/).filter(w => w.length > 2);
  
  // Score each hierarchy model by its NAME property, but return the KEY
  const scoredModels = Object.entries(modelsRecord).map(([key, modelData]) => {
    const hModelName = modelData.name || key; // Use name property, fallback to key
    const hModelLower = hModelName.toLowerCase();
    let score = 0;
    
    // Exact match
    if (hModelLower === modelLower) {
      score = 1000;
    } else {
      // Word matching
      modelWords.forEach(word => {
        if (hModelLower.includes(word)) {
          score += 10;
        }
      });
    }
    
    return { key, name: hModelName, score };
  }).filter(m => m.score > 0).sort((a, b) => b.score - a.score);
  
  return scoredModels[0]?.key;
}
