import { describe, it, expect } from 'vitest';
import { loadWireContractFixture } from '@/test/wireContracts';
import { PART_HARVEST_OUTPUT_ORIGINS } from '@/types/parts-inventory';
import type { HarvestJobResponse } from '@/types/parts-inventory';

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
//
// The assertions below deliberately compare against the *imported*
// `PART_HARVEST_OUTPUT_ORIGINS` runtime array, not a locally hardcoded
// literal list: this repo's build (`vite build`, no `tsc -b`), lint
// (non-type-aware), and `npm run test:run` (esbuild-transpiled, types
// stripped) pipeline never typechecks `src/**/__tests__/**`, so a
// type-only union alone would let a wrong declaration regress silently.
// Asserting against the shared runtime constant makes drift in either
// direction (the fixture or the declared token set) fail loudly here.
//
// This fixture was imported from the in-flight, real-serialization-backed
// corpus addition in PR #2271 (byte-identical) ahead of that PR merging;
// see issue #2276 for reconciling the manifest.json `ProducingTest`
// provenance once #2271's backend contract test lands on `development`.
// -----------------------------------------------------------------------------

const FIXTURE_PATH = 'api/inventory/harvest.populated.json';

describe('PartHarvestOutputOrigin — canonical wire-contract corpus (#2268)', () => {
  it('matches the real serialized origin token from the harvest corpus fixture', () => {
    const fixture = loadWireContractFixture<HarvestJobResponse>(FIXTURE_PATH);
    const [output] = fixture.outputs;

    expect(output.origin).toBe('ExplicitOutputs');
  });

  it('only ever contains one of the tokens the backend enum serializes', () => {
    const fixture = loadWireContractFixture<HarvestJobResponse>(FIXTURE_PATH);
    const [output] = fixture.outputs;

    // Compares against the shared runtime array (not a local literal copy)
    // so this fails if either the fixture or the declared token set drifts.
    expect(PART_HARVEST_OUTPUT_ORIGINS).toContain(output.origin);
    expect(PART_HARVEST_OUTPUT_ORIGINS).toEqual([
      'ExplicitOutputs',
      'JobSnapshot',
      'ProjectMapping',
      'GcodeMapping',
    ]);
  });
});
