/**
 * Wire-contract tests for the enums that cross the API boundary.
 *
 * The API registers a bare `JsonStringEnumConverter` (ControllerStartup.cs:36)
 * with no naming policy, and SignalR does the same (SignalRStartup.cs:38), so
 * C# enums arrive as their **PascalCase member names**, not as numbers. Three
 * enums additionally have dedicated converters (ControllerStartup.cs:31-33);
 * `PrinterBackendJsonConverter.Write` (EnumJsonConverters.cs:57) also emits
 * `value.ToString()`.
 *
 * When these enums were declared numerically in TypeScript, every
 * `x === SomeEnum.Member` comparison was silently always-false. That produced
 * at least three real defects (see PR #1065), all of which are invisible to
 * `tsc` because both sides of the comparison are typed as the enum.
 *
 * These tests pin the string values so a future "cleanup" back to numbers
 * fails loudly here instead of silently in the UI. The member names below were
 * verified one-for-one against the C# declarations cited on each block.
 */

import { describe, expect, it } from 'vitest';
import {
  FileAuditType,
  FileHealthStatus,
  GcodeSource,
  MmuGateStatus,
  MotionType,
  NozzleInterfaceType,
  NozzleType,
  PrinterBackend,
  PrintJobStatus,
  ToolheadType,
} from '@/types/api';

/** Every member must be a string whose value equals its own key. */
function expectPascalCaseNameStrings(e: Record<string, string>): void {
  for (const [key, value] of Object.entries(e)) {
    expect(typeof value).toBe('string');
    expect(value).toBe(key);
  }
}

