### 2026-08-05T08-33-40: #1102 guard was blind: built-stylesheet checks cannot see a paint utility re-added to the variant map
**By:** jpapiez-1102
**What:** #1102 guard was blind: built-stylesheet checks cannot see a paint utility re-added to the variant map
**References:** #1102, #1122, #1130, dev/jpapiez/1102-reconciled, c7f7e574f
**Why:** Verified by injection on branch dev/jpapiez/1102-reconciled.

Re-adding `bg-transparent` to the `subtle` entry of Button.tsx's variantClasses left ALL FOUR existing assertions in ghostButtonBuiltStylesheet.test.ts green, while the built artifact placed `.bg-transparent{` at byte 92813 and `.bg-pf-accent{` at byte 73022 - the transparent rule sorting 19,791 bytes later and therefore winning. That is #1102 fully restored under a passing gate.

Cause: the built-stylesheet assertions verify component-layer rule PLACEMENT (that defaults sit inside @layer components). A paint utility re-added to the variant map lands in @layer utilities and leaves every component-layer rule untouched, so placement checks stay green.

Fix (commit c7f7e574f): source-level contract asserting ghost/subtle/tab/toggle/link declare no bg-* or shadow-* utility in any state, prefix-aware so enabled:hover:bg-* is caught. Falsified both directions - injection turns it red naming the variant and offending utility; restore turns it green.

Note origin/development currently carries NO #1102 guard at all (test/styles/ holds only bareButtonSkin.test.ts and selectedRowHover.test.tsx, neither referencing data-pf-variant). This makes #1122 an open hole rather than merely under-covered.