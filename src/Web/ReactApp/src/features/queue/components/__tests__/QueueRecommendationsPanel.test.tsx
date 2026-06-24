import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import "@testing-library/jest-dom";
import { QueueRecommendationsPanel } from "../QueueRecommendationsPanel";
import type { QueueRecommendationDto } from "@/types/api";

describe("QueueRecommendationsPanel", () => {
  it("renders recommendation items with action text and impact count", () => {
    const recommendations: QueueRecommendationDto[] = [
      {
        category: "material-mismatch",
        title: "Material mismatch",
        actionText: "Load matching material on compatible printers to unlock blocked jobs.",
        estimatedUnlockedJobCount: 3,
        priorityScore: 3,
      },
      {
        category: "idle-printer-opportunity",
        title: "Idle printer opportunity",
        actionText: "Dispatch queued jobs to currently idle compatible printers.",
        estimatedUnlockedJobCount: 2,
        priorityScore: 2,
      },
    ];

    render(<QueueRecommendationsPanel recommendations={recommendations} isLoading={false} />);

    expect(screen.getByText("Queue To-Do Recommendations")).toBeInTheDocument();
    expect(screen.getByText("Material mismatch")).toBeInTheDocument();
    expect(screen.getByText("Idle printer opportunity")).toBeInTheDocument();
    expect(screen.getByText("+3 jobs")).toBeInTheDocument();
    expect(screen.getByText("+2 jobs")).toBeInTheDocument();
  });

  it("renders empty state when no actionable recommendations exist", () => {
    render(<QueueRecommendationsPanel recommendations={[]} isLoading={false} />);

    expect(
      screen.getByText("No actionable recommendations right now. Queue constraints are currently satisfied.")
    ).toBeInTheDocument();
  });
});
