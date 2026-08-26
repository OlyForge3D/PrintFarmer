# Dallas History


## Core Context

Dallas is the project lead & product architect. Key contributions:
- Feature prioritization & architecture oversight
- Location hierarchy system design (phase 1 approved)
- Auto-dispatch phase 1 & 2 architecture
- Competitive analysis & market differentiation
- Team coordination & decision governance
- Failure detection & UI polish sessions (2026-03-25)
- Auto-dispatch naming cleanup & consistency (2026-03-25)

Early entries (pre-2026-03-25) summarized for maintainability. See decisions-archive.md for historical context.

---

## Learnings — 2026-07-13 Hicks v5 Independent Remediation of PR #750 / issue #708

**Scope:** Reviewer Rejection Protocol reassigned the v5 revision of the F3 native push backend to Dallas. Landed one descendant commit `45474b8b9` on top of `4227f5141` (v5 baseline) that fixes both Hicks blockers.

### Blockers resolved

- **H1-v5 (High) — global push opt-out bypass.** `NativePushDispatcher` was only consulting per-attention-kind push toggles, so a preserved `PushOnPrinterFailure=true` could sneak past a user who had set `EnablePushNotifications=false`. Added a master gate that increments `SkippedCategoryOptOut` and skips dispatch when the persisted row exists AND its master push flag is false. A missing row still falls back to the CLR default (true) so pre-#708 opt-in behaviour is preserved for users who never touched the preference UI.
- **H1-v5 (High) — write projection.** The controller was OR-ing only the four legacy job rows into `EnablePushNotifications`, which silently reset the master flag to `false` when a user disabled every job row even though attention rows were still active. Relocated derivation to `NotificationService.ApplyMasterFlagsFromMatrix` (OR of all nine event×channel rows) and made it run inside the same tracked read/write as the row itself. Result is mirrored back to the caller DTO via `MirrorAttentionAndMasterFlags` so the controller response body reflects reality.
- **H2-v5 (Medium) — stale legacy snapshot race.** Controller was doing an `AsNoTracking` pre-read then handing the transient DTO to the service, which did its own tracked read/write. A concurrent newer-client attention update between the two reads could be overwritten by the legacy PUT's stale snapshot. Moved attention-row preservation into `NotificationService.UpdatePreferencesAsync` behind a new `preserveAttentionFields` parameter; controller now signals whether the incoming matrix addressed any attention row and stops touching the persisted row up-front.

### Key discoveries

- The interface signature change is safe because the only non-controller caller (`NotificationServiceDeliveryTests.cs:519`) doesn't set the parameter and receives the default `false`, preserving pre-fix semantics for modern requests.
- Master-flag derivation is now the service's single source of truth. This is a small but real architectural shift — no other consumer computes those flags any more. Worth flagging in decisions.md if it comes up in future reviews.
- EF Core in-memory (`UseInMemoryDatabase(name)`) shares state across DbContext instances sharing the same name, which is exactly what the two-context concurrency regression test needs — no external server required.
- The two full-suite failures (`FilamentCoverageControllerTests.GetFleet_LargeFleet_CompletesWithinReasonableBudget` — 15s budget missed at 29.9s; `PrintersServiceSwapBindingTests.GuidedConcurrentFirstGateBinds…` — SQLite file locked by another process during teardown) are demonstrably environmental/pre-existing and unrelated to notification code paths.

### Gates

- `dotnet build ./farm-web.sln -c Debug` → 0 errors, 0 warnings
- Focused notification tests (NativePush | NotificationPreferences | NotificationService) → 139 / 139 pass
- Full `Farm.Web.Api.Tests` suite → 3282 pass, 2 pre-existing environmental failures unrelated to notifications
- `dotnet format --verify-no-changes` flagged a pre-existing CHARSET encoding issue on `NativePushDispatcherTests.cs` (confirmed present at baseline `4227f5141`); other four edited files are format-clean
- Push confirmed both anchors ancestors of remote head `45474b8b9`: `4227f5141` (exit 0) and `6ce67c89e` (exit 0)

### Coordinator notes

- PR #750 remains draft; trio review is coordinator's next step per protocol
- Contract untouched: capabilities endpoint, nine PascalCase enum tokens, camelCase DTO properties, unknown-token → 400, nine rows materialized with the expected attention defaults

---

