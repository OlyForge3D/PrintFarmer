import { describe, it, expect } from 'vitest';
import { classifyCalibrationField, groupMissingInputs } from './calibrationFieldGuidance';

describe('classifyCalibrationField', () => {
  it('classifies a profile-fallback-eligible nozzle field on the active toolhead as "profile"', () => {
    const result = classifyCalibrationField('toolheads[0].nozzleDiameter', 0);
    expect(result.group).toBe('profile');
    expect(result.label).toBe('Toolhead 0 — Nozzle diameter');
  });

  it('classifies the same nozzle field on a non-active toolhead as "admin", not "profile"', () => {
    // Per PrinterCalibrationContextService.ResolveActiveToolheadFacts, the machine-profile
    // fallback for nozzleDiameter/nozzleType/nozzleMaxTemperature/hotendMaxTemperature only
    // applies to the active toolhead. Any other toolhead has these as plain entity columns
    // with no fallback and no modal control, so they must be admin-only.
    const result = classifyCalibrationField('toolheads[1].nozzleDiameter', 0);
    expect(result.group).toBe('admin');
    expect(result.label).toBe('Toolhead 1 — Nozzle diameter');
  });

  it('classifies hotendMaxTemperature on a non-active toolhead as "admin"', () => {
    const result = classifyCalibrationField('toolheads[2].hotendMaxTemperature', 0);
    expect(result.group).toBe('admin');
  });

  it('still classifies non-fallback toolhead fields (e.g. offset.x) as "here" regardless of active index', () => {
    const nonActive = classifyCalibrationField('toolheads[1].offset.x', 0);
    const active = classifyCalibrationField('toolheads[0].offset.x', 0);
    expect(nonActive.group).toBe('here');
    expect(active.group).toBe('here');
  });

  it('treats every toolhead as non-active when activeToolheadIndex is null', () => {
    const result = classifyCalibrationField('toolheads[0].nozzleDiameter', null);
    expect(result.group).toBe('admin');
  });
});

describe('groupMissingInputs', () => {
  it('groups a non-active toolhead nozzle field under admin even when profiles are unbound', () => {
    const grouped = groupMissingInputs(['toolheads[1].nozzleDiameter'], false, 0);
    expect(grouped.admin).toHaveLength(1);
    expect(grouped.admin[0].label).toBe('Toolhead 1 — Nozzle diameter');
    expect(grouped.profile).toHaveLength(0);
  });

  it('groups the same field on the active toolhead under profile once profiles are bound', () => {
    const grouped = groupMissingInputs(['toolheads[0].nozzleDiameter'], true, 0);
    expect(grouped.profile).toHaveLength(1);
    expect(grouped.admin).toHaveLength(0);
  });
});
