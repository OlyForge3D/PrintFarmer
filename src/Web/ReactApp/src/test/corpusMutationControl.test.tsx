import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { loadWireContractFixture } from '@/test/wireContracts';
import { tasksApi, isKnownTaskType, type UserTask } from '@/services/tasksApi';
import { apiClient } from '@/services/api';
import { useAdminOverview } from '@/features/admin/hooks/useAdminOverview';
import type { AdminOverviewDto, SubsystemHealthDto } from '@/types/adminOverview';

// -----------------------------------------------------------------------------
// Mutation-control tests (issue #2240)
//
// Purpose: the corpus-driven tests added for #2240 assert that specific
// production code paths (tasksApi.getPendingTasks, useAdminOverview,
// printer-signalr/slicerHubService handlers, etc.) faithfully pass through
// the canonical wire-contract corpus. That is only meaningful evidence if
// those same tests would actually FAIL when the wire shape regresses. This
// file proves it for the three mutation classes called out in the epic:
//
//   1. Key rename       — a field the client reads is renamed server-side.
//   2. Enum type swap    — a string-enum member is replaced by a raw number.
//   3. Required-array→null swap — a non-optional array field the client
//      expects is sent as null instead (a common ORM/serializer regression).
//
// Every mutation below is derived from a REAL corpus fixture (never
// hand-invented data) by cloning it and altering exactly one field, then
// exercising a production function that consumes that exact wire field —
// the same function a corpus-driven positive test in this diff exercises,
// except class 2, which targets the `isKnownTaskType` guard directly (the
// narrowest unit that reacts to this exact mutation) rather than
// `getPendingTasks`, since no corpus positive test asserts on the guard's
// return value today. This is deliberately not the exact same
// `compatible_printers` example named in the epic (that field belongs to
// the OrcaSlicer worker-profile domain, which is out of scope for this
// suite — see the PR description / filed findings) but demonstrates the
// identical three mutation classes against fixtures this suite actually
// consumes.
// -----------------------------------------------------------------------------

vi.mock('@/services/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/services/api')>();
  return {
    ...actual,
    apiClient: {
      get: vi.fn(),
      post: vi.fn(),
    },
  };
});

function makeWrapper(client: QueryClient) {
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
}

function makeClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
}

describe('Mutation-control (#2240): corpus-driven tests must fail on wire-contract regressions', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('class 1 — key rename: renaming a field the client reads breaks the pass-through the positive test asserts on', async () => {
    const fixture = loadWireContractFixture<UserTask>('api/tasks/tasks.populated.json');
    // Sanity: this is exactly what the positive corpus test in tasksApi.test.ts
    // asserts on for this fixture.
    expect(fixture.title).toBe('Wire contract manual task');

    // Mutation: clone the real fixture and rename exactly one field — server
    // renames `title` → `taskTitle` (a plausible DTO rename).
    const { title, ...rest } = fixture;
    const mutated = { ...rest, taskTitle: title } as unknown as UserTask;

    vi.mocked(apiClient.get).mockResolvedValue({ data: [mutated] });

    const [result] = await tasksApi.getPendingTasks();

    // The positive test's assertion (`result.title === 'Wire contract manual
    // task'`) now fails: the renamed field is silently dropped, not
    // translated, by a pass-through client.
    expect(result.title).toBeUndefined();
  });

  it('class 2 — enum type swap: a numeric taskType is rejected by the same guard the positive test relies on', () => {
    const fixture = loadWireContractFixture<UserTask>('api/tasks/tasks.populated.json');
    // Sanity: the real, unmutated corpus value is recognized.
    expect(isKnownTaskType(fixture.taskType)).toBe(true);

    // Mutation: clone the real fixture and swap only `taskType` from its
    // string-enum member to the raw numeric equivalent (e.g. server switches
    // from JsonStringEnumConverter to the default integer enum serializer).
    const mutated: UserTask = { ...fixture, taskType: 4 as unknown as string };

    expect(isKnownTaskType(mutated.taskType)).toBe(false);
  });

  it('class 3 — required-array→null swap: a nulled array field diverges from the positive test\'s exact-equality assertion', async () => {
    const fixture = loadWireContractFixture<AdminOverviewDto>(
      'api/admin-overview/overview.live-shape.json'
    );
    // Sanity: this is exactly what the positive corpus test in
    // useAdminOverview.test.tsx asserts on for this fixture. `subsystems` is
    // a non-optional field on AdminOverviewDto (unlike e.g. QueueOverviewDto's
    // optional `supportedMaterials`), so a null here is a genuine
    // required-array regression, not a value within the declared type.
    expect(fixture.subsystems).toHaveLength(4);

    // Mutation: clone the real fixture and null out exactly one required
    // array field — server sends null instead of the required `subsystems`
    // collection (a common ORM "no rows joined" / serializer regression on a
    // non-nullable collection).
    const mutated = {
      ...fixture,
      subsystems: null as unknown as SubsystemHealthDto[],
    };
    vi.mocked(apiClient.get).mockResolvedValue({ data: mutated });

    const { result } = renderHook(() => useAdminOverview(), {
      wrapper: makeWrapper(makeClient()),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // The positive test's assertion (`data.subsystems` has length 4) now
    // fails: the hook passes the DTO through unchanged, so the nulled
    // required array reaches the consumer instead of the expected tiles.
    expect(result.current.data?.subsystems).toBeNull();
    expect(result.current.data?.subsystems).not.toEqual(fixture.subsystems);
  });
});
