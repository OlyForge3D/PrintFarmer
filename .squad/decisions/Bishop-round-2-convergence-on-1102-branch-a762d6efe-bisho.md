### 2026-08-05T01-25-19: Round 2 convergence on #1102 branch (a762d6efe): Bishop changes APPROVE→REJECT. H-1 and H-2 UPHELD empirically; state-prefix-parser OVERTURNED.
**By:** Bishop
**What:** Round 2 convergence on #1102 branch (a762d6efe): Bishop changes APPROVE→REJECT. H-1 and H-2 UPHELD empirically; state-prefix-parser OVERTURNED.
**References:** #1102, #1112, #1105, #1127, Hicks, Vasquez
**Why:** Bishop revised verdict: REJECT (changed from round-1 APPROVE).

Personally verified:
- H-1 UPHELD: ExplorerView.tsx:239 "New Folder" (subtle, UNTOUCHED by diff) renders hover=bg-pf-bg-2 over a bg-pf-bg-2 parent (context menu div at line 232) = invisible hover. Pre-fix it rendered bg-pf-bg-1 (variant enabled:hover won) = visible. Genuine regression. Adjacent Delete Folder (line 250) WAS fixed to hover:bg-pf-bg-1, proving the author knew the remedy and missed the sibling. One-line fix.
- I2 = same defect class: EmailConfirmationBanner.tsx:57 resend button rest+hover both bg-pf-bg-1 (author changed hover:bg-pf-border -> hover:bg-pf-bg-1, actively creating a no-op hover). I under-rated this as Info in round 1; correct severity is Warning/must-fix, same as H-1.
- H-2 UPHELD (empirical): injected shadow-xs into link variant (Button.tsx:39); guard stayed GREEN 38/38. BASE_CONTRIBUTED=/^shadow-xs$/ strips shadow-xs for ALL variants incl link/ghost/unstyled which get no base shadow (Button.tsx:77; test comment lines 332-336 admits link is excluded). Falsifies commit Claim H. Reverted, tree clean.
- State-prefix-parser (Hicks 🟡) OVERTURNED (empirical): injected focus-visible:bg-pf-accent into toggle; guard went RED. First FORBIDDEN test's (?:^|\s|:) alternation is prefix-agnostic, catches palette paint under any prefix. Reverted, tree clean.
- Vasquez CutPlaneOverlay ruling CORRECT: broken by #1105, fixed by #1112 (verified 8d8e31fb1 adds hover:bg-pf-bg-1). "broke in #1112" is a BRIEF error, NOT author error (controls.css says "#1112 for the one that bit" = accurate). Do not penalize author.
- Vasquez "full visibility" = overclaim: Claim D browser harness is absent from tree (no untracked, no matching harness). Not hidden evidence.
- Citations #1127/#1123/#1102/#1105 all real + accurately referenced. No fabrication.
- Commit message "nine sites... each now declares its own hover" imprecise (GCodeViewer3D removes a bg; controls.css says "Six") — 🔵 Info, not blocking.

Must-fix before ship: (1) ExplorerView.tsx:239 hover:bg-pf-bg-2->bg-pf-bg-1; (2) EmailConfirmationBanner.tsx:57 restore distinct hover; (3) Button.test.tsx:337 make BASE_CONTRIBUTED per-variant (only strip base shadow where applyShadow is true).