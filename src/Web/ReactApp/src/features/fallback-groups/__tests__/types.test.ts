import { describe, it, expect } from "vitest";
import type { ToolheadCoverage } from "@/features/filament-coverage/types";
import {
  buildCoverageLookup,
  decodeFallbackGroupsUpdatedEvent,
  decodeFilamentFallbackGroup,
  decodeFilamentFallbackGroups,
  deriveFallbackGroupChainState,
  validateFallbackGroupDraft,
  type FilamentFallbackGroup,
  type FilamentFallbackGroupMember,
} from "../types";

function member(overrides: Partial<FilamentFallbackGroupMember> = {}): FilamentFallbackGroupMember {
  return {
    id: overrides.id ?? "m",
    toolheadId: overrides.toolheadId ?? "t",
    position: overrides.position ?? 0,
    toolheadName: overrides.toolheadName ?? null,
    toolheadIndex: overrides.toolheadIndex ?? 0,
    currentMaterial: overrides.currentMaterial ?? null,
    currentSpoolId: overrides.currentSpoolId ?? null,
    materialMatches: overrides.materialMatches ?? false,
  };
}

function group(members: FilamentFallbackGroupMember[], overrides: Partial<FilamentFallbackGroup> = {}): FilamentFallbackGroup {
  return {
    id: overrides.id ?? "g1",
    printerId: overrides.printerId ?? "p1",
    name: overrides.name ?? "PLA",
    materialType: overrides.materialType ?? "PLA",
    displayOrder: overrides.displayOrder ?? 0,
    createdAt: overrides.createdAt ?? "2025-01-01T00:00:00Z",
    updatedAt: overrides.updatedAt ?? "2025-01-01T00:00:00Z",
    members,
  };
}

describe("decodeFilamentFallbackGroup", () => {
  it("decodes canonical camelCase payloads and sorts members by position", () => {
    const decoded = decodeFilamentFallbackGroup({
      id: "g",
      printerId: "p",
      name: "PLA fallback",
      materialType: "PLA",
      displayOrder: 2,
      createdAt: "2025-01-01T00:00:00Z",
      updatedAt: "2025-01-02T00:00:00Z",
      members: [
        {
          id: "m2",
          toolheadId: "t2",
          position: 1,
          toolheadName: "Right",
          toolheadIndex: 1,
          currentMaterial: "PLA",
          currentSpoolId: 55,
          materialMatches: true,
        },
        {
          id: "m1",
          toolheadId: "t1",
          position: 0,
          toolheadName: null,
          toolheadIndex: 0,
          currentMaterial: null,
          currentSpoolId: null,
          materialMatches: false,
        },
      ],
    });
    expect(decoded.name).toBe("PLA fallback");
    expect(decoded.displayOrder).toBe(2);
    expect(decoded.members[0].id).toBe("m1");
    expect(decoded.members[1].id).toBe("m2");
  });

  it("returns safe defaults for missing / malformed fields", () => {
    const decoded = decodeFilamentFallbackGroup({});
    expect(decoded.id).toBe("");
    expect(decoded.name).toBe("");
    expect(decoded.members).toEqual([]);
    expect(decoded.displayOrder).toBe(0);
  });

  it("decodes lists sorted by displayOrder", () => {
    const decoded = decodeFilamentFallbackGroups([
      { id: "b", displayOrder: 2, members: [] },
      { id: "a", displayOrder: 1, members: [] },
    ]);
    expect(decoded.map((g) => g.id)).toEqual(["a", "b"]);
  });

  it("decodes an event payload", () => {
    expect(decodeFallbackGroupsUpdatedEvent({ printerId: "p" })).toEqual({ printerId: "p" });
    expect(decodeFallbackGroupsUpdatedEvent({})).toEqual({ printerId: null });
  });
});

