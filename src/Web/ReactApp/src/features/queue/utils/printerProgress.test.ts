import { describe, it, expect } from "vitest";
import { mergePrinterProgress, mergePrinterThumbnail } from "./printerProgress";

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

describe("mergePrinterThumbnail", () => {
  it("caches a printer-side thumbnail while the printer is actively printing", () => {
    const next = mergePrinterThumbnail({}, "printer-1", "http://printer/thumb.png", true);
    expect(next).toEqual({ "printer-1": "http://printer/thumb.png" });
  });

  it("returns the same reference when the thumbnail is unchanged", () => {
    const prev = { "printer-1": "http://printer/thumb.png" };
    const next = mergePrinterThumbnail(prev, "printer-1", "http://printer/thumb.png", true);
    expect(next).toBe(prev);
  });

  it("preserves the previous thumbnail when an active update omits it", () => {
    const prev = { "printer-1": "http://printer/thumb.png" };
    const next = mergePrinterThumbnail(prev, "printer-1", undefined, true);
    expect(next).toBe(prev);
    expect(next["printer-1"]).toBe("http://printer/thumb.png");
  });

  it("clears the cached thumbnail when the printer goes idle/finished", () => {
    const prev = { "printer-1": "http://printer/thumb.png", "printer-2": "http://p2/t.png" };
    const next = mergePrinterThumbnail(prev, "printer-1", undefined, false);
    expect(next).toEqual({ "printer-2": "http://p2/t.png" });
    expect("printer-1" in next).toBe(false);
  });

  it("does not let a stale thumbnail leak across consecutive jobs on the same printer", () => {
    let state: Record<string, string> = { "printer-1": "http://printer/jobA.png" };
    state = mergePrinterThumbnail(state, "printer-1", undefined, false); // idle between jobs
    expect(state["printer-1"]).toBeUndefined();
    state = mergePrinterThumbnail(state, "printer-1", "http://printer/jobB.png", true);
    expect(state["printer-1"]).toBe("http://printer/jobB.png");
  });

  it("is a no-op when clearing an absent printer", () => {
    const prev = { "printer-2": "http://p2/t.png" };
    const next = mergePrinterThumbnail(prev, "printer-1", undefined, false);
    expect(next).toBe(prev);
  });
});
