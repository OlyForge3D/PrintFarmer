import "@testing-library/jest-dom";
import React from "react";
import { act, renderHook, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";

const hoisted = vi.hoisted(() => ({
  apiGet: vi.fn(),
  apiPost: vi.fn(),
  apiPut: vi.fn(),
  apiDelete: vi.fn(),
  cb: { current: null as null | ((event: unknown) => void) },
  signalR: {
    connect: vi.fn().mockResolvedValue(undefined),
    onFallbackGroupsUpdated: vi.fn(),
  },
}));

vi.mock("@/services/api", () => ({
  apiClient: {
    get: hoisted.apiGet,
    post: hoisted.apiPost,
    put: hoisted.apiPut,
    delete: hoisted.apiDelete,
  },
}));

vi.mock("@/services/printer-signalr", () => {
  hoisted.signalR.onFallbackGroupsUpdated = vi.fn((cb: (event: unknown) => void) => {
    hoisted.cb.current = cb;
    return () => {
      hoisted.cb.current = null;
    };
  });
  return { printerSignalRService: hoisted.signalR };
});

import {
  __resetFallbackGroupsSubscriptionForTests,
  fallbackGroupsQueryKeys,
  useCreateFallbackGroup,
  useDeleteFallbackGroup,
  useFallbackGroups,
  useReorderFallbackGroupMembers,
  useUpdateFallbackGroup,
} from "../hooks";

function makeClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, refetchOnWindowFocus: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
}

