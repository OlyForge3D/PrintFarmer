import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import { TableFiltersBar } from "../QueueFiltersBar";

describe("TableFiltersBar Component", () => {
  it("should render all filter fields", () => {
    const mockHandlers = {
      onStatusChange: vi.fn(),
      onModelChange: vi.fn(),
      onMaterialChange: vi.fn(),
      onSortChange: vi.fn(),
      onRefresh: vi.fn(),
    };

    render(<TableFiltersBar {...mockHandlers} />);

    expect(screen.getByLabelText("Filter by status")).toBeInTheDocument();
    expect(screen.getByLabelText("Filter by printer model")).toBeInTheDocument();
    expect(screen.getByLabelText("Filter by material")).toBeInTheDocument();
    expect(screen.getByTitle("Refresh data")).toBeInTheDocument();
  });

  it("should call onStatusChange when status filter changes", () => {
    const onStatusChange = vi.fn();
    const mockHandlers = {
      onStatusChange,
      onModelChange: vi.fn(),
      onMaterialChange: vi.fn(),
      onSortChange: vi.fn(),
      onRefresh: vi.fn(),
    };

    render(<TableFiltersBar {...mockHandlers} />);

    const statusSelect = screen.getByDisplayValue("Active Jobs") as HTMLSelectElement;
    fireEvent.change(statusSelect, { target: { value: "Queued" } });

    expect(onStatusChange).toHaveBeenCalledWith("Queued");
  });

  it("should call onModelChange when model filter changes", () => {
    const onModelChange = vi.fn();
    const mockHandlers = {
      onStatusChange: vi.fn(),
      onModelChange,
      onMaterialChange: vi.fn(),
      onSortChange: vi.fn(),
      onRefresh: vi.fn(),
    };

    render(<TableFiltersBar {...mockHandlers} />);

    const modelInput = screen.getByPlaceholderText("Printer model...") as HTMLInputElement;
    fireEvent.change(modelInput, { target: { value: "Prusa CORE One" } });

    expect(onModelChange).toHaveBeenCalledWith("Prusa CORE One");
  });

  it("should call onMaterialChange when material filter changes", () => {
    const onMaterialChange = vi.fn();
    const mockHandlers = {
      onStatusChange: vi.fn(),
      onModelChange: vi.fn(),
      onMaterialChange,
      onSortChange: vi.fn(),
      onRefresh: vi.fn(),
    };

    render(<TableFiltersBar {...mockHandlers} />);

    const materialSelect = screen.getByDisplayValue("All Materials") as HTMLSelectElement;
    fireEvent.change(materialSelect, { target: { value: "PLA" } });

    expect(onMaterialChange).toHaveBeenCalledWith("PLA");
  });

  it("should call onRefresh when refresh button is clicked", () => {
    const onRefresh = vi.fn();
    const mockHandlers = {
      onStatusChange: vi.fn(),
      onModelChange: vi.fn(),
      onMaterialChange: vi.fn(),
      onSortChange: vi.fn(),
      onRefresh,
    };

    render(<TableFiltersBar {...mockHandlers} />);

    const refreshButton = screen.getByTitle("Refresh data");
    fireEvent.click(refreshButton);

    expect(onRefresh).toHaveBeenCalled();
  });

  it("should clear all filters when clear button is clicked", () => {
    const onStatusChange = vi.fn();
    const onModelChange = vi.fn();
    const onMaterialChange = vi.fn();
    const onSortChange = vi.fn();
    const mockHandlers = {
      onStatusChange,
      onModelChange,
      onMaterialChange,
      onSortChange,
      onRefresh: vi.fn(),
    };

    render(<TableFiltersBar {...mockHandlers} />);

    // Set a filter first so the clear button appears
    const statusSelect = screen.getByDisplayValue("Active Jobs") as HTMLSelectElement;
    fireEvent.change(statusSelect, { target: { value: "Queued" } });

    // Click clear button (Reset all filters)
    const clearButton = screen.getByTitle("Reset all filters");
    fireEvent.click(clearButton);

    // Verify all handlers were called with null
    expect(onStatusChange).toHaveBeenCalledWith(null);
    expect(onModelChange).toHaveBeenCalledWith(null);
    expect(onMaterialChange).toHaveBeenCalledWith(null);
    expect(onSortChange).toHaveBeenCalledWith("priority");
  });

  it("should disable refresh button when loading", () => {
    const mockHandlers = {
      onStatusChange: vi.fn(),
      onModelChange: vi.fn(),
      onMaterialChange: vi.fn(),
      onSortChange: vi.fn(),
      onRefresh: vi.fn(),
    };

    render(<TableFiltersBar {...mockHandlers} isLoading={true} />);

    const refreshButton = screen.getByTitle("Refresh data") as HTMLButtonElement;
    expect(refreshButton).toBeDisabled();
  });

  it("should display active status options and exclude terminal states", () => {
    const mockHandlers = {
      onStatusChange: vi.fn(),
      onModelChange: vi.fn(),
      onMaterialChange: vi.fn(),
      onSortChange: vi.fn(),
      onRefresh: vi.fn(),
    };

    render(<TableFiltersBar {...mockHandlers} />);

    const statusSelect = screen.getByDisplayValue("Active Jobs") as HTMLSelectElement;
    const options = statusSelect.querySelectorAll("option");

    const statusValues = Array.from(options).map((o) => o.value);
    expect(statusValues).toContain("Queued");
    expect(statusValues).toContain("Assigned");
    expect(statusValues).toContain("Starting");
    expect(statusValues).toContain("Printing");
    expect(statusValues).toContain("Paused");

    // Terminal states live on the History tab and must not be selectable here.
    expect(statusValues).not.toContain("Completed");
    expect(statusValues).not.toContain("Failed");
    expect(statusValues).not.toContain("Cancelled");
  });

  it("should display all material options in dropdown", () => {
    const mockHandlers = {
      onStatusChange: vi.fn(),
      onModelChange: vi.fn(),
      onMaterialChange: vi.fn(),
      onSortChange: vi.fn(),
      onRefresh: vi.fn(),
    };

    render(<TableFiltersBar {...mockHandlers} />);

    const materialSelect = screen.getByDisplayValue("All Materials") as HTMLSelectElement;
    const options = materialSelect.querySelectorAll("option");

    const materialValues = Array.from(options).map((o) => o.value);
    expect(materialValues).toContain("PLA");
    expect(materialValues).toContain("PETG");
    expect(materialValues).toContain("ABS");
  });

  it("should call onSortChange when sort changes", () => {
    const onSortChange = vi.fn();
    const mockHandlers = {
      onStatusChange: vi.fn(),
      onModelChange: vi.fn(),
      onMaterialChange: vi.fn(),
      onSortChange,
      onRefresh: vi.fn(),
    };

    render(<TableFiltersBar {...mockHandlers} />);

    const sortSelect = screen.getByLabelText("Sort queue jobs") as HTMLSelectElement;
    fireEvent.change(sortSelect, { target: { value: "deadline" } });

    expect(onSortChange).toHaveBeenCalledWith("deadline");
  });
});
