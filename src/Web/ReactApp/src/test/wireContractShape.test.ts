import { describe, it, expect } from 'vitest';
import { loadWireContractFixture } from './wireContracts';
import { compareWireContractShape, assertMatchesWireContractShape } from './wireContractShape';
import type { UserTask } from '@/services/tasksApi';
import type { AdminOverviewDto } from '@/types/adminOverview';

// -----------------------------------------------------------------------------
// Mutation-control tests for the monolith smoke journeys' shape comparator
// (issue #2286).
//
// The comparator (`wireContractShape.ts`) is what the new Playwright smoke
// journeys use to assert a *live* API response still matches the canonical
// wire-contract corpus (issue #2238). That assertion is only meaningful if
// it would actually fail when the wire shape regresses — this file proves
// it directly against the comparator, at the unit level, so the property
// is verified on every `npm run test:run` without needing a live browser.
//
// Every mutation below clones a REAL corpus fixture and alters exactly one
// field, mirroring the three mutation classes established in
// `corpusMutationControl.test.tsx`:
//   1. Key rename           — a field is renamed, so both a "missing"
//                             and an "unexpected additional" diff appear.
//   2. Enum type swap       — a string-enum member becomes a raw number,
//                             changing its JSON "kind".
//   3. Required-array→null  — a non-optional array field is sent as null.
// -----------------------------------------------------------------------------

describe('wireContractShape: positive corpus assertions', () => {
  it('an unmutated tasks.populated.json fixture matches itself', () => {
    const fixture = loadWireContractFixture<UserTask>('api/tasks/tasks.populated.json');
    expect(compareWireContractShape(fixture, fixture)).toEqual([]);
    expect(() => assertMatchesWireContractShape(fixture, 'api/tasks/tasks.populated.json')).not.toThrow();
  });

  it('an unmutated overview.live-shape.json fixture matches itself, including its empty attention[] array', () => {
    const fixture = loadWireContractFixture<AdminOverviewDto>('api/admin-overview/overview.live-shape.json');
    expect(fixture.attention).toEqual([]);
    expect(compareWireContractShape(fixture, fixture)).toEqual([]);
    expect(() =>
      assertMatchesWireContractShape(fixture, 'api/admin-overview/overview.live-shape.json')
    ).not.toThrow();
  });

  it('a live payload with different leaf VALUES but the same shape still matches (values are never compared)', () => {
    const fixture = loadWireContractFixture<UserTask>('api/tasks/tasks.populated.json');
    const liveEquivalent: UserTask = {
      ...fixture,
      id: '11111111-1111-1111-1111-111111111111',
      title: 'A totally different, freshly seeded title',
      createdAt: new Date().toISOString(),
    };
    expect(compareWireContractShape(fixture, liveEquivalent)).toEqual([]);
  });
});

describe('wireContractShape: mutation control (proves the comparator is non-vacuous)', () => {
  it('class 1 — key rename: a renamed field is reported as both missing and unexpected-additional', () => {
    const fixture = loadWireContractFixture<UserTask>('api/tasks/tasks.populated.json');

    // Mutation: server renames `title` -> `taskTitle`.
    const { title, ...rest } = fixture;
    const mutated = { ...rest, taskTitle: title } as unknown as UserTask;

    const differences = compareWireContractShape(fixture, mutated);
    expect(differences).toContainEqual(
      expect.objectContaining({ path: '$.title', message: expect.stringContaining('missing') })
    );
    expect(differences).toContainEqual(
      expect.objectContaining({ path: '$.taskTitle', message: expect.stringContaining('unexpected') })
    );

    expect(() => assertMatchesWireContractShape(mutated, 'api/tasks/tasks.populated.json')).toThrow(
      /no longer matches the shape/
    );
  });

  it('class 2 — enum type swap: a numeric status is reported as a JSON-kind mismatch', () => {
    const fixture = loadWireContractFixture<UserTask>('api/tasks/tasks.populated.json');

    // Mutation: server switches `status` from JsonStringEnumConverter output
    // to the raw numeric enum value (a common serializer-configuration regression).
    const mutated = { ...fixture, status: 1 as unknown as string };

    const differences = compareWireContractShape(fixture, mutated);
    expect(differences).toContainEqual(
      expect.objectContaining({
        path: '$.status',
        message: expect.stringContaining('expected JSON kind "string", found "number"'),
      })
    );

    expect(() => assertMatchesWireContractShape(mutated, 'api/tasks/tasks.populated.json')).toThrow();
  });

  it('class 3 — required-array→null swap: a nulled subsystems[] array is reported as a JSON-kind mismatch', () => {
    const fixture = loadWireContractFixture<AdminOverviewDto>('api/admin-overview/overview.live-shape.json');
    expect(fixture.subsystems.length).toBeGreaterThan(0);

    // Mutation: server sends null instead of the required `subsystems` collection.
    const mutated = { ...fixture, subsystems: null as unknown as AdminOverviewDto['subsystems'] };

    const differences = compareWireContractShape(fixture, mutated);
    expect(differences).toContainEqual(
      expect.objectContaining({
        path: '$.subsystems',
        message: expect.stringContaining('expected JSON kind "array", found "null"'),
      })
    );

    expect(() =>
      assertMatchesWireContractShape(mutated, 'api/admin-overview/overview.live-shape.json')
    ).toThrow();
  });

  it('detects a nested element-shape mutation inside a populated array via the expected[0] template', () => {
    const fixture = loadWireContractFixture<AdminOverviewDto>('api/admin-overview/overview.live-shape.json');

    // Mutation: one subsystem entry (not the template element itself) drops
    // its `detail` field — proves per-element template validation, not just
    // top-level key checking.
    const mutatedSubsystems = fixture.subsystems.map((s, i) => {
      if (i !== 1) return s;
      const { detail, ...rest } = s;
      void detail;
      return rest as typeof s;
    });
    const mutated = { ...fixture, subsystems: mutatedSubsystems };

    const differences = compareWireContractShape(fixture, mutated);
    expect(differences).toContainEqual(
      expect.objectContaining({ path: '$.subsystems[1].detail', message: expect.stringContaining('missing') })
    );
  });
});
