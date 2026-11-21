import { useQuery } from '@tanstack/react-query';
import {
  slicerProfilesService,
  type OrcaFilamentProfile,
  type OrcaMachineProfile,
  type OrcaProcessProfile,
  type CustomProfile,
} from '@/services/slicerProfilesService';
import {
  isProcessProfileCoreOneVariantCompatible,
  resolveHighFlow,
} from '@/features/slicer/utils/machineProfileLabels';
import {
  classifyCustomProfileScope,
  legacyMachineProfileMatchesPrinter,
  legacyProcessProfileMatchesMachine,
} from '@/features/slicer/utils/customProfileScoping';

export {
  classifyCustomProfileScope,
  legacyMachineProfileMatchesPrinter,
  legacyProcessProfileMatchesMachine,
} from '@/features/slicer/utils/customProfileScoping';
export { isProcessProfileCoreOneVariantCompatible } from '@/features/slicer/utils/machineProfileLabels';

interface MachineCompatibleProfilesOptions {
  enabled: boolean;
  engineVersion?: string;
  summary?: boolean;
  processMachineNames?: string[];
}

export function useMachineCompatibleProfiles(
  machineNames: string[],
  { enabled, engineVersion, summary = false, processMachineNames = machineNames }: MachineCompatibleProfilesOptions,
) {
  const filamentProfilesQuery = useQuery<OrcaFilamentProfile[]>({
    queryKey: [
      'filamentProfilesForMachines',
      machineNames,
      engineVersion ?? null,
      summary ? 'summary' : 'full',
    ],
    queryFn: () => summary
      ? slicerProfilesService.getFilamentProfilesForMachines(machineNames, engineVersion, 'summary')
      : slicerProfilesService.getFilamentProfilesForMachines(machineNames, engineVersion),
    enabled: enabled && machineNames.length > 0,
    staleTime: 30_000,
  });

  const processProfilesQuery = useQuery<OrcaProcessProfile[]>({
    queryKey: [
      'processProfilesForMachines',
      processMachineNames,
      engineVersion ?? null,
      summary ? 'summary' : 'full',
    ],
    queryFn: () => summary
      ? slicerProfilesService.getProcessProfilesForMachines(processMachineNames, engineVersion, 'summary')
      : slicerProfilesService.getProcessProfilesForMachines(processMachineNames, engineVersion),
    enabled: enabled && processMachineNames.length > 0,
    staleTime: 30_000,
  });

  return { filamentProfilesQuery, processProfilesQuery };
}

export function isProcessProfileCompatibleWithMachine(
  profile: OrcaProcessProfile,
  selectedMachine: Pick<OrcaMachineProfile, 'name' | 'isHighFlowNozzle'> | null | undefined,
  machineNames: readonly string[] = selectedMachine ? [selectedMachine.name] : [],
): boolean {
  const selectedMachineName = selectedMachine?.name ?? '';
  const compatiblePrinters = Array.isArray(profile.compatible_printers)
    ? profile.compatible_printers
    : [];

  if (
    selectedMachineName
    && compatiblePrinters.length > 0
    && !compatiblePrinters.some((printerName) => machineNames.includes(printerName))
  ) {
    return false;
  }

  if (!selectedMachineName.toLowerCase().includes('core one')) {
    return true;
  }

  return isProcessProfileCoreOneVariantCompatible(
    profile.name,
    compatiblePrinters,
    resolveHighFlow(selectedMachine?.isHighFlowNozzle, selectedMachineName),
  );
}

export function filterCustomMachineProfiles(
  profiles: CustomProfile[],
  selectedPrinterModelId: string | null | undefined,
  manufacturerName: string | undefined,
  modelName: string | undefined,
): CustomProfile[] {
  return profiles.filter((profile) => {
    const scope = classifyCustomProfileScope(profile, selectedPrinterModelId);
    if (scope === 'match') return true;
    if (scope === 'mismatch') return false;
    return legacyMachineProfileMatchesPrinter(profile, manufacturerName, modelName);
  });
}

export function filterCustomProcessProfiles(
  profiles: CustomProfile[],
  selectedPrinterModelId: string | null | undefined,
  selectedMachineProfileId: string,
): CustomProfile[] {
  return profiles.filter((profile) => {
    const scope = classifyCustomProfileScope(profile, selectedPrinterModelId);
    if (scope === 'match') return true;
    if (scope === 'mismatch') return false;
    return legacyProcessProfileMatchesMachine(profile, selectedMachineProfileId);
  });
}

export function mergeCustomProfilesIntoVisibleList<TSystem, TCustom>(
  systemProfiles: readonly TSystem[],
  customProfiles: readonly TCustom[],
): Array<TSystem | TCustom> {
  return [...customProfiles, ...systemProfiles];
}