## 2026-08-25 — Machine profile family cloning: design (no code)

**Task:** Produce the design for cloning an OrcaSlicer machine profile *family* (Jeff's "Voron 2.4 but smaller" Micron case). Inputs: Lambert's backend trace + inheritance addendum, Ripley's frontend trace, Brett's blocked live repro, `.github/skills/orcaslicer-profiles/SKILL.md`. Output: `.squad/decisions/inbox/dallas-profile-family-design.md`.

### Key discoveries (verified myself, not taken on report)

- **An Orca `machine_model` is a label, not an inheritance node.** `Voron/machine/Voron 2.4 250.json` is pure metadata — no `inherits`, no settings. Nozzle presets carry `printer_model` as a *pointer* and independently `inherits: fdm_klipper_common`. So "clone inherits from the model" is not expressible as an Orca edge. The fix is to honour the *shape* at the PrintFarmer level (family record holding shared overrides) and anchor children to the source **nozzle preset** instead.
- **The worker already hands us a fully flattened base.** `OrcaProfilesService` walks the whole `inherits` chain and `CachedOrcaProfilesService` persists *resolved* DTOs. So the "resolver" PrintFarmer needs is fetch-one-DTO + apply-two-override-bags + rewrite-six-identity-keys — **not** a chain walker. This materially de-risks the phase Lambert called the main cost. Always check whether an upstream layer has already done the expensive part before sizing a resolver.
- **`ResolveMachineSystemPresetName` already mirrors Orca's `CLI::run`**: `from == "system"` → `name`, else → `inherits`. That single existing function is what makes display identity and compatibility identity separable — stock Voron processes keep matching with zero cloning. The blocker is `WithSystemPresetInherits` unconditionally overwriting `inherits` with the profile's own name (`OrcaSlicingPipelineService.cs:848-861`).
- **Compatibility is by exact preset name.** `fdm_process_voron_common.json` has an empty condition + explicit `compatible_printers` array. Preserving `printer_model` is necessary but **not sufficient** — the coordinator had previously told Jeff it was sufficient. Corrected explicitly in the design so he isn't operating on a false premise.
- **Two production-only index traps in `MachineModelProfileConfiguration`**: `(Name, SlicerType)` unique is global (family names collide with system models → chose 409 over an index migration), and `HasIndex(Hash).IsUnique()` is **unfiltered** — PostgreSQL treats NULLs as distinct but **SQL Server does not**, so a second null-hash family fails only on SQL Server. Ships green locally, breaks on deploy. Check unique-index NULL semantics per provider whenever adding user-owned rows to a system-seeded table.
- **Custom machine profiles are already catalog-model-scoped**, not printer-scoped (`NewSliceJobPage.tsx:1024-1039`, `classifyCustomProfileScope`). Reusing that contract avoided an AppDbContext migration entirely.
- `getWorkerHierarchy`/`getCatalogHierarchy` exist in `slicerProfilesService.ts:517-530` with **zero React callers** — confirmed by grep. The family concept was already modelled server-side and simply unwired.

### Calls made

- **Overruled Lambert Q12** (per-printer family scope) in favour of catalog-model scope for v1, mitigated by allowing multiple families per model. His concern is real; the mitigation is free.
- **Confirmed Lambert's addendum over his own report body** — no `PrinterModelAlias`, no `printer_model` rewrite. An alias for `Voron 2.4 180` would resolve against the worker cache and match nothing.
- **Reconciled Ripley vs Lambert on the loop's cause**: Ripley's missing `['customProfiles']` invalidation is real and proven, but `clone-from-template` creates *no* machine profile at all, so the cache fix alone cannot break the loop. Shipped it as Phase 0 anyway (free + correct) while stating plainly that it does not fix the report. Resisted the temptation to let a tidy one-line fix look like the answer.
- **Synthesised the allowlist-vs-intersection disagreement** rather than picking a side: allowlist bounds the *form*, invariance bounds the *safety*. Pure intersection is unusable because `fdm_klipper_common` makes hundreds of keys trivially invariant.

### Lesson worth carrying

When a user's design steer is structurally right but factually unbuildable as stated, the useful answer is neither rubber-stamp nor quiet redesign: separate the *shape* (keep it, name it as theirs) from the *anchor point* (move it, explain why with the actual file evidence). Jeff asked us to inspect real profiles — the honest citation of `Voron 2.4 250.json` having no `inherits` is what makes the redirect land as an answer rather than a refusal.

### 2026-08-25 (later) — v2 revision after live repro evidence

Jeff captured authenticated live evidence that **re-ranked the root cause** and superseded part of both inbox reports. Design updated to v2.

- **The real blocker is an alias gap, and it 404s.** `GET /api/slicer/profiles/machine/for-model/{modelId}` returns `404 No OrcaSlicer alias configured for model Voron 2.4 180` — **not** `200 []`. I traced it: the gate is a hard `NotFound` in the **controller** at `ProfilesController.cs:872-876`, so `GetMachineProfilesForCatalogModelAsync` is never even called. Contrast printer arco1 (`Phrozen Arco`) returns 200 on the same endpoint, proving the mechanism is healthy. Lambert predicted the alias mechanism correctly; **Ripley's "(a) stale cache" classification was superseded on the mechanism** — she reasoned from an assumed empty-array worker response that never happens.
- **Lesson: a report that reasons from an assumed response shape can be internally consistent and still wrong.** Ripley's cache defect was real and I verified it independently — but her *causal chain* started from `receives [] from the live worker lookup`, and the endpoint never returns that. When an investigation's first step is an assumed HTTP response, insist on the captured one. I had already flagged her finding as non-blocking in v1 on Lambert's evidence; the live capture demoted it further, to rank 4.
- **Resolved the alias tension by declining the mechanism while accepting the requirement.** The instinctive fix (write a `PrinterModelAlias` so the gate passes) encodes a false claim into shared catalog state: the table means "OrcaSlicer knows this model by this name", consumed by exact matching against Orca's own `printer_model`, and Orca ships no `Voron 2.4 180`. Lambert's own text also concedes the alias is insufficient without the union — so the union is required either way and the alias then contributes nothing. Decision: **open the gate** (union stock + custom families; 404 only when both are empty), write no alias, no AppDbContext migration.
- **General pattern worth keeping:** when a precondition check blocks a feature, the choice is *satisfy the check* vs *correct the check*. Satisfying it with synthetic data is almost always wrong when the check's semantics are a factual claim about an external system. Ask what the field *means*, not what value would make the branch pass.
- **New concrete traps recorded:** (1) returning custom family DTOs with the *source* `printer_model` is required for condition-based compatibility, but any UI grouping on `printer_model` will then label the Micron family "Voron 2.4 250" — must group on a new `familyName`; (2) `for-model` 404s while sibling `available-for-printer` returns `200 []` for the same condition — inconsistent, fix together; (3) split the 404 into `no_profiles_for_model` vs `alias_matched_no_profiles`, because after an Orca upgrade the symptom is identical from the user's seat and "create a family" would be exactly the wrong advice.
- **UX finding from the live capture:** the empty state says "No machine **profiles**" while the modal offers to clone "**process** profiles" from a picker of single nozzle variants, via an endpoint that creates neither. Three mismatches in one card. Wrote replacement copy into the design. Worth remembering that a UI can be the *cause* of a reported bug loop without any code being broken on that path.
- Restructured phases so nobody ships the tidy one-line cache fix and calls it a fix: Phase 0 is explicitly labelled as producing **no user-visible change**, and Phase 1 (honest copy + reason codes) is now the first thing Jeff notices.

### 2026-08-25 (later still) — v3 revision after Jeff proposed filesystem injection

Jeff asked: *"should we create our own printer models then and inject them into the orcaslicer container during build?"* The coordinator supplied a grounding pass and asked me to adjudicate. **This flipped the architecture** — filesystem injection wins decisively over the DB-resolver design, and Jeff's original two-level base-template hierarchy becomes *literally buildable* as real OrcaSlicer inheritance rather than a PrintFarmer-level simulation of it.

- **I verified the grounding pass instead of building on it, and it contained a load-bearing error.** It stated the worker "rebuilds the SQLite cache when the filesystem catalog hash changes", implying new files are ingested automatically. False. `CachedOrcaProfilesService.InitializeAsync` opens with `if (_cacheInitialized) return;` (`:63-66`); the hash is computed **once**, at startup; `InvalidateCacheAsync` (`:281-293`) has **zero callers**; there is **no `FileSystemWatcher`** anywhere in the worker; and the worker's `SlicerProfilesController` is entirely read-only. The whole runtime-injection story depended on that claim. **Lesson: when a hand-off tells you the hard part is already solved, that is precisely the claim to check first — it is the one that, if wrong, invalidates the design rather than a detail of it.**
- **The opposite also happened, and it is the more valuable half.** I expected cross-manufacturer inheritance to be the blocker and found it **already shipped**: `FindParentProfile` (`OrcaProfilesService.cs:853-892`) falls through to `FindParentProfileAcrossManufacturers` (`:937-961`), which enumerates every manufacturer directory under the root. So `Custom/machine/Micron 180 base.json` inheriting `"Voron 2.4 250 0.4 nozzle"` resolves natively, in existing exercised code. **Before designing new machinery, check whether the expensive part already exists upstream.** That single method is why Jeff's steer went from "honour the shape, move the anchor" to "build it exactly as he described".
- **The same method forced the biggest constraint.** It enumerates **one** root — so cross-*root* inheritance does not exist, which kills the multi-root option outright. Teaching the scanner multiple roots means touching parent resolution, the manufacturer-keyed lookup cache, the hash function and the cross-manufacturer search — i.e. reimplementing the loader, exactly the cost we're avoiding. Recommended a composed overlay root instead, and flagged the symlink-traversal question as a **spike, not an assertion** — I do not know how .NET's directory enumeration treats directory symlinks on Linux here, and guessing would have been the wrong kind of confidence.
- **I reversed my own v2 recommendation, in writing, in the same document.** v2 refused to create a `PrinterModelAlias` because it would encode a false claim (Orca ships no `Voron 2.4 180`). Under v3 we *install a bundle that genuinely declares it* — so the claim becomes true and the alias is correct. The v2 reasoning was right given v2's premise and wrong given v3's. **Marking the old section reversed and saying why is better than silently deleting it**: the principle survives intact (*don't write false data to satisfy a precondition — change reality so the precondition is honestly met*), and a reader can see the premise that moved.
- **Timing was the coordinator's concern and it was correct.** "During build" defeats the feature — a family that needs an image rebuild is unusable for click-to-clone. Runtime injection into a persistent volume is strictly better. But build-time is *not* wrong for everything: shipping curated, reviewed stock families is a legitimate separate use of the same mechanism, so I said so rather than flatly rejecting his phrasing.
- **Second-order trap I nearly missed:** invalidating the SQLite cache is not enough. The inner `OrcaProfilesService` holds process-lifetime in-memory dictionaries (`:46`, `:50`, `:998`, `:1028`) with **no eviction method**, so an invalidate endpoint would rebuild through stale lookups — green in dev (fresh process), broken in production (long-lived container). Same species as the SQL-Server-only unique-index trap from v1: **the bugs that matter here are the ones whose failure mode is environment-dependent.**
- **A tolerant loader is safe for vendor data and dangerous for generated data.** `CollectInheritanceChainAsJson` warns on a missing parent and imports the child anyway (`:719-722`, `:732-734`). Fine for shipped Voron files; catastrophic for an injected Micron base after an upgrade renames its parent — the family would load stripped of every inherited setting and still present as valid. Must become a hard failure **scoped to custom bundles only**. Generalisable: leniency policies should be scoped by data provenance, not applied uniformly.
- **Deployment reality changed a storage decision.** `docker-compose.orcaslicer-worker-previous.yml` runs a **second worker on a different Orca version simultaneously** — so the custom-profiles volume must be **version-keyed**. A base inheriting `Voron 2.4 250 0.4 nozzle` is only valid where that preset exists under that name. I would have shipped a single shared volume if I hadn't read the compose templates.
- **Kept the DB as system of record rather than going filesystem-only.** Rendered files are *generated artifacts*; ownership, provenance, source version and the shared override bag belong in the DB. That single choice answers upgrade re-render, volume-loss recovery, manual-edit drift and family editing through one code path — and it means Lambert's entity work survives even though his resolver does not.
- **Explicitly listed which of Lambert's findings survive** (exact-preset-name compatibility, the `WithSystemPresetInherits` overwrite hazard — now *more* important, the per-nozzle field split, `machine_model` not being inheritable) and which are superseded (the DB resolver, `RawJson` handling). When a design supersedes a teammate's central conclusion, itemising what still stands is what keeps the earlier work usable instead of discarded.

### Lesson worth carrying (v3)

A user's "naive" suggestion can be better than the expert design, and the way to find out is to go read the code rather than to reason about it. Jeff's proposal sounded like a deployment hack; it dissolved the single hardest blocker in the previous design because the capability it needed was already implemented. **The reflex to defend an existing design is the thing to watch for** — my job was to test his idea honestly, including the possibility that it beat mine, and it did.

---

## 2026-08-25 — v4 addendum: measured the real vendor data, overruled an expert, retracted myself

**Context.** Jeff reframed the feature twice more: first "inject profiles into the OrcaSlicer container" (v3), then "this isn't about my Micron — it's any catalog printer with no profiles, and clone machine + process + filament" (v4). The v4 message asked me to adjudicate Jeff's "clone all" against Lambert's "project dynamically, never duplicate".

**What I did differently, and it mattered more than anything else this session.** Instead of reasoning from the two inbox reports, I pulled the actual Voron bundle from `SoftFever/OrcaSlicer@main` and read the real files. That single decision overturned a well-argued expert recommendation in about twenty minutes:

- A stock process leaf (`0.20mm Standard @Voron`) is **nine keys**. All substance is inherited. So a "clone" under filesystem injection is a ~200-byte stub, not a duplicated profile.
- `Voron.json` declares `filament_list: 0`. Voron ships **no filament profiles at all** — the entire filament half of the question evaporated on contact with data.
- The compatibility arrays live in **9 band-base files**, not the 41 leaves. Mirroring that structure means 9 maintained arrays, not 41.
- Full family: ~59 files, ~12 KB.

**Lesson 1 — go read the real data before adjudicating between experts.** Both Lambert and I had been arguing about duplication cost from first principles. Neither of us had counted. The count decided it instantly and unambiguously.

**Lesson 2 — the cost of an option can invert when the substrate changes underneath it.** Lambert's anti-duplication recommendation was *correct for the architecture he investigated* (DB rows holding resolved profiles). Jeff's filesystem proposal arrived after Lambert's report and changed what "duplicate" means. I made a point of saying this explicitly in the doc: he wasn't wrong, he was answering a question that had since changed. Overruling a teammate is much cheaper when you can name precisely which premise moved.

**Lesson 3 — retract your own errors loudly, in the same document, at the point of the error.** v3 flagged `WithSystemPresetInherits` (`OrcaSlicingPipelineService.cs:848-861`) as a blocker needing a carve-out. It isn't — that assumed the "masquerade" approach, which v4 rejects. I put the retraction inline at §A(c) item 1, again in §B10.3, and once more in the ledger. An implementer who read only the summary would otherwise have burned a day defending a fix for issue #1768 that never needed touching. Same for v2's "object → deep merge" claim: `MergeProfilesJson` is a **flat top-level replace**, which as it happens is exactly what the design needs.

**Lesson 4 — the two-gate distinction is the kind of thing that only falls out of reading both call sites.** Discovery (`SlicerProfilesController.cs:673-679`) filters on the machine's `Name`; execution (`OrcaSlicingPipelineService.cs:913-931`) uses the *system preset name*. The masquerade satisfies the second and completely fails the first. Any design that only reasons about slice-time compatibility will produce a feature where the profile works but the user can never select it.

**Lesson 5 — cross-manufacturer *inheritance* works, cross-manufacturer *compatibility evaluation* does not.** `FindParentProfileAcrossManufacturers` searches all bundles; condition evaluation is scoped to `GetCachedMachinesForManufacturer(folderName)`. That asymmetry is the entire reason cloning is mandatory rather than merely convenient, and it isn't documented anywhere.

**Lesson 6 — generalizing on the user's instruction made the design simpler, not harder.** I expected "make it generic" to add scope. It removed it: forcing the mechanism to be vendor-native killed the Voron-specific special-casing I'd been carrying since v1.

**Process note.** Four revisions, each triggered by new input rather than by rework. Keeping superseded sections in place with dated banners (rather than deleting them) turned out to be worth the clutter — Jeff and the coordinator can see *why* the recommendation moved, and I could point at exactly which premise changed when overruling Lambert. I'd do it again on a document that evolves this fast.

**Standing discipline that held.** No code, no migrations, nothing written outside `.squad/`, and I did **not** file the catalog-coverage issue I recommended — recommended only, as instructed.
