# Drake — History

## Core Context

PrintFarmer: React 19 + TypeScript frontend (`src/Web/ReactApp`, Vite, Vitest, Tailwind) against
an ASP.NET Core 10 API. Joined during the final gate round of epic #931 (Admin Console & Settings
redesign).

## Learnings

### `prop.name` is the camelCase JSON wire name, not the .NET property name

`getSettings` returns raw wire JSON with no normalisation, so `prop.name` is whatever
`[JsonPropertyName]` produced — `perEngine`, not `PerEngine`. This single mismatch caused **four
separate defects** across epic #931: broken command-palette deep-links, mispaired DOM
`id`/`htmlFor`, wrong form `name` attributes, and mis-attributed validation errors.

Compounding it: `enabled` appears on 13 settings classes, `intervalSeconds` on 4, `baseUrl` on 3
— and several of those sections render on the same page, so a bare property name is not even
unique within a page.

**Treat any bare-property-name lookup in this codebase as suspect until proven otherwise.**

### The two error maps in `SettingsPage` are not symmetric

`GroupSaveBlock` holds two error maps, and they behave differently in a way that is easy to miss:

- `fieldErrors` — **self-healing.** `handleFieldChange` recomputes `validateSection` on every
  keystroke and overwrites that section's entry, so stale field errors clear themselves as the
  user types.
- `sectionErrors` — **not self-healing.** These come from the server (a memberless
  `ValidationException`) and cannot be re-derived on the client. Nothing clears them except an
  explicit reset.

The bug I was brought in to fix came from copying the `{ ...prev, ...next }` merge pattern from
the first onto the second. On a partial-failure save, sections that succeeded kept their old
alert, because only *failing* sections contribute new entries and the spread preserved everything
else.

Fix: drop the attempted section keys from the previous state before applying the current
attempt's errors, so only this save's failures survive. Sections outside the save stay untouched.
Also clear a section's server error on field edit, matching the field-error behaviour.

### Memberless `ValidationException` is the common case, not the edge case

**21 of the 23 `throw new ValidationException(...)` sites** across the settings classes pass no
`MemberNames`. Only `ExternalServicesHealthSettings` populates them. Any error-handling path that
only considers the member-named shape will drop the message for nearly every validation failure a
user can actually trigger.

### Testing note

When writing a regression test for the partial-failure save path, deliberately make **no edits
between the two save attempts**. Editing a field triggers the `handleFieldChange` self-healing
path, which masks the bug and lets the test pass against unfixed code.

Verified red-before-green by stashing only `SettingsPage.tsx`: the new test failed with
`AssertionError: expected true to be false`, then passed once restored.

### Vitest output on Windows

Summary lines have leading whitespace, so `Select-String "Tests "` on raw console output misses
them. Pipe to a file first: `npm run test:run 2>&1 | Out-File -Encoding utf8 t.log`.

### Fresh worktrees don't have `node_modules`

Worktrees under `D:\s\copilot-worktrees\...` are pristine git checkouts — no `node_modules`. Any
`vitest` / `npm run build` run fails with `Cannot find package 'vitest'` (it tries to resolve
against the main checkout at `D:\s\pfarm1\...` and gets the wrong path). Fix: run
`npm ci --no-audit --no-fund` in the worktree's `src/Web/ReactApp` first. This doesn't touch
`package.json` or `package-lock.json`, so it stays inside the "no dependency changes" rule.

### The four admin feedback patterns, and which one to reach for

As of epic #931 merge, the admin surface has four distinct failure-feedback shapes:

1. `adminToast.error(msg)` — imperative, button-triggered "action failed" (Save, Search, Export).
   Used by `SettingsPage`. **This is the right choice for anything that isn't a query hook.**
2. `<AdminError error={error} ... />` — declarative, for React Query load failures with retry.
   Used by `LoginAuditPage`, `AdminControlCenterPage`.
3. `setSectionErrors` / `setFieldErrors` — inline, form-level, for validation errors on a
   specific field. Only in `SettingsPage`'s `GroupSaveBlock`.
4. `window.alert()` — legacy. Should not exist anywhere on the admin surface. Was in
   `SystemLogsContent.tsx`; #943 removed it. Grep before adding more; there are still two
   in `features/slicer/components/SlicerConfigModal.tsx` (non-admin, so out of #943's scope).

Do NOT introduce a fifth shape for two call sites. Match #1 or #2 to the call shape.

### `createObjectURL` without `revokeObjectURL` is a codebase-wide pattern

`SystemLogsContent.exportLogs` creates a blob URL for the download `<a>` and never revokes it —
small memory leak per export. Noted in the #943 PR but not fixed (scope). Worth grepping the
codebase for the same shape before touching any other export path.

## 2026-09-03: iOS Navigation Redesign Testing (1 child issue)

Assigned to testing and validation of A′ · Two Hats, adaptive shell.

**Epic**: #2410 — iOS Navigation Redesign
**Assigned issue**: #2413 (testing and validation)
**Role**: QA and test coverage — shell behavior, mode transitions, edge cases
**Status**: PENDING (awaiting implementation start)
