import { describe, it, expect, vi, beforeEach } from 'vitest';
import { loadWireContractFixture } from '@/test/wireContracts';
import { tasksApi, isKnownTaskType, type UserTask } from '@/services/tasksApi';
import { apiClient as tasksApiClient } from '@/services/api';
import { ApiClient } from '@/services/api';
import type { QueueOverviewDto } from '@/types/api';

// -----------------------------------------------------------------------------
// Mutation-control tests (issue #2240)
//
// Purpose: the corpus-driven tests added for #2240 assert that specific
// production code paths (tasksApi.getPendingTasks/createTask,
// ApiClient.getQueueOverview, printer-signalr/slicerHubService handlers,
// etc.) faithfully pass through the canonical wire-contract corpus. That is
// only meaningful evidence if those same tests would actually FAIL when the
// wire shape regresses. This file proves it for the three mutation classes
// called out in the epic:
//
//   1. Key rename       — a field the client reads is renamed server-side.
//   2. Enum type swap    — a string-enum member is replaced by a raw number.
//   3. Required-array→null swap — an array field the client expects is sent
//      as null instead (a common ORM/serializer regression).
//
// Every mutation below is derived from a REAL corpus fixture (never
// hand-invented data) by cloning it and altering exactly one field, then
// exercising the SAME production function the corresponding positive test
// exercises, and showing the specific assertion that the positive test
// relies on now fails/differs. This is deliberately not the exact same
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

describe('Mutation-control (#2240): corpus-driven tests must fail on wire-contract regressions', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('class 1 — key rename: renaming a field the client reads breaks the pass-through the positive test asserts on', async () => {
    const fixture = loadWireContractFixture<UserTask>('api/tasks/tasks.populated.json');
    // Sanity: this is exactly what the positive corpus test in tasksApi.test.ts
    // asserts on for this fixture.
    expect(fixture.title).toBe('Wire contract manual task');

    // Mutation: server renames `title` → `taskTitle` (a plausible DTO rename).
    const { title, ...rest } = fixture;
    const mutated = { ...rest, taskTitle: title } as unknown as UserTask;

    vi.mocked(tasksApiClient.get).mockResolvedValue({ data: [mutated] });

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

    // Mutation: string-enum → numeric swap (e.g. server switches from
    // JsonStringEnumConverter to the default integer enum serialization).
    const mutatedTaskType = 4 as unknown as string;

    expect(isKnownTaskType(mutatedTaskType)).toBe(false);
  });

  it('class 3 — required-array→null swap: a nulled array field diverges from the positive test\'s exact-equality assertion', async () => {
    const fixture = loadWireContractFixture<QueueOverviewDto[]>(
      'api/print-queue/queue.populated.json'
    );
    // Sanity: this is exactly what the positive corpus test in api.test.ts
    // asserts on for this fixture.
    expect(fixture[0].supportedMaterials).toEqual(['PLA', 'PETG']);

    // Mutation: server sends null instead of an empty/populated array (a
    // common ORM "no rows joined" regression).
    const mutated = [
      { ...fixture[0], supportedMaterials: null as unknown as string[] },
      ...fixture.slice(1),
    ];

    const client = new ApiClient();
    const mockGet = vi.fn().mockResolvedValue({ data: mutated });
    (client as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

    const [result] = await client.getQueueOverview();

    expect(result.supportedMaterials).not.toEqual(['PLA', 'PETG']);
    expect(result.supportedMaterials).toBeNull();
  });
});
