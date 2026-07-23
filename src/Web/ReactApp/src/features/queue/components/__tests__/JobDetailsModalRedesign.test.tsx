import { render, screen, act } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import "@testing-library/jest-dom";
import type { JobDetails } from "@/types/queue";

// Mock the API client used by the modal (fetch job details) and its child
// JobDetailsSection (filament list — only fires in edit mode).
const getAnalyticsJobDetails = vi.fn();
const getFilaments = vi.fn().mockResolvedValue([]);
vi.mock("@/services/api", () => ({
  apiClient: {
    getAnalyticsJobDetails: (id: string) => getAnalyticsJobDetails(id),
    getFilaments: () => getFilaments(),
  },
}));

import JobDetailsModal from "../JobDetailsModal";

const mockDetails: JobDetails = {
  id: "job-1",
  name: "dragon-model.gcode",
  status: "Completed",
  priority: 0,
  queuePosition: 0,
  printerName: "Snapmaker U1",
  printerModel: "U1",
  materialType: "PLA",
  nozzleDiameter: 0.4,
  energyCostUsd: 0.5,
  materialCostUsd: 3.34,
  machineTimeCostUsd: 8.5,
  totalCostUsd: 12.34,
};

describe("JobDetailsModal redesign", () => {
  beforeEach(() => {
    getAnalyticsJobDetails.mockReset();
    getAnalyticsJobDetails.mockResolvedValue(mockDetails);
  });

  // The modal fetches its data via React 19 use()+Suspense, so the render must
  // happen inside an awaited act() for the suspended content to resolve.
  const renderModal = async () => {
    await act(async () => {
      render(<JobDetailsModal jobId="job-1" isOpen onClose={() => {}} />);
    });
  };

  it("renders the filename exactly once (no duplicate name/File card)", async () => {
    await renderModal();
    expect(screen.getAllByText("dragon-model.gcode")).toHaveLength(1);
    // The standalone "File" card heading was removed.
    expect(
      screen.queryByRole("heading", { name: "File" })
    ).not.toBeInTheDocument();
  });

  it("renders the status badge exactly once", async () => {
    await renderModal();
    // Scope to the badge <span>; the Timeline has a "Completed" column label too.
    expect(screen.getAllByText("Completed", { selector: "span" })).toHaveLength(1);
  });

  it("promotes the total cost with a prominent value", async () => {
    await renderModal();
    expect(screen.getByText("Total Cost")).toBeInTheDocument();
    expect(screen.getByText("$12.34")).toBeInTheDocument();
  });
});
