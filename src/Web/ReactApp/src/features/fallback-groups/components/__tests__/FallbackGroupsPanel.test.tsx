import "@testing-library/jest-dom";
import React from "react";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
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
    onFilamentCoverageChanged: vi.fn(),
  },
  filamentCoverage: {
    getFleetCoverage: vi.fn(),
    getPrinterCoverage: vi.fn(),
  },
  filamentTypes: vi.fn(),
  // Mutable per-test coverage payload for usePrinterFilamentCoverage (see
  // mock below). Defaults to no toolhead rows since coverage isn't
  // exercised by most of this file's tests.
  coverageData: { printerId: "p1", toolheads: [] as unknown[] },
}));

vi.mock("@/services/api", () => ({
  apiClient: {
    get: hoisted.apiGet,
    post: hoisted.apiPost,
    put: hoisted.apiPut,
    delete: hoisted.apiDelete,
    getFilamentTypes: hoisted.filamentTypes,
  },
}));

vi.mock("@/services/printer-signalr", () => {
  hoisted.signalR.onFallbackGroupsUpdated = vi.fn((cb: (event: unknown) => void) => {
    hoisted.cb.current = cb;
    return () => {
      hoisted.cb.current = null;
    };
  });
  hoisted.signalR.onFilamentCoverageChanged = vi.fn(() => () => undefined);
  return { printerSignalRService: hoisted.signalR };
});

// Coverage lookup is exercised per-test via `hoisted.coverageData`; most
// tests in this file don't care and rely on the empty-toolheads default.
vi.mock("@/features/filament-coverage/hooks", () => ({
  usePrinterFilamentCoverage: () => ({ data: hoisted.coverageData, isSuccess: true }),
}));

import { FallbackGroupsPanel } from "../FallbackGroupsPanel";
import { __resetFallbackGroupsSubscriptionForTests } from "../../hooks";
import type { ToolheadDto } from "@/types/api";

const physicalToolheads: ToolheadDto[] = [
  {
    id: "t1",
    name: "Left",
    index: 0,
    isPrimary: true,
    toolheadType: "Physical",
    currentSpoolId: 1,
    currentMaterial: "PLA",
  },
  {
    id: "t2",
    name: "Right",
    index: 1,
    isPrimary: false,
    toolheadType: "Physical",
    currentSpoolId: 2,
    currentMaterial: "PLA",
  },
  {
    id: "t3",
    name: "Third",
    index: 2,
    isPrimary: false,
    toolheadType: "Physical",
    currentSpoolId: undefined,
    currentMaterial: undefined,
  },
];

const twoGroupResponse = [
  {
    id: "g1",
    printerId: "p1",
    name: "PLA lineup",
    materialType: "PLA",
    displayOrder: 0,
    createdAt: "",
    updatedAt: "",
    members: [
      { id: "m1", toolheadId: "t1", position: 0, toolheadName: "Left", toolheadIndex: 0, currentMaterial: "PLA", currentSpoolId: 1, materialMatches: true },
      { id: "m2", toolheadId: "t2", position: 1, toolheadName: "Right", toolheadIndex: 1, currentMaterial: "PLA", currentSpoolId: 2, materialMatches: true },
    ],
  },
];

