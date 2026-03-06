# G-Code Printer Specificity: Auto-Dispatch Constraint & Revised Strategy

**Author:** Dallas (Lead/Architect)
**Date:** 2026-03-07
**Context:** Jeff raised critical feedback on auto-dispatch scoring approach
**Status:** REQUIRES TEAM DECISION on approach (see Recommendation below)

---

## The Constraint: Why G-Code Isn't Portable

G-code is **NOT** a generic instruction set. It is **printer-specific firmware and hardware configuration baked in at slice time**.

### What Gets Baked In
- **Printer firmware flavor:** Klipper, Marlin, PrusaFirmware, Smoothieware — each has different acceleration/jerk curves, compensation math
- **Hardware parameters:** Nozzle diameter (affects line width, flow), bed size (part positioning), hotend/extruder (temperature limits, retraction speed)
- **Acceleration & jerk curves:** Different firmware/hardware combos have wildly different optimal acceleration settings (e.g., 3000 mm/s² Klipper vs 1000 mm/s² Marlin)
- **Retraction & cooling:** Firmware-specific behavior; wrong retraction settings cause stringing or under-extrusion
- **Start/end sequences:** Custom bed leveling, nozzle priming, park positions — all printer-specific
- **Firmware features:** Variable layer height, pressure advance (Klipper), input shaper — available on some hardware, not others
- **Filament flow assumptions:** Slicer assumes specific volumetric flow rates based on hardware; wrong hardware = under/over-extrusion

### The Risk
Running G-code sliced for Printer A on Printer B, even if they're the "same model," is unpredictable:
- **Different firmware versions on "identical" hardware** = acceleration curves diverge
- **Different hotend/nozzle combos** = filament flow assumptions broken
- **Different bed leveling offsets/hardware wear** = Z-height miscalibration
- **Firmware customizations** = farmers patch their firmware (acceleration tuning, bed probing scripts, etc.)

**Result:** Print fails mid-print, part quality degrades, or physical damage (nozzle crashes, bed adhesion loss).

---

## What My Previous Plan Got Wrong

In my original auto-dispatch plan, Factor 6 ("Printer Model Match") treated G-code as somewhat portable:

```
6. Printer Model Match (weight: 60)
   - GcodeFile sliced for this exact PrinterModel → 100
   - GcodeFile sliced for same manufacturer → 50
   - No model data in gcode → 70 (neutral)
   - Different manufacturer → 30
```

**This is WRONG.** The presence of a GcodeFile row with `PrinterModelId` doesn't guarantee it's safe to run elsewhere. Even if two printers are "the same model," they may have:
- Different firmware versions
- Different nozzles installed
- Different hardware modifications
- Different slicer profiles used

**The correct approach:** ONLY dispatch a G-code file to:
1. The exact printer it was sliced for, OR
2. A different printer IF re-sliced for that target printer, OR
3. A printer in a **user-curated group** of truly interchangeable hardware (same model, firmware, config)

---

## Revised Dispatch Strategy: Three Approaches

### Approach A: Printer Profile Matching (Conservative, Safe)

**How it works:**
- Each `Printer` gets a **PrinterProfile** — captures firmware version, nozzle model, hotend config, firmware customizations
- `GcodeFile` stores not just `PrinterModelId`, but also `PrinterProfileId` (the exact profile it was sliced for)
- Dispatch scorer: **Only consider printers whose PrinterProfile matches the GcodeFile's target profile exactly**
- Eliminates risk entirely — no cross-printer guessing

