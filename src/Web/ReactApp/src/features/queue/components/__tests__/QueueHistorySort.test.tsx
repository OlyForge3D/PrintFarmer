import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { apiClient } from "@/services/api";
import QueueHistoryTab from "../QueueHistoryTab";

vi.mock("@/services/api", () => ({
  apiClient: { getAnalyticsQueueHistory: vi.fn() },
}));
vi.mock("@/features/harvest/components/HarvestJobAction", () => ({
  HarvestJobAction: () => null,
}));

const statuses = ["completed", "failed", "cancelled"];
const dateFrom = new Date("2026-01-01T00:00:00.000Z");
const dateTo = new Date("2026-01-31T00:00:00.000Z");
const historyRequest = vi.mocked(apiClient.getAnalyticsQueueHistory);

async function expectRequest(mode: string, offset = 0, filters = statuses) {
  await waitFor(() => expect(historyRequest).toHaveBeenLastCalledWith(
    50, offset, mode, filters, dateFrom.toISOString(), dateTo.toISOString(),
  ));
  await screen.findByRole("table");
}

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  historyRequest.mockResolvedValue({
    entries: ["Zulu", "Alpha"].map((name, index) => ({
      id: `history-${index}`,
      jobName: name,
      printerName: "Printer",
      status: "Completed",
      completionPercentage: 100,
      startedAtUtc: "2026-01-01T00:00:00.000Z",
      completedAtUtc: "2026-01-01T01:00:00.000Z",
      actualPrintTimeSeconds: 3600,
    })),
    totalCount: 101,
    currentPage: 0,
    pageSize: 50,
    stats: {
      totalCompleted: 101, totalFailed: 0, totalCancelled: 0,
      successRate: 100, averageDurationMinutes: 60, totalPrintTimeMinutes: 6060,
    },
  });
});

describe("History server-owned header sorting", () => {
  it.each([
    ["Job Name", "name", "name_desc"],
    ["Printer", "printer", "printer_desc"],
    ["Material", "material", "material_desc"],
    ["Status", "status", "status_desc"],
    ["Filament", "filament", "filament_desc"],
    ["Cost", "cost", "cost_desc"],
    ["Duration", "duration_asc", "duration"],
    ["Completed", "oldest", "newest"],
  ])("cycles %s through ascending, descending and the default before paging", async (label, ascending, descending) => {
    const user = userEvent.setup();
    render(<QueueHistoryTab dateFrom={dateFrom} dateTo={dateTo} />);
    await expectRequest("newest");
    await user.click(screen.getByRole("button", { name: "Next" }));
    await expectRequest("newest", 50);
    historyRequest.mockClear();

    for (const [mode, direction] of [
      [ascending, "ascending"], [descending, "descending"], ["newest", "none"],
    ]) {
      await user.click(within(screen.getByRole("columnheader", { name: label }))
        .getByRole("button"));
      if (label === "Completed" && direction === "none") {
        // Clearing the indicator keeps the already-selected newest server mode.
        expect(historyRequest).not.toHaveBeenCalled();
      } else {
        await expectRequest(mode);
        expect(historyRequest).toHaveBeenCalledTimes(1);
      }
      const header = screen.getByRole("columnheader", { name: label });
      expect(header).toHaveAttribute("aria-sort", direction);
      expect(header.querySelectorAll('[aria-hidden="true"] svg')).toHaveLength(2);
      expect(screen.getByRole("combobox", { name: "Sort by" })).toHaveValue(mode);
      // The response order is deliberately opposite to ascending name order.
      expect(within(screen.getByRole("table")).getAllByRole("row").slice(1)
        .map(row => within(row).getAllByRole("cell")[0].textContent)).toEqual(["Zulu", "Alpha"]);
      historyRequest.mockClear();
    }
    expect(screen.getByText("101 total jobs")).toBeInTheDocument();
  });

  it("supports native keyboard sorting and preserves filter, model and card controls", async () => {
    const user = userEvent.setup();
    render(<QueueHistoryTab dateFrom={dateFrom} dateTo={dateTo} />);
    await expectRequest("newest");
    within(screen.getByRole("columnheader", { name: "Duration" })).getByRole("button").focus();
    await user.keyboard("{Enter}");
    await expectRequest("duration_asc");
    within(screen.getByRole("columnheader", { name: "Duration" })).getByRole("button").focus();
    await user.keyboard(" ");
    await expectRequest("duration");

    await user.click(screen.getByRole("button", { name: "Filters" }));
    await user.click(screen.getByRole("button", { name: "Failed" }));
    await expectRequest("duration", 0, ["completed", "cancelled"]);
    await user.selectOptions(screen.getByRole("combobox", { name: "Sort by" }), "model");
    await expectRequest("model", 0, ["completed", "cancelled"]);
    expect(screen.getAllByRole("columnheader").filter(header => header.hasAttribute("aria-sort"))
      .every(header => header.getAttribute("aria-sort") === "none")).toBe(true);
    await user.click(screen.getByRole("button", { name: "Card view" }));
    expect(screen.queryByRole("table")).not.toBeInTheDocument();
    expect(localStorage.getItem("printfarmer-queue-history-viewmode")).toBe("cards");
    await user.selectOptions(screen.getByRole("combobox", { name: "Sort by" }), "model_desc");
    await waitFor(() => expect(historyRequest).toHaveBeenLastCalledWith(
      50, 0, "model_desc", ["completed", "cancelled"], dateFrom.toISOString(), dateTo.toISOString(),
    ));
    await screen.findByText("Zulu");
    await user.click(screen.getByRole("button", { name: "Table view" }));
    await screen.findByRole("table");
    expect(screen.getByRole("combobox", { name: "Sort by" })).toHaveValue("model_desc");
    await user.click(screen.getByRole("button", { name: "Refresh" }));
    await expectRequest("model_desc", 0, ["completed", "cancelled"]);
  });
});