describe("deriveFallbackGroupChainState", () => {
  it("marks the first loaded matching-material member active, subsequent ones backup", () => {
    const g = group([
      member({ id: "a", position: 0, toolheadIndex: 0, currentSpoolId: 1, currentMaterial: "PLA", materialMatches: true }),
      member({ id: "b", position: 1, toolheadIndex: 1, currentSpoolId: 2, currentMaterial: "PLA", materialMatches: true }),
    ]);
    const chain = deriveFallbackGroupChainState(g);
    expect(chain.members.map((m) => m.state)).toEqual(["active", "backup"]);
    expect(chain.mixedMaterialWarning).toBe(false);
  });

  it("marks a member with no spool as empty", () => {
    const g = group([
      member({ id: "a", currentSpoolId: null }),
      member({ id: "b", position: 1, toolheadIndex: 1, currentSpoolId: 2, currentMaterial: "PLA", materialMatches: true }),
    ]);
    const chain = deriveFallbackGroupChainState(g);
    expect(chain.members[0].state).toBe("empty");
    expect(chain.members[1].state).toBe("active");
  });

  it("marks a runout member as exhausted regardless of material", () => {
    const g = group([
      member({ id: "a", currentSpoolId: 1, currentMaterial: "PLA", materialMatches: true, toolheadIndex: 0 }),
      member({ id: "b", position: 1, toolheadIndex: 1, currentSpoolId: 2, currentMaterial: "PLA", materialMatches: true }),
    ]);
    const coverage: ToolheadCoverage[] = [
      {
        toolheadIndex: 0,
        toolheadName: "Left",
        spoolId: 1,
        material: "PLA",
        filamentColor: null,
        remainingGrams: 0,
        currentJobRequiredGrams: null,
        currentJobRemainingGrams: null,
        queuedRequiredGrams: null,
        totalDemandGrams: null,
        status: "runout",
        statusReason: null,
        predictedRunoutAt: null,
        predictedRunoutLayer: null,
      },
    ];
    const chain = deriveFallbackGroupChainState(g, buildCoverageLookup(coverage));
    expect(chain.members[0].state).toBe("exhausted");
    // The next matching-material member takes over as active.
    expect(chain.members[1].state).toBe("active");
  });

  it("flags a mismatched material and raises mixedMaterialWarning", () => {
    const g = group([
      member({ id: "a", currentSpoolId: 1, currentMaterial: "PETG", materialMatches: false }),
    ]);
    const chain = deriveFallbackGroupChainState(g);
    expect(chain.members[0].state).toBe("mismatch");
    expect(chain.mixedMaterialWarning).toBe(true);
  });

  it("uses case-insensitive comparison when materialMatches is false but material equals the target", () => {
    const g = group([
      member({ id: "a", currentSpoolId: 1, currentMaterial: "pla", materialMatches: false }),
    ]);
    const chain = deriveFallbackGroupChainState(g, undefined);
    // The client-side fallback comparison should still consider this active.
    expect(chain.members[0].state).toBe("active");
  });
});

describe("validateFallbackGroupDraft", () => {
  const physical = new Set(["t1", "t2", "t3"]);

  it("rejects blank name / material / empty toolheads", () => {
    const errs = validateFallbackGroupDraft(
      { name: "  ", materialType: "  ", toolheadIds: [] },
      [],
      physical,
    );
    expect(errs.map((e) => e.field).sort()).toEqual(["materialType", "name", "toolheadIds"]);
  });

  it("rejects duplicate names case-insensitively but ignores the group being edited", () => {
    const existing: FilamentFallbackGroup[] = [
      { id: "g1", printerId: "p", name: "PLA lineup", materialType: "PLA", displayOrder: 0, createdAt: "", updatedAt: "", members: [] },
    ];
    expect(
      validateFallbackGroupDraft(
        { name: "pla lineup", materialType: "PLA", toolheadIds: ["t1"] },
        existing,
        physical,
      ).some((e) => e.field === "name"),
    ).toBe(true);
    expect(
      validateFallbackGroupDraft(
        { name: "PLA lineup", materialType: "PLA", toolheadIds: ["t1"] },
        existing,
        physical,
        "g1",
      ).some((e) => e.field === "name"),
    ).toBe(false);
  });

  it("rejects unknown / MMU toolheads and duplicates", () => {
    expect(
      validateFallbackGroupDraft(
        { name: "x", materialType: "PLA", toolheadIds: ["not-in-set"] },
        [],
        physical,
      ).some((e) => e.field === "toolheadIds"),
    ).toBe(true);

    expect(
      validateFallbackGroupDraft(
        { name: "x", materialType: "PLA", toolheadIds: ["t1", "t1"] },
        [],
        physical,
      ).some((e) => e.field === "toolheadIds"),
    ).toBe(true);
  });

  it("returns no errors for a valid draft", () => {
    expect(
      validateFallbackGroupDraft(
        { name: "PLA lineup", materialType: "PLA", toolheadIds: ["t1", "t2"] },
        [],
        physical,
      ),
    ).toEqual([]);
  });
});
