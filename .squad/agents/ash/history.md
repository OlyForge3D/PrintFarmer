
## Learnings

### 2025-01-13 — Epic #940 (admin/settings surface documentation)

**Verification-first paid off again.** Coordinator's `trust these facts'' block was 100 percent accurate this time (three cheers for a re-baselined issue after #938 merged), but I still opened every file I described. The moment I stopped verifying is the moment I would start writing fiction — the two prior derailments on this epic were proof.

**Highest-value single thing to document: essential-manifest silent demotion.** `essential-manifest.ts` keys on backend `SectionName`/`JsonPropertyName`. Rename a backend property, no build error, no warning, the setting just silently demotes to advanced-only. That is a trap I would want the next agent to see the first time they read SETTINGS_ARCHITECTURE.md.

**Second-highest: the section-qualified deep-link contract.** `?field=Section.Property` is load-bearing because `Enabled` alone appears in 13 settings classes (code comment at SettingsPage.tsx:438 confirms). Bare property names resolved to the wrong row.

**Pre-existing broken docs I found but did not fix (scope):**
- `README.md:139` links non-existent `docs/DATABASE.md`
- `README.md:147` links non-existent `docs/QUICK_REFERENCE.md`
- `docs/SETTINGS_ARCHITECTURE.md` was TEXTUALLY corrupted around lines 40-64 before this epic (Phase 5 status text interleaved into a Phase 1 code sample). Fixed as part of the rewrite.
- `docs/design/settings-redesign-v2.md` and `ui-reorganization-requirements.md` describe URLs that never shipped. Left alone — they are archival planning docs, not user-facing.
- `archived/root-level-notes/features/EXTENDING_DYNAMIC_SETTINGS_UI.md` uses old attribute names (`SystemSetting(DisplayName = ...)`). Archived, not fixing.
- `Job Queue` group in `HistorySeedingBackgroundService.cs` is unreachable via UI (no tab lists it in `allowedGroups`). Documented as a known limitation, not fixed.
- Old `ModelDetailPage.tsx`, `ModelController.cs`, and `Entities.cs` paths cited in TAGGING_SYSTEM.md no longer exist as single files. Reworded that pointer list rather than making up new locations.

**Markdown quirk:** `.github/instructions/markdown.instructions.md` has YAML front matter (post_title, microsoft_alias, categories from categories.txt) clearly templated from a Microsoft blog repo. Existing docs in this repo do NOT use that front matter. I did not add it to my edits.

**Route ownership caption in copilot-instructions.md:** I briefly regressed a plain caption line ("Route ownership in microservices mode:") into an H2 while editing, then reverted. Worth remembering: the file mixes H2 sections with H2-looking captions and it is easy to break the visual hierarchy.
