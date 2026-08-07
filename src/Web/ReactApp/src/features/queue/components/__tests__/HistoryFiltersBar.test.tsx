import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import HistoryFiltersBar from "../HistoryFiltersBar";
import ModelFiltersBar from "../ModelFiltersBar";

const renderHistoryFilters = ({
  selectedStatuses = ["completed"],
  viewMode = "cards" as const,
  onStatusChange = vi.fn(),
  onSortChange = vi.fn(),
  onViewModeChange = vi.fn(),
} = {}) => {
  render(
    <HistoryFiltersBar
      selectedStatuses={selectedStatuses}
      onStatusChange={onStatusChange}
      sortBy="newest"
      onSortChange={onSortChange}
      onRefresh={vi.fn().mockResolvedValue(undefined)}
      isLoading={false}
      viewMode={viewMode}
      onViewModeChange={onViewModeChange}
    />,
  );

  fireEvent.click(screen.getByRole("button", { name: /Filters/ }));

  return { onStatusChange, onSortChange, onViewModeChange };
};

describe("HistoryFiltersBar accessibility", () => {
  it("exposes stable names and selected state for every status toggle", () => {
    renderHistoryFilters();

    expect(screen.getByRole("button", { name: "Done" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: "Failed" })).toHaveAttribute("aria-pressed", "false");
    expect(screen.getByRole("button", { name: "Cancelled" })).toHaveAttribute("aria-pressed", "false");
  });

  it("preserves status toggle click behavior", () => {
    const onStatusChange = vi.fn();
    renderHistoryFilters({ onStatusChange });

    fireEvent.click(screen.getByRole("button", { name: "Failed" }));
    expect(onStatusChange).toHaveBeenCalledWith(["completed", "failed"]);

    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(onStatusChange).toHaveBeenCalledWith([]);
  });

  it("exposes stable names and selected state for both view-mode toggles", () => {
    const onViewModeChange = vi.fn();
    renderHistoryFilters({ viewMode: "table", onViewModeChange });

    expect(screen.getByRole("button", { name: "Card view" })).toHaveAttribute("aria-pressed", "false");
    expect(screen.getByRole("button", { name: "Table view" })).toHaveAttribute("aria-pressed", "true");

    fireEvent.click(screen.getByRole("button", { name: "Card view" }));
    expect(onViewModeChange).toHaveBeenCalledWith("cards");
  });

  it("gives the sort control an unambiguous name and preserves the model option behavior", () => {
    const onSortChange = vi.fn();
    renderHistoryFilters({ onSortChange });

    const sortControl = screen.getByRole("combobox", { name: "Sort history jobs" });
    expect(screen.getByRole("option", { name: "Model" })).toBeInTheDocument();

    fireEvent.change(sortControl, { target: { value: "model" } });
    expect(onSortChange).toHaveBeenCalledWith("model");
  });
});

describe("ModelFiltersBar accessibility", () => {
  it("associates visible labels and keeps status toggle names stable on click", () => {
    const onStatusChange = vi.fn();
    render(
      <ModelFiltersBar
        models={["Core One"]}
        selectedModel={null}
        onModelChange={vi.fn()}
        selectedStatuses={["queued", "paused"]}
        onStatusChange={onStatusChange}
        sortBy="name"
        onSortChange={vi.fn()}
        onRefresh={vi.fn()}
        isLoading={false}
      />,
    );

    expect(screen.getByRole("combobox", { name: "Printer Model" })).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Sort By" })).toBeInTheDocument();
    expect(screen.getByRole("group", { name: "Job Status" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Queued" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: "Printing" })).toHaveAttribute("aria-pressed", "false");
    expect(screen.getByRole("button", { name: "Paused" })).toHaveAttribute("aria-pressed", "true");

    fireEvent.click(screen.getByRole("button", { name: "Printing" }));
    expect(onStatusChange).toHaveBeenCalledWith(["queued", "paused", "printing"]);
  });
});