**Pros:**
- ✅ Zero compatibility risk
- ✅ Safe for farms with diverse hardware even within same model
- ✅ Works with current slicer architecture (don't need re-slicing)
- ✅ Easy to implement: add PrinterProfileId FK to GcodeFile

**Cons:**
- ❌ Dispatch candidates shrink to 0-1 printers per job (only original printer usually available)
- ❌ Farm can't leverage printer redundancy
- ❌ If Printer A is busy, job can't move to Printer B even if they're identical

**Best for:** Farms where printers are customized or firmware is patched frequently.

**Dispatcher #1 approach:**
```
Factor: GcodeFile.PrinterProfileId == Printer.PrinterProfileId
Result: Exact match → 100; Any mismatch → 0 (ELIMINATE)
```

---

### Approach B: Slice-on-Demand (Maximum Flexibility)

**How it works:**
- Job upload stores the **model file** (STL/3MF), not just G-code
- When dispatch scorer evaluates Printer B: **trigger a re-slice** of the model for Printer B's profile
- Store the new G-code in `GcodeFile` with new `PrinterProfileId`
- Dispatch to Printer B with fresh, compatible G-code

**Pros:**
- ✅ Can dispatch to ANY compatible printer (same nozzle, same firmware)
- ✅ G-code is always correct for target hardware
- ✅ Enables true load-balancing across interchangeable hardware
- ✅ Ideal for farm standardization (all Creality, all Prusa, etc.)

**Cons:**
- ❌ **Slicing takes 2-10 minutes** — introduces latency into dispatch
- ❌ Requires keeping original model file (not all jobs have it — historical jobs are G-code only)
- ❌ Slicer integration complexity: OrcaSlicer + PrusaSlicer workers must handle on-demand slicing
- ❌ Requires slicer profile caching (each printer's profile stored, accessible to workers)
- ❌ High computational cost if farms have 50+ printers and high job volume

**Best for:** Farms with standardized hardware (e.g., all Prusa i3 MK4, all Creality Ender 5 Pro) where slicing time is acceptable.

**Dispatcher #2 approach:**
```
1. Query compatible printers (same backend, nozzle support, build volume fit)
2. For each candidate, trigger async slice job via SliceJobService
3. Poll for completed slice; once done, dispatch G-code to that printer
4. If slice fails, exclude that candidate
```

**Implementation note:** PrintFarmer already has `OrcaSlicer` and `PrusaSlicer` worker services. Extending them to support on-demand slicing is feasible but adds complexity.

---

### Approach C: Printer Groups / Classes (User-Curated, Pragmatic)

**How it works:**
- Users define **PrinterGroup** entities: "My Prusa Farm (5x i3 MK4s)" or "Creality Fleet A"
- Each group captures: shared firmware version, nozzle config, slicer profile constraints
- When uploading a job, user selects the **PrinterGroup** it's sliced for
- `GcodeFile` stores `PrinterGroupId` instead of single `PrinterId`
- Dispatch scorer: **Only consider printers within the same PrinterGroup**
- Within a group, dispatch is safe because user asserts they're configured identically

**Pros:**
- ✅ Safe dispatch across multiple printers (no re-slicing latency)
- ✅ Practical for most farms (farmers group printers this way already)
- ✅ No slicer integration complexity
- ✅ Users have explicit control (not automated magic)
- ✅ Easy migration: start conservative (each printer = own group), then merge as farmer gains confidence

**Cons:**
- ❌ Puts responsibility on user to ensure group members are truly compatible
- ❌ If user is wrong, silent failures (bad prints, potential damage)
- ❌ Dispatch candidates limited to printers in same group (no true fleet-wide load balancing)
- ❌ Maintenance burden: if farmer swaps a nozzle on one printer, they must update the group config

**Best for:** Most farms. Gives farmers control, avoids magic, works with current slicer setup.

**Dispatcher #3 approach:**
```
Factor: GcodeFile.PrinterGroupId == Printer.PrinterGroupId
Result: Exact group match → 100; Different group → 0 (ELIMINATE)
```

**UI/UX:**
- Job upload form: "Which printer group is this sliced for?" dropdown
- Printer management: "Create Printer Groups" section (e.g., "Prusa Fleet", "Creality Ender 5", "Test Bench")
- Printer edit: Assign printer to group(s)

---

### Approach D: Hybrid (Recommended)

**Combine B + C:**
1. **Safe by default:** Dispatch only within PrinterGroup (Approach C)
2. **Power user option:** Enable "Slice for different group" — triggers on-demand slice if user/admin opts in
3. **Fallback:** If slice fails, offer re-queue on original group

**Pros:**
- ✅ Safe for non-experts (group matching is the default)
- ✅ Power users and large farms can leverage slice-on-demand for fleet-wide load balancing
- ✅ Graceful degradation (slice failure doesn't break dispatch)
- ✅ Leverages existing slicer infrastructure as an optional enhancement

**Cons:**
- ⚠️ More complex implementation (both approaches in one feature)
- ⚠️ Requires user education (when to use each mode)

---

## Recommendation: Implement Approach C (Printer Groups), Plan for D

**Immediate (Sprint 1-2):**
- Implement **Printer Groups** (Approach C)
- Update `GcodeFile` schema: add `PrinterGroupId` FK (drop single `PrinterId` assumption)
- Update dispatch scorer: **ELIMINATE** (score 0) any printer NOT in the file's PrinterGroup
- Update job upload UX: "Which printer group is this sliced for?" dropdown
- Update printer management: "Create & manage printer groups" page

**Why this first:**
- ✅ Safest. Puts power in user's hands.
- ✅ No slicer latency (no on-demand slicing yet)
- ✅ Unblocks auto-dispatch feature for immediate value
- ✅ Farmers already think in groups (they say "my Prusa farm" or "test bench")

**Future (Sprint 5+):**
- Optional: Add on-demand slice feature (Approach B) for farms that want cross-group dispatch
- Integrate with existing OrcaSlicer/PrusaSlicer workers
- Add admin toggle: "Allow cross-group slicing?"

---

## Updated Dispatch Scoring Algorithm

### Factor 6 (NEW): PrinterGroup Compatibility

**Remove old "Printer Model Match" logic.** Replace with:

```
6. PrinterGroup Compatibility (weight: HARD/ELIMINATE)
   - GcodeFile.PrinterGroupId == Printer.PrinterGroupId → 100
   - GcodeFile.PrinterGroupId != Printer.PrinterGroupId → 0 (ELIMINATE)
```

### Revised Factor Weights (Post-Fix)

```
Factor 1: Material Match (HARD, eliminate on mismatch) → weight 100
Factor 2: Nozzle Diameter Match (HARD, eliminate on mismatch) → weight 100
Factor 3: Build Volume Fit (HARD, eliminate on oversize) → weight 50
Factor 4: Enclosure Requirement (HARD if needed, eliminate on missing) → weight 80
Factor 5: Nozzle Hardness (HARD if abrasive, eliminate on mismatch) → weight 80
Factor 6: PrinterGroup Compatibility (HARD, eliminate on mismatch) → [NEW]
Factor 7: Queue Depth (soft, reward idle printers) → weight 30
Factor 8: Preferred Printer (soft, reward user preference) → weight 40
Factor 9: Printer Availability (pre-filter, not scored) → [pre-filter]
```

---

## Database Schema Update

**Drop assumption from old plan:**
```sql
-- OLD (REMOVE):
-- GcodeFile has PrinterModelId (non-exclusive, misleading)

-- NEW (ADD):
ALTER TABLE GcodeFiles ADD COLUMN PrinterGroupId TEXT;
ALTER TABLE GcodeFiles ADD FOREIGN KEY (PrinterGroupId) 
  REFERENCES PrinterGroups(Id) ON DELETE CASCADE;
CREATE INDEX IX_GcodeFiles_PrinterGroupId ON GcodeFiles(PrinterGroupId);
```

**New table:**
```sql
CREATE TABLE PrinterGroups (
    Id TEXT PRIMARY KEY,
    FarmId TEXT NOT NULL REFERENCES Farms(Id) ON DELETE CASCADE,
    Name TEXT NOT NULL,
    Description TEXT,
    FirmwareVersion TEXT,  -- e.g., "Klipper 0.11", "Marlin 2.1.2"
    NozzleModelId TEXT,    -- all printers in group use same nozzle
    SharedNotes TEXT,      -- user-facing notes: "All have 50° bed leveling mod, etc."
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
CREATE INDEX IX_PrinterGroups_FarmId ON PrinterGroups(FarmId);

CREATE TABLE PrinterGroupMembers (
    PrinterId TEXT PRIMARY KEY REFERENCES Printers(Id) ON DELETE CASCADE,
    PrinterGroupId TEXT NOT NULL REFERENCES PrinterGroups(Id) ON DELETE CASCADE
);
CREATE INDEX IX_PrinterGroupMembers_GroupId ON PrinterGroupMembers(PrinterGroupId);
```

---

## Impact on Feature Plan

### Phase 1 (MVP: Scored Suggestions) — UNCHANGED
- Still works: Find Best Printer, score and rank candidates
- Now filters by PrinterGroup compatibility first
- Eliminates "wrong group" printers from candidates

### Phase 2 (Auto-Dispatch on Idle) — UPDATED
- When printer idles, dispatch only jobs with matching PrinterGroup
- If no jobs in group → printer stays idle (don't cross groups without explicit user action)
- This is safe and prevents accidents

### Phase 3 (Future: Cross-Group Slicing) — NEW OPTIONAL
- Admin enables "Allow on-demand slicing?"
- When no jobs in printer's group, offer to slice a job from a different group
- If user approves, trigger slice job and dispatch result

---

## Testing Impact

### New Unit Tests (DispatchScorer)
```
ScorePrinter_SameGroup_Returns100
ScorePrinter_DifferentGroup_Eliminates
ScorePrinter_NoGroupAssigned_Eliminates
ScorePrinter_GroupNull_Eliminates
```

### New Integration Tests
```
FindCandidates_FiltersOutDifferentGroups
FindCandidates_OnlyReturnsPrintersInSameGroup
DispatchJob_CrossGroupJob_Returns400BadRequest
```

### Job Upload Tests
```
UploadJob_RequiresGroupSelection_Returns400IfMissing
UploadJob_ValidatesGroupExistence
```

---

## Documentation & User Education

**In-app Help (new):**
- "What are printer groups?" — Short explainer
- "How do I create a printer group?" — Step-by-step
- "Why can't I dispatch this job to that printer?" — Troubleshooting (group mismatch, material, etc.)

**Admin Guide (new):**
- "Printer groups best practices"
- "How to audit printer group compatibility"
- "When to enable cross-group slicing"

---

## Risk Assessment

| Approach | Risk | Mitigation |
|----------|------|-----------|
| Group misconfig by user | Silent failures, bad prints | UI shows "Printer Group: [name]" on upload; confirmation dialog; audit log |
| Printer not in any group | No dispatch candidates | Force group assignment at printer creation; validation on upload |
| User forgets to update group after hardware change | G-code now incompatible | Group edit form shows "Last updated: X days ago"; prompt user on printer maintenance state change |

---

## Decision Gate for Team

**Question for Jeff & team:**

1. **Approach C (Printer Groups) - Approve for Phase 1?**
   - Safe, practical, no latency, puts farmer in control
   - Recommended: YES

2. **Plan Approach D (Slice-on-Demand) for future?**
   - Complex, adds slicer latency, optional power-user feature
   - Recommended: YES (plan but don't build immediately)

3. **Any other constraint I'm missing?**
   - Existing job uploads with `PrinterModelId` only — how do we backfill GroupId?
   - Answer: Migration script assigns legacy jobs to a default group per printer model (farmer can consolidate later)

---

## Next Steps

Once approved:
1. **Lambert (Backend):** Implement PrinterGroup entities, update dispatch scorer, add upload endpoints
2. **Ripley (Frontend):** Create PrinterGroup management page, update job upload UX
3. **Kane (Testing):** New test suite for group-based dispatch

---

**Approval:** Dallas ✓ (ready for team review)