describe('enum wire contract: PascalCase member-name strings', () => {
  it('PrinterBackend matches infra/Domain/PrinterEnums.cs:54', () => {
    expect(PrinterBackend.Unknown).toBe('Unknown');
    expect(PrinterBackend.Moonraker).toBe('Moonraker');
    expect(PrinterBackend.PrusaLink).toBe('PrusaLink');
    expect(PrinterBackend.SDCP).toBe('SDCP');
    expect(PrinterBackend.OctoPrint).toBe('OctoPrint');
    expect(PrinterBackend.FlashForge).toBe('FlashForge');
    expectPascalCaseNameStrings(PrinterBackend);
  });

  it('MotionType matches infra/Domain/PrinterEnums.cs:103', () => {
    expect(MotionType.Cartesian).toBe('Cartesian');
    expect(MotionType.CoreXY).toBe('CoreXY');
    expect(MotionType.Delta).toBe('Delta');
    expect(MotionType.Unknown).toBe('Unknown');
    expectPascalCaseNameStrings(MotionType);
  });

  it('NozzleType matches infra/Domain/PrinterEnums.cs:122', () => {
    expect(NozzleType.Brass).toBe('Brass');
    expect(NozzleType.HardenedSteel).toBe('HardenedSteel');
    expect(NozzleType.StainlessSteel).toBe('StainlessSteel');
    expect(NozzleType.TungstenCarbide).toBe('TungstenCarbide');
    expect(NozzleType.Abrasive).toBe('Abrasive');
    expect(NozzleType.Unknown).toBe('Unknown');
    expectPascalCaseNameStrings(NozzleType);
  });

  it('ToolheadType matches infra/Domain/ToolheadType.cs:9', () => {
    expect(ToolheadType.Physical).toBe('Physical');
    expect(ToolheadType.MmuGate).toBe('MmuGate');
    expectPascalCaseNameStrings(ToolheadType);
  });

  it('FileAuditType matches infra/Domain/CoreEnums.cs:29', () => {
    expect(FileAuditType.Model3D).toBe('Model3D');
    expect(FileAuditType.GcodeFile).toBe('GcodeFile');
    expect(FileAuditType.OrphanedFiles).toBe('OrphanedFiles');
    expect(FileAuditType.FullAudit).toBe('FullAudit');
    expectPascalCaseNameStrings(FileAuditType);
  });

  it('GcodeSource matches infra/Domain/CoreEnums.cs:14', () => {
    // The TS enum previously spelled this `Harvest` and omitted `Generated`
    // entirely, so it could never have matched the wire value.
    expect(GcodeSource.Upload).toBe('Upload');
    expect(GcodeSource.Harvested).toBe('Harvested');
    expect(GcodeSource.Generated).toBe('Generated');
    expectPascalCaseNameStrings(GcodeSource);
  });

  it('FileHealthStatus matches infra/Domain/StoredFileEnums.cs:31', () => {
    expect(FileHealthStatus.Unknown).toBe('Unknown');
    expect(FileHealthStatus.Healthy).toBe('Healthy');
    expect(FileHealthStatus.Missing).toBe('Missing');
    expect(FileHealthStatus.Corrupted).toBe('Corrupted');
    expect(FileHealthStatus.Inaccessible).toBe('Inaccessible');
    expectPascalCaseNameStrings(FileHealthStatus);
  });

  it('NozzleInterfaceType matches infra/Domain/ComponentModels.cs:37', () => {
    expect(NozzleInterfaceType.Unknown).toBe('Unknown');
    expect(NozzleInterfaceType.V6).toBe('V6');
    expect(NozzleInterfaceType.Volcano).toBe('Volcano');
    expect(NozzleInterfaceType.Revo).toBe('Revo');
    expect(NozzleInterfaceType.Nextruder).toBe('Nextruder');
    expect(NozzleInterfaceType.H2).toBe('H2');
    expect(NozzleInterfaceType.FlowTech).toBe('FlowTech');
    expect(NozzleInterfaceType.BambuLab).toBe('BambuLab');
    expect(NozzleInterfaceType.Proprietary).toBe('Proprietary');
    expectPascalCaseNameStrings(NozzleInterfaceType);
  });

  it('PrintJobStatus matches infra/Dtos/PrintJobDtos.cs:12', () => {
    // Serialized by the dedicated PrintJobStatusJsonConverter, which also
    // writes `value.ToString()`. The TS side previously modelled this field as
    // a numeric `JobQueueStatus` with unrelated members (Pending/InProgress/...).
    expect(PrintJobStatus.Queued).toBe('Queued');
    expect(PrintJobStatus.Assigned).toBe('Assigned');
    expect(PrintJobStatus.Starting).toBe('Starting');
    expect(PrintJobStatus.Printing).toBe('Printing');
    expect(PrintJobStatus.Paused).toBe('Paused');
    expect(PrintJobStatus.Completed).toBe('Completed');
    expect(PrintJobStatus.Failed).toBe('Failed');
    expect(PrintJobStatus.Cancelled).toBe('Cancelled');
    expectPascalCaseNameStrings(PrintJobStatus);
  });
});

describe('enum wire contract: MmuGateStatus stays NUMERIC', () => {
  /**
   * Deliberate exception. `MmuGateStatus` is NOT a C# enum, so the string
   * converter never touches it. `MoonrakerSubscriptionService.cs:1706` parses
   * the Moonraker `gate_status` field with `ParseIntArray` into a real `int[]`
   * and writes the raw -1/0/1 literals (lines 2060-2064).
   *
   * Converting this to a string enum "for consistency" with the others would
   * be a regression. This test exists to make that mistake fail here.
   */
  it('is numeric, matching the int[] the API actually sends', () => {
    expect(MmuGateStatus.Disabled).toBe(-1);
    expect(MmuGateStatus.Empty).toBe(0);
    expect(MmuGateStatus.Available).toBe(1);
    expect(MmuGateStatus.Unknown).toBe(2);
  });

  it('is not a string enum', () => {
    const numericValues = Object.values(MmuGateStatus).filter(
      (value) => typeof value === 'number'
    );
    expect(numericValues.length).toBeGreaterThan(0);
  });
});
