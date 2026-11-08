# Ceremonies

## UI feature shaping

Before UI feature implementation, the coordinator automatically runs
Impeccable `shape` using the appropriate product and design authorities and
the issue's brief. Follow the pre-build workflow in
[`ralph-instructions.md`](ralph-instructions.md#ui-work-shape-and-design-review)
and the canonical
[design policy](../.github/copilot-instructions.md#risk-based-review-scope).
A precise brief may supply intent; mark assumptions rather than fabricate
confirmation. Material unresolved choices require needs-input before code.
Fix-only refinements preserve incumbent identity and do not require an
unrelated brand interview.

## UI design review

Before opening a rendered-UI PR, the author supplies local desktop/mobile
browser screenshots and available native simulator captures under the canonical
[UI visual evidence](../.github/copilot-instructions.md#ui-visual-evidence)
requirement. This applies to drafts too. Policy/tooling-only changes record an
explicit not-applicable reason rather than fabricated visual evidence.

For each `squad`-labelled PR, use the shared
`scripts/design/ui-paths.mjs` classifier against the PR's changed paths. When
it identifies UI changes, the coordinator automatically dispatches
`design-reviewer` with `task` after implementation and before ready/merge.
Use the registered `design-reviewer` custom agent when available. If it is not
loaded in the current runtime, dispatch `general-purpose` via `task` with the
complete read-only charter at `.squad/agents/design-reviewer/charter.md` and
the same review packet; never substitute `create_session`.
If the classifier or path evidence is unavailable, block rather than assume
there is no UI. This ceremony does not expand the gate to non-`squad` PRs,
alter fork or authenticated owner-override rules, or replace the risk-based
Bishop/Hicks/Vasquez review quorum. The canonical design requirement and
quorum are defined in
[Risk-Based Review Scope](../.github/copilot-instructions.md#risk-based-review-scope);
this ceremony implements that policy, not a separate gate.

The dispatch packet includes the exact current head SHA, comparison baseline,
all changed UI paths and platforms, the applicable `PRODUCT.md` and `DESIGN.md`
authorities, author-supplied test and scanner evidence (or explicit absence),
the PR's visual evidence with source SHA, screen, viewport/device, appearance,
state and unavailable-platform limitations,
and all prior stable finding IDs with their disposition and closure criteria.
Web uses the root authorities; native iOS uses `mobile/PRODUCT.md` and
`mobile/DESIGN.md`. For mixed changes provide both. Recheck the head before
recording a verdict; a changed head requires fresh exact-head design review,
with no carry-forward across sync. Include both old and new names for renames;
incomplete changed-path or classification evidence blocks the determination.

The reviewer uses the Impeccable critique and platform-appropriate audit
rubrics against source and the actual evidence supplied. It is a read-only
`task`, not a session: no edits, tracking files, builds, tests, simulators,
linters or commits. It does not self-approve authored work. Label the review
**source-only**, identify missing runtime/visual evidence, and never claim
browser, simulator, accessibility, performance or device verification that
did not occur. A source-only adaptation of Impeccable critique is degraded:
say so explicitly, including the absent independent assessment and visual
evidence; do not claim a full dual-agent critique. The deterministic scanner
is supplementary and cannot substitute for judgment or verify native behavior.
Assess supplied screenshots against the source and documented UI conventions;
missing required captures or visible unresolved defects block approval.

Report actionable findings with stable IDs, severity, location, user impact
and closure criteria; include positive evidence and preserve unresolved
findings on follow-up. Follow the repository's delta-only review and checkpoint
rules for subsequent heads. A missing required source, baseline, prior finding
or credible evidence needed for a conclusion is a blocker, not an approval.
The coordinator records the reviewer's decision on the PR in the canonical
format, with the exact reviewed head SHA:

```text
<!-- squad-verdict -->
Squad-Reviewer: design-reviewer
Squad-Verdict: APPROVE
Squad-Head-SHA: <full-reviewed-head-sha>
```

For a rejection, use `REQUEST_CHANGES` in place of `APPROVE`.
Use `APPROVE` only when the assigned source review is complete and there are no
blocking findings; otherwise use `REQUEST_CHANGES` or report a blocker without
inventing a verdict. A clean source review is self-attestation, not a claim of
runtime validation or independent human approval.