function renderPanel(toolheads = physicalToolheads, isOnline?: boolean) {
  const qc = new QueryClient({
    defaultOptions: {
      queries: { retry: false, refetchOnWindowFocus: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return {
    qc,
    ...render(
      <QueryClientProvider client={qc}>
        <FallbackGroupsPanel printerId="p1" toolheads={toolheads} isOnline={isOnline} />
      </QueryClientProvider>,
    ),
  };
}

describe("FallbackGroupsPanel", () => {
  beforeEach(() => {
    hoisted.apiGet.mockReset();
    hoisted.apiPost.mockReset();
    hoisted.apiPut.mockReset();
    hoisted.apiDelete.mockReset();
    hoisted.filamentTypes.mockReset();
    hoisted.filamentTypes.mockResolvedValue([{ id: "pla", name: "PLA", defaultTemperatures: {}, isAbrasive: false, needsEnclosure: false }]);
    __resetFallbackGroupsSubscriptionForTests();
    hoisted.cb.current = null;
    hoisted.coverageData = { printerId: "p1", toolheads: [] };
  });

  afterEach(() => {
    __resetFallbackGroupsSubscriptionForTests();
  });

  it("does not render when there are fewer than 2 physical toolheads", () => {
    const single: ToolheadDto[] = [physicalToolheads[0]];
    const { container } = renderPanel(single);
    expect(container.firstChild).toBeNull();
  });

  it("shows a loading indicator while fetching", async () => {
    hoisted.apiGet.mockImplementation(() => new Promise(() => {}));
    renderPanel();
    expect(await screen.findByText(/loading fallback chains/i)).toBeInTheDocument();
  });

  it("shows an empty state when there are no groups", async () => {
    hoisted.apiGet.mockResolvedValueOnce({ data: [] });
    renderPanel();
    expect(await screen.findByText(/no fallback chains configured/i)).toBeInTheDocument();
  });

  it("renders configured groups with chain state and members", async () => {
    hoisted.apiGet.mockResolvedValueOnce({ data: twoGroupResponse });
    renderPanel();
    expect(await screen.findByTestId("fallback-group-g1")).toBeInTheDocument();
    const chain = await screen.findByTestId("fallback-chain-display");
    const rows = within(chain).getAllByRole("listitem");
    expect(rows).toHaveLength(2);
    expect(within(rows[0]).getByText("Active")).toBeInTheDocument();
    expect(within(rows[1]).getByText("Backup ready")).toBeInTheDocument();
  });

  it("renders an error alert when the fetch fails", async () => {
    hoisted.apiGet.mockRejectedValueOnce({ statusCode: 500, message: "boom" });
    renderPanel();
    expect(await screen.findByRole("alert")).toHaveTextContent(/boom/i);
  });

  it("opens the create modal, filters MMU toolheads, and creates on submit", async () => {
    hoisted.apiGet.mockResolvedValueOnce({ data: [] });
    hoisted.apiPost.mockResolvedValueOnce({ data: { ...twoGroupResponse[0], id: "created" } });
    // After the mutation, list is re-fetched.
    hoisted.apiGet.mockResolvedValueOnce({ data: [{ ...twoGroupResponse[0], id: "created" }] });
    renderPanel([
      ...physicalToolheads,
      { id: "gate1", name: "Gate 1", index: 4, isPrimary: false, toolheadType: "MmuGate" } as ToolheadDto,
    ]);
    await screen.findByText(/no fallback chains configured/i);
    fireEvent.click(screen.getByRole("button", { name: /add a new fallback chain/i }));
    // Modal is open — the MMU gate must not appear in the add-toolhead select.
    const addSelect = await screen.findByLabelText(/add physical toolhead/i);
    const options = within(addSelect as HTMLSelectElement).getAllByRole("option");
    expect(options.some((o) => /Gate 1/i.test(o.textContent ?? ""))).toBe(false);

    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "PETG chain" } });
    fireEvent.change(screen.getByLabelText(/^material$/i), { target: { value: "PETG" } });
    // Add first available toolhead.
    fireEvent.click(screen.getByRole("button", { name: /add selected toolhead to chain/i }));
    // Add second toolhead (dropdown auto-advances to next available).
    fireEvent.click(screen.getByRole("button", { name: /add selected toolhead to chain/i }));

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /create chain/i }));
    });

    await waitFor(() => expect(hoisted.apiPost).toHaveBeenCalled());
    const [, body] = hoisted.apiPost.mock.calls[0];
    expect(body).toMatchObject({ name: "PETG chain", materialType: "PETG" });
    expect(body.toolheadIds).toHaveLength(2);
  });

  it("shows client-side validation errors before submitting", async () => {
    hoisted.apiGet.mockResolvedValueOnce({ data: [] });
    renderPanel();
    await screen.findByText(/no fallback chains configured/i);
    fireEvent.click(screen.getByRole("button", { name: /add a new fallback chain/i }));
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /create chain/i }));
    });
    expect(await screen.findByText(/name is required/i)).toBeInTheDocument();
    expect(screen.getByText(/material type is required/i)).toBeInTheDocument();
    // Backend not called.
    expect(hoisted.apiPost).not.toHaveBeenCalled();
  });

  it("keyboard reorder buttons issue a PUT with the new ordering", async () => {
    hoisted.apiGet.mockResolvedValueOnce({ data: twoGroupResponse });
    hoisted.apiPut.mockResolvedValueOnce({ data: twoGroupResponse[0] });
    hoisted.apiGet.mockResolvedValueOnce({ data: twoGroupResponse });
    renderPanel();
    const chain = await screen.findByTestId("fallback-chain-display");
    const moveDown = within(chain).getByRole("button", { name: /move position 1 — t0 left down/i });
    await act(async () => {
      fireEvent.click(moveDown);
    });
    await waitFor(() => expect(hoisted.apiPut).toHaveBeenCalled());
    const [, body] = hoisted.apiPut.mock.calls[0];
    expect(body.toolheadIds).toEqual(["t2", "t1"]);
  });

  it("delete flow opens confirmation modal then issues DELETE", async () => {
    hoisted.apiGet.mockResolvedValueOnce({ data: twoGroupResponse });
    hoisted.apiDelete.mockResolvedValueOnce({ data: undefined });
    hoisted.apiGet.mockResolvedValueOnce({ data: [] });
    renderPanel();
    await screen.findByTestId("fallback-group-g1");
    fireEvent.click(screen.getByRole("button", { name: /delete fallback chain pla lineup/i }));
    const confirm = await screen.findByRole("button", { name: /delete chain/i });
    await act(async () => {
      fireEvent.click(confirm);
    });
    await waitFor(() => expect(hoisted.apiDelete).toHaveBeenCalledWith("/printers/p1/fallback-groups/g1"));
  });

  it("surfaces a delete failure at the panel level after the confirmation modal closes", async () => {
    // The confirmation modal auto-closes on both success and failure, so the
    // delete error must be routed to a panel-level Alert rather than the
    // editor's submitError (which is not rendered when the editor is closed).
    hoisted.apiGet.mockResolvedValueOnce({ data: twoGroupResponse });
    hoisted.apiDelete.mockRejectedValueOnce({
      statusCode: 403,
      message: "Request failed with status code 403",
      response: { status: 403 },
    });
    renderPanel();
    await screen.findByTestId("fallback-group-g1");
    fireEvent.click(screen.getByRole("button", { name: /delete fallback chain pla lineup/i }));
    const confirm = await screen.findByRole("button", { name: /delete chain/i });
    await act(async () => {
      fireEvent.click(confirm);
    });
    expect(
      await screen.findByText(/admin role required to configure fallback chains/i),
    ).toBeInTheDocument();
    // And the panel remains visible with the group still listed.
    expect(screen.getByTestId("fallback-group-g1")).toBeInTheDocument();
  });

  it("surfaces a server error message from a failed create", async () => {
    hoisted.apiGet.mockResolvedValueOnce({ data: [] });
    hoisted.apiPost.mockRejectedValueOnce({
      statusCode: 400,
      message: "Request failed with status code 400",
      data: { detail: "A group named \"PLA\" already exists." },
    });
    renderPanel();
    await screen.findByText(/no fallback chains configured/i);
    fireEvent.click(screen.getByRole("button", { name: /add a new fallback chain/i }));
    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "PLA" } });
    fireEvent.change(screen.getByLabelText(/^material$/i), { target: { value: "PLA" } });
    fireEvent.click(screen.getByRole("button", { name: /add selected toolhead to chain/i }));
    fireEvent.click(screen.getByRole("button", { name: /add selected toolhead to chain/i }));
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /create chain/i }));
    });
    expect(await screen.findByText(/a group named "pla" already exists/i)).toBeInTheDocument();
  });

  it("shows the mixed-material warning when a loaded spool material differs from the group", async () => {
    const mismatchGroup = {
      ...twoGroupResponse[0],
      members: [
        { ...twoGroupResponse[0].members[0], currentMaterial: "PETG", materialMatches: false },
        twoGroupResponse[0].members[1],
      ],
    };
    hoisted.apiGet.mockResolvedValueOnce({ data: [mismatchGroup] });
    renderPanel();
    expect(await screen.findByText(/mixed materials in chain/i)).toBeInTheDocument();
  });

  // Regression test for issue #1684: this panel independently fetches
  // per-toolhead coverage and derives "Exhausted" chain state from a raw
  // `status === "runout"` check, bypassing the shared `withOfflineOverride`
  // gating used elsewhere (MaterialLoadout, FilamentCoverageBreakdown,
  // PrinterCoverageSummary) unless `isOnline` is explicitly threaded through.
  it("does not render 'Exhausted' for a last-known runout status when the printer is offline (#1684)", async () => {
    hoisted.coverageData = {
      printerId: "p1",
      toolheads: [
        { toolheadIndex: 0, status: "runout" },
        { toolheadIndex: 1, status: "covers" },
      ],
    };
    hoisted.apiGet.mockResolvedValueOnce({ data: twoGroupResponse });
    renderPanel(physicalToolheads, false);
    await screen.findByTestId("fallback-group-g1");
    expect(screen.queryByText("Exhausted")).not.toBeInTheDocument();
  });

  it("renders 'Exhausted' for a runout toolhead when the printer is online (back-compat)", async () => {
    hoisted.coverageData = {
      printerId: "p1",
      toolheads: [
        { toolheadIndex: 0, status: "runout" },
        { toolheadIndex: 1, status: "covers" },
      ],
    };
    hoisted.apiGet.mockResolvedValueOnce({ data: twoGroupResponse });
    renderPanel(physicalToolheads, true);
    await screen.findByTestId("fallback-group-g1");
    expect(await screen.findByText("Exhausted")).toBeInTheDocument();
  });

  it("refetches the printer's groups when the SignalR event arrives", async () => {
    hoisted.apiGet.mockResolvedValueOnce({ data: [] });
    renderPanel();
    await screen.findByText(/no fallback chains configured/i);
    expect(hoisted.apiGet).toHaveBeenCalledTimes(1);
    hoisted.apiGet.mockResolvedValueOnce({ data: twoGroupResponse });
    await act(async () => {
      hoisted.cb.current?.({ printerId: "p1" });
    });
    await waitFor(() => expect(hoisted.apiGet).toHaveBeenCalledTimes(2));
    expect(await screen.findByTestId("fallback-group-g1")).toBeInTheDocument();
  });

  it("hides the panel when the operator feature is disabled (404 with featureDisabled code)", async () => {
    // Backend gates every fallback endpoint on the MultiSlotFallback operator
    // feature. When it's off the list endpoint returns 404 with the standard
    // ProblemDetails `code: "featureDisabled"` extension — the panel should
    // render nothing instead of a scary error, mirroring filament coverage.
    const err = {
      statusCode: 404,
      message: "Feature disabled",
      response: {
        status: 404,
        data: {
          status: 404,
          title: "Feature disabled",
          detail: "The 'multiSlotFallback' operator feature is disabled by an administrator.",
          type: "https://printfarmer.io/errors/feature-disabled",
          code: "featureDisabled",
          feature: "multiSlotFallback",
        },
      },
    };
    hoisted.apiGet.mockRejectedValueOnce(err);
    const { container } = renderPanel();
    await waitFor(() => expect(hoisted.apiGet).toHaveBeenCalled());
    await waitFor(() => expect(container.firstChild).toBeNull());
  });

  it("renders a friendly admin-required message when a mutation returns 403", async () => {
    // POST/PUT/DELETE now require the `farm_admin` role (round-5 remediation).
    // A bare 403 without a ProblemDetails body should surface as actionable
    // copy instead of the axios boilerplate.
    hoisted.apiGet.mockResolvedValueOnce({ data: [] });
    hoisted.apiPost.mockRejectedValueOnce({
      statusCode: 403,
      message: "Request failed with status code 403",
      response: { status: 403 },
    });
    renderPanel();
    await screen.findByText(/no fallback chains configured/i);
    fireEvent.click(screen.getByRole("button", { name: /add a new fallback chain/i }));
    fireEvent.change(screen.getByLabelText(/^name$/i), { target: { value: "PLA chain" } });
    fireEvent.change(screen.getByLabelText(/^material$/i), { target: { value: "PLA" } });
    fireEvent.click(screen.getByRole("button", { name: /add selected toolhead to chain/i }));
    fireEvent.click(screen.getByRole("button", { name: /add selected toolhead to chain/i }));
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /create chain/i }));
    });
    expect(
      await screen.findByText(/admin role required to configure fallback chains/i),
    ).toBeInTheDocument();
  });
});
