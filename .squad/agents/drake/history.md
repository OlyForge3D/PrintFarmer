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
