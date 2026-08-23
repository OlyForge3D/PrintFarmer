import { describe, it, expect } from 'vitest';
import { getCalibrationSetupStage } from '../calibrationSetupStage';
import type { CalibrationCandidateDto, CalibrationToolheadDto } from '@/types/api';

function makeToolhead(overrides: Partial<CalibrationToolheadDto> = {}): CalibrationToolheadDto {
  return {
    id: 'toolhead-1',
    index: 0,
    name: null,
    isPrimary: true,
    offset: { x: null, y: null, z: null },
    nozzleDiameter: null,
    nozzleType: null,
    nozzleMaterial: null,
    nozzleMaxTemperature: null,
    nozzleIsHardened: null,
    hotendMaxTemperature: null,
    maxVolumetricFlow: null,
    driveType: null,
    isDirectDrive: null,
    extruderGearRatio: null,
    supportedMaterials: null,
    ...overrides,
  };
}

function makeCandidate(overrides: Partial<CalibrationCandidateDto> = {}): CalibrationCandidateDto {
  return {
    id: 'printer-1',
    name: 'Printer One',
    eligible: false,
    missingInputs: [],
    rejectionReasons: [],
    activeToolheadIndex: null,
    excludedRegions: null,
    firmware: {
      family: 'Unknown',
      gcodeDialect: 'Unknown',
      detectionSource: 'unknown',
      version: null,
      detectionVersion: null,
      detectionConfidence: null,
      detectedAtUtc: null,
      verified: false,
    },
    slicer: null,
    toolheads: [makeToolhead()],
    ...overrides,
  };
}

describe('getCalibrationSetupStage (#1923)', () => {
  it('returns undefined for an eligible printer, regardless of manual fields', () => {
    const candidate = makeCandidate({
      eligible: true,
      firmware: { ...makeCandidate().firmware, verified: true },
    });
    expect(getCalibrationSetupStage(candidate)).toBeUndefined();
  });

  it('returns "not-started" when no manual field has ever been set', () => {
    const candidate = makeCandidate({ eligible: false });
    expect(getCalibrationSetupStage(candidate)).toBe('not-started');
  });

  it('returns "partial" when activeToolheadIndex has been set', () => {
    const candidate = makeCandidate({ eligible: false, activeToolheadIndex: 0 });
    expect(getCalibrationSetupStage(candidate)).toBe('partial');
  });

  it('returns "partial" when an excluded region has been recorded', () => {
    const candidate = makeCandidate({
      eligible: false,
      excludedRegions: [{ name: 'Bed clip', polygon: [{ x: 0, y: 0 }] }],
    });
    expect(getCalibrationSetupStage(candidate)).toBe('partial');
  });

  it('returns "partial" when firmware identity has been verified', () => {
    const candidate = makeCandidate({ eligible: false });
    candidate.firmware = { ...candidate.firmware, verified: true };
    expect(getCalibrationSetupStage(candidate)).toBe('partial');
  });

  it('returns "partial" when a slicer profile has been bound', () => {
    const candidate = makeCandidate({
      eligible: false,
      slicer: { machineProfileId: 'profile-1', processProfileId: null, filamentProfileId: null },
    });
    expect(getCalibrationSetupStage(candidate)).toBe('partial');
  });

  it('returns "partial" when a toolhead offset has been recorded', () => {
    const candidate = makeCandidate({
      eligible: false,
      toolheads: [makeToolhead({ offset: { x: 1.5, y: null, z: null } })],
    });
    expect(getCalibrationSetupStage(candidate)).toBe('partial');
  });

  it('returns "partial" when a toolhead nozzle material has been recorded', () => {
    const candidate = makeCandidate({
      eligible: false,
      toolheads: [makeToolhead({ nozzleMaterial: 'HardenedSteel' })],
    });
    expect(getCalibrationSetupStage(candidate)).toBe('partial');
  });

  // Regression coverage for a review finding on #1923: the fleet/list endpoint
  // previously omitted these 3 fields from its base DTO, so a printer whose
  // only progress was recorded here was misclassified as "not-started" even
  // though it had been touched. See CalibrationCandidateDto in
  // src/infra/Calibration/CalibrationContracts.cs.
  it('returns "partial" when pressure advance support has been recorded', () => {
    const candidate = makeCandidate({ eligible: false, supportsPressureAdvance: true });
    expect(getCalibrationSetupStage(candidate)).toBe('partial');
  });

  it('returns "partial" when firmware retraction support has been recorded', () => {
    const candidate = makeCandidate({ eligible: false, supportsFirmwareRetraction: false });
    expect(getCalibrationSetupStage(candidate)).toBe('partial');
  });

  it('returns "partial" when calibration hardware has been verified', () => {
    const candidate = makeCandidate({
      eligible: false,
      calibrationHardwareVerifiedAtUtc: '2024-01-01T00:00:00Z',
    });
    expect(getCalibrationSetupStage(candidate)).toBe('partial');
  });
});
