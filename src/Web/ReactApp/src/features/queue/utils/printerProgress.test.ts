import { describe, it, expect } from "vitest";
import { mergePrinterProgress } from "./printerProgress";

describe("mergePrinterProgress", () => {
  it("sets a numeric progress value for a printer", () => {
    const next = mergePrinterProgress({}, "printer-1", 42);
    expect(next).toEqual({ "printer-1": 42 });
  });

  it("returns the same reference when the value is unchanged", () => {
    const prev = { "printer-1": 42 };
    const next = mergePrinterProgress(prev, "printer-1", 42);
    expect(next).toBe(prev);
  });

  it("updates only the targeted printer, preserving others", () => {
    const prev = { "printer-1": 10, "printer-2": 90 };
    const next = mergePrinterProgress(prev, "printer-1", 55);
    expect(next).toEqual({ "printer-1": 55, "printer-2": 90 });
  });

  it("clears a cached entry when progress becomes non-numeric (idle/finished)", () => {
    const prev = { "printer-1": 80, "printer-2": 30 };
    const next = mergePrinterProgress(prev, "printer-1", undefined);
    expect(next).toEqual({ "printer-2": 30 });
    expect("printer-1" in next).toBe(false);
  });

  it("does not let a stale value leak across consecutive jobs on the same printer", () => {
    // Job A finishes at 80%, printer reports idle (null) → cache cleared,
    // so Job B starting on the same printer cannot inherit 80%.
    let state: Record<string, number> = { "printer-1": 80 };
    state = mergePrinterProgress(state, "printer-1", undefined); // idle between jobs
    expect(state["printer-1"]).toBeUndefined();
    state = mergePrinterProgress(state, "printer-1", 5); // Job B first update
    expect(state["printer-1"]).toBe(5);
  });

  it("is a no-op when clearing an absent printer", () => {
    const prev = { "printer-2": 30 };
    const next = mergePrinterProgress(prev, "printer-1", undefined);
    expect(next).toBe(prev);
  });
});
