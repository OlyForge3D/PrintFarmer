import { render, screen } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import { TableFiltersBar } from "../QueueFiltersBar";

// Regression coverage for issue #1754: the print queue control row (status,
// material, model-filter selects and the refresh/clear actions) collapsed to
// unusable widths and clipped off-screen at 375x667. jsdom does not compute
// real flex layout, so these assertions pin the Tailwind classes that make
// the row wrap onto multiple readable rows on narrow viewports (flex-wrap +
// full-width controls that only take a fixed width at the `sm` breakpoint)
// instead of shrinking every control to fit a single unbroken row.
describe("TableFiltersBar mobile control layout (issue #1754)", () => {
  const mockHandlers = {
    onStatusChange: vi.fn(),
    onModelChange: vi.fn(),
    onMaterialChange: vi.fn(),
    onSortChange: vi.fn(),
    onRefresh: vi.fn(),
  };

  it("wraps the control row instead of forcing every control onto a single non-wrapping line", () => {
    const { container } = render(<TableFiltersBar {...mockHandlers} />);

    const row = container.firstElementChild;
    expect(row).toHaveClass("flex-wrap");
  });

  it("gives every filter control a full-width mobile size that only becomes fixed-width at the sm breakpoint", () => {
    render(<TableFiltersBar {...mockHandlers} />);

    // Select renders its own "relative w-full" wrapper div around the
    // <select>, so the sizing wrapper is one level further up than it is
    // for the plain <input> model filter.
    const statusSelect = screen.getByLabelText("Filter by status");
    const materialSelect = screen.getByLabelText("Filter by material");
    const sortSelect = screen.getByLabelText("Sort queue jobs");
    const modelInput = screen.getByLabelText("Filter by printer model");

    for (const control of [statusSelect, materialSelect, sortSelect]) {
      const wrapper = control.closest("div")?.parentElement;
      expect(wrapper).toHaveClass("w-full");
      expect(wrapper?.className).toMatch(/\bsm:w-(36|40)\b/);
      // shrink-0 stops the flex row from crushing the control below its
      // intended width instead of wrapping it onto its own row.
      expect(wrapper).toHaveClass("shrink-0");
    }

    const modelWrapper = modelInput.closest("div");
    expect(modelWrapper).toHaveClass("w-full");
    expect(modelWrapper?.className).toMatch(/\bsm:w-(36|40)\b/);
    expect(modelWrapper).toHaveClass("shrink-0");
  });

  it("keeps the refresh/clear actions reachable (not clipped) on narrow rows", () => {
    render(<TableFiltersBar {...mockHandlers} />);

    const refreshButton = screen.getByTitle("Refresh data");
    expect(refreshButton).toBeVisible();

    const actionGroup = refreshButton.closest("div");
    expect(actionGroup).toHaveClass("shrink-0");
  });
});
