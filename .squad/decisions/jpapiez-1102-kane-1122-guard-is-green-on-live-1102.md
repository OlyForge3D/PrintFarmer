### 2026-08-05T09-40-00: Kane's #1122 guard (5169b3cc4) is green on live #1102, and the blindness generalises

**By:** jpapiez-1102
**References:** #1102, #1122, 5169b3cc4, dev/jpapiez/1102-reconciled

**What:** Falsified the pending #1122 guard by injection rather than by inspection. It passes with
#1102 fully reintroduced. The blind spot is a property of built-CSS placement checking as a
technique, not of that guard's authorship.

**Method:** Injected the real defect into the variant map -- `subtle: 'border'` ->
`subtle: 'bg-transparent border'`, +15 bytes on Button.tsx, line read back to confirm the injection
applied before trusting any result. Rebuilt (`npm run build`), then ran Kane's guard from
`5169b3cc4` alongside the branch guard.

**Result:**

    kaneGuardScratch.test.ts               (2 tests)              <- GREEN on live #1102
    ghostButtonBuiltStylesheet.test.ts     (21 tests | 1 failed)
      x subtle declares no background or shadow utility           <- RED, names the site

Kane's guard contains zero occurrences of `subtle`, `tab`, `toggle`, `link` or `bg-transparent`.

**The generalisation, which is the important part:** the branch's OWN built-stylesheet assertions
also stayed green under the same injection --

    v keeps the subtle, tab, toggle and link paint defaults below caller utilities
    v detects an unlayered subtle paint rule in the built artifact

Of 21 tests only the source-level contract went red. The two failure modes are orthogonal: a paint
re-add in the variant map does not move the component-layer rule or reorder the layers, so
`@layer components` defaults stay correctly placed and a caller class still displaces them. What
changes is that the variant now ALSO emits a utility that outranks the caller. Every placement
assertion inspects the half of the artifact the defect does not touch.

Consequence: hardening the stylesheet assertions cannot close this. Only a source-level assertion
on the variant map can. A green "ghost button built stylesheet" run must not be read as #1102
coverage.

**Collision to resolve before #1122 lands:** `5169b3cc4` and this branch both create
`src/Web/ReactApp/src/test/styles/ghostButtonBuiltStylesheet.test.ts` -- 248 lines vs 400. Whichever
lands second conflicts on the whole file, not a hunk. They are not alternatives: Kane's
`detects an unlayered ghost paint rule` negative test covers a mode this branch does not assert.
The correct resolution is a union, done deliberately by someone holding both, not by whoever hits
the conflict.

**Hygiene:** tree restored with `git checkout --` and `subtle: 'border'` read back, scratch copy
deleted, `dist` rebuilt from clean source, branch tip unchanged at the time of measurement.