function wrapper(client: QueryClient) {
  return function Wrapper({ children }: { children: React.ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  };
}

const oneGroup = {
  id: "g",
  printerId: "p1",
  name: "PLA",
  materialType: "PLA",
  displayOrder: 0,
  createdAt: "",
  updatedAt: "",
  members: [
    {
      id: "m1",
      toolheadId: "t1",
      position: 0,
      toolheadName: "Left",
      toolheadIndex: 0,
      currentMaterial: "PLA",
      currentSpoolId: 1,
      materialMatches: true,
    },
    {
      id: "m2",
      toolheadId: "t2",
      position: 1,
      toolheadName: "Right",
      toolheadIndex: 1,
      currentMaterial: "PLA",
      currentSpoolId: 2,
      materialMatches: true,
    },
  ],
};

describe("fallback-groups hooks", () => {
  beforeEach(() => {
    hoisted.apiGet.mockReset();
    hoisted.apiPost.mockReset();
    hoisted.apiPut.mockReset();
    hoisted.apiDelete.mockReset();
    hoisted.signalR.connect.mockClear();
    hoisted.signalR.onFallbackGroupsUpdated.mockClear();
    __resetFallbackGroupsSubscriptionForTests();
    hoisted.cb.current = null;
  });

  afterEach(() => {
    __resetFallbackGroupsSubscriptionForTests();
  });

  it("useFallbackGroups fetches and decodes the list; SignalR subscription is registered", async () => {
    hoisted.apiGet.mockResolvedValueOnce({ data: [oneGroup] });
    const qc = makeClient();
    const { result } = renderHook(() => useFallbackGroups("p1"), { wrapper: wrapper(qc) });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.[0].id).toBe("g");
    expect(hoisted.signalR.onFallbackGroupsUpdated).toHaveBeenCalledTimes(1);
  });

  it("does not fire the query when printerId is null", async () => {
    const qc = makeClient();
    const { result } = renderHook(() => useFallbackGroups(null), { wrapper: wrapper(qc) });
    // Give React Query a tick — it should never actually call the service.
    await new Promise((r) => setTimeout(r, 10));
    expect(hoisted.apiGet).not.toHaveBeenCalled();
    expect(result.current.fetchStatus).toBe("idle");
  });

  it("surfaces errors on the standard error field", async () => {
    hoisted.apiGet.mockRejectedValueOnce({ statusCode: 500, message: "boom" });
    const qc = makeClient();
    const { result } = renderHook(() => useFallbackGroups("p1"), { wrapper: wrapper(qc) });
    await waitFor(() => expect(result.current.isError).toBe(true));
    expect((result.current.error as { message: string }).message).toBe("boom");
  });

  it("invalidates only the matching printer when the SignalR event carries a printerId", async () => {
    hoisted.apiGet.mockResolvedValue({ data: [oneGroup] });
    const qc = makeClient();
    const { result } = renderHook(() => useFallbackGroups("p1"), { wrapper: wrapper(qc) });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    // Also seed a second printer's cache so we can prove selective invalidation.
    qc.setQueryData(fallbackGroupsQueryKeys.byPrinter("other"), [oneGroup]);
    const invalidate = vi.spyOn(qc, "invalidateQueries");

    await act(async () => {
      hoisted.cb.current?.({ printerId: "p1" });
    });
    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: fallbackGroupsQueryKeys.byPrinter("p1") }),
    );
    // No fleet-wide invalidation for a scoped event.
    expect(invalidate).not.toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: fallbackGroupsQueryKeys.all }),
    );
  });

  it("invalidates the whole feature namespace when the event has no printerId", async () => {
    hoisted.apiGet.mockResolvedValue({ data: [oneGroup] });
    const qc = makeClient();
    renderHook(() => useFallbackGroups("p1"), { wrapper: wrapper(qc) });
    await waitFor(() => expect(hoisted.apiGet).toHaveBeenCalled());
    const invalidate = vi.spyOn(qc, "invalidateQueries");
    await act(async () => {
      hoisted.cb.current?.({ printerId: null });
    });
    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: fallbackGroupsQueryKeys.all }),
    );
  });

  it("ref-counts the SignalR subscription across concurrent hooks", async () => {
    hoisted.apiGet.mockResolvedValue({ data: [] });
    const qc = makeClient();
    const first = renderHook(() => useFallbackGroups("p1"), { wrapper: wrapper(qc) });
    const second = renderHook(() => useFallbackGroups("p2"), { wrapper: wrapper(qc) });
    await waitFor(() => {
      expect(first.result.current.isSuccess).toBe(true);
      expect(second.result.current.isSuccess).toBe(true);
    });
    // Only one physical subscription regardless of consumer count.
    expect(hoisted.signalR.onFallbackGroupsUpdated).toHaveBeenCalledTimes(1);

    first.unmount();
    // Still subscribed while the second consumer is mounted.
    expect(hoisted.cb.current).not.toBeNull();
    second.unmount();
    // Subscription cleaned up once refcount hits zero.
    expect(hoisted.cb.current).toBeNull();
  });

  it("create mutation invalidates the printer list on success", async () => {
    hoisted.apiPost.mockResolvedValueOnce({ data: { ...oneGroup, id: "new" } });
    const qc = makeClient();
    const invalidate = vi.spyOn(qc, "invalidateQueries");
    const { result } = renderHook(() => useCreateFallbackGroup("p1"), { wrapper: wrapper(qc) });
    await act(async () => {
      await result.current.mutateAsync({ name: "New", materialType: "PLA", toolheadIds: ["t1"] });
    });
    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: fallbackGroupsQueryKeys.byPrinter("p1") }),
    );
  });

  it("update / delete / reorder all invalidate the printer list", async () => {
    hoisted.apiPut.mockResolvedValue({ data: oneGroup });
    hoisted.apiDelete.mockResolvedValue({ data: undefined });
    const qc = makeClient();
    const invalidate = vi.spyOn(qc, "invalidateQueries");

    const update = renderHook(() => useUpdateFallbackGroup("p1"), { wrapper: wrapper(qc) });
    await act(async () => {
      await update.result.current.mutateAsync({
        groupId: "g",
        request: { name: "PLA", materialType: "PLA", toolheadIds: ["t1"] },
      });
    });

    const del = renderHook(() => useDeleteFallbackGroup("p1"), { wrapper: wrapper(qc) });
    await act(async () => {
      await del.result.current.mutateAsync("g");
    });

    const reorder = renderHook(() => useReorderFallbackGroupMembers("p1"), { wrapper: wrapper(qc) });
    await act(async () => {
      await reorder.result.current.reorder(oneGroup, ["t2", "t1"]);
    });

    expect(hoisted.apiPut).toHaveBeenLastCalledWith(
      "/printers/p1/fallback-groups/g",
      expect.objectContaining({ toolheadIds: ["t2", "t1"] }),
    );

    const printerKey = fallbackGroupsQueryKeys.byPrinter("p1");
    const invalidations = invalidate.mock.calls.filter(([arg]) => {
      const key = (arg as { queryKey?: readonly unknown[] }).queryKey;
      return Array.isArray(key) && key.length === printerKey.length &&
        key.every((v, i) => v === printerKey[i]);
    });
    // Three mutations → at least three invalidations for the printer key.
    expect(invalidations.length).toBeGreaterThanOrEqual(3);
  });
});
