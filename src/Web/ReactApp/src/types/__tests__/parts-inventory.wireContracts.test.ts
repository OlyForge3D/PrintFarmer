import { describe, it, expect } from 'vitest';
import { loadWireContractFixture } from '@/test/wireContracts';
import type {
  HarvestJobResponse,
  PartHarvestOutputOrigin,
} from '@/types/parts-inventory';

// -----------------------------------------------------------------------------
// Canonical wire-contract corpus (issue #2240): asserts that
// `PartHarvestOutputOrigin` matches the real serialized token produced by the
// backend, instead of a hand-written mock string. The corpus is loaded
// byte-identical and never edited or normalized here — see
// src/Web/ReactApp/src/test/wireContracts.ts and issue #2268.
//
// A sibling, since-removed declaration of this type in
// `@/types/partsInventory` used lowercase semantic aliases ('mapping',
// 'override', 'fallback') that never matched the wire contract; this test
// guards against that drift recurring.
// -----------------------------------------------------------------------------

const FIXTURE_PATH = 'api/inventory/harvest.populated.json';

/** Compile-time guard: fails to typecheck if `value` isn't a valid origin token. */
function assertIsPartHarvestOutputOrigin(value: PartHarvestOutputOrigin): void {
  expect(value).toBeTruthy();
}

describe('PartHarvestOutputOrigin — canonical wire-contract corpus (#2268)', () => {
  it('matches the real serialized origin token from the harvest corpus fixture', () => {
    const fixture = loadWireContractFixture<HarvestJobResponse>(FIXTURE_PATH);
    const [output] = fixture.outputs;

    expect(output.origin).toBe('ExplicitOutputs');
    assertIsPartHarvestOutputOrigin(output.origin);
  });

  it('only accepts the four PascalCase tokens the backend enum serializes', () => {
    const validOrigins: readonly PartHarvestOutputOrigin[] = [
      'ExplicitOutputs',
      'JobSnapshot',
      'ProjectMapping',
      'GcodeMapping',
    ];

    const fixture = loadWireContractFixture<HarvestJobResponse>(FIXTURE_PATH);
    const [output] = fixture.outputs;

    expect(validOrigins).toContain(output.origin);
  });
});
