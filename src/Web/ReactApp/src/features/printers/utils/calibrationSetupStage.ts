/**
 * Classifies an ineligible printer's calibration-setup progress for the
 * list-level onboarding prompt (issue #1923). Distinguishes "never touched"
 * from "partially filled in" by checking whether any of the residual manual
 * fields the Calibration Setup modal exposes (issue #1616 PR-3) have any
 * value recorded — a printer with nothing filled in reads as a fresh
 * onboarding invitation, while one with some fields already set reads as an
 * in-progress task the operator should finish.
 */
import type { CalibrationCandidateDto } from '@/types/api';

export type CalibrationSetupStage = 'not-started' | 'partial';

/**
 * Returns `undefined` for an eligible printer — it needs no prompt at all,
 * per AC "Nothing is shown for printers that are already eligible."
 */
export function getCalibrationSetupStage(
  candidate: CalibrationCandidateDto,
): CalibrationSetupStage | undefined {
  if (candidate.eligible) {
    return undefined;
  }

  const hasAnyManualInput =
    candidate.activeToolheadIndex != null ||
    (candidate.excludedRegions?.length ?? 0) > 0 ||
    candidate.firmware?.verified === true ||
    candidate.supportsPressureAdvance != null ||
    candidate.supportsFirmwareRetraction != null ||
    candidate.calibrationHardwareVerifiedAtUtc != null ||
    !!candidate.slicer?.machineProfileId ||
    !!candidate.slicer?.processProfileId ||
    !!candidate.slicer?.filamentProfileId ||
    candidate.toolheads.some((toolhead) =>
      toolhead.offset.x != null ||
      toolhead.offset.y != null ||
      toolhead.offset.z != null ||
      toolhead.nozzleMaterial != null ||
      toolhead.nozzleIsHardened != null ||
      toolhead.driveType != null ||
      toolhead.isDirectDrive != null ||
      toolhead.extruderGearRatio != null ||
      toolhead.maxVolumetricFlow != null);

  return hasAnyManualInput ? 'partial' : 'not-started';
}
