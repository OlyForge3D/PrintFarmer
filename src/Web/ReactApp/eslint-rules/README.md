# Local ESLint rules (`local/*`)

Project-specific rules that encode PrintFarmer conventions a generic linter cannot know
about. Registered in [`eslint-plugin-local.js`](./eslint-plugin-local.js) and configured in
[`../eslint.config.js`](../eslint.config.js).

| Rule | Enforces |
|---|---|
| `local/pf-require-apiclient` | All REST calls go through the `apiClient` singleton from `@/services/api` — no direct `axios`/`fetch`, no custom axios instances. |
| `local/pf-no-raw-html-controls` | Shared UI components instead of raw `<button>`/`<input>`/`<select>`/`<textarea>`. See `UI_COMPONENTS_GUIDE.md`. |
| `local/pf-no-unguarded-console` | `console.log/debug/info` in UI code is wrapped in a `window.PrintFarmerDebug` guard; no raw object dumps in JSX. |
| `local/no-hardcoded-colors` | Theme tokens instead of literal hex/rgb/Tailwind palette colors. |
| `local/pf-no-oversized-radius` | Border radii stay inside the `DESIGN-LANGUAGE.md` scale. See below. |
| `local/pf-no-inert-state-bg` | Direct `hover:`/`active:` backgrounds must differ from the background already active at that interaction stage. See below. |

## `local/pf-no-inert-state-bg`

This rule prevents token migrations from silently erasing interactive feedback. It reports a
direct `hover:` background that repeats the base background and an `active:` background that
repeats the hover background:

```tsx
<button className="bg-pf-bg-2 hover:bg-pf-bg-2" />
<div className="bg-pf-border hover:bg-pf-accent active:bg-pf-accent" />
```

The rule is intentionally background-only. Run the class-order-independent JSX inventory from
the React root; its JSON output records the scanned scope, Git HEAD, dirty state, counts, and
every matching site:

```bash
npm run lint:inventory-inert-state
```

The 2026-08-07 inventory found 21 current same-token base/state shapes: 8 text, 9 border,
3 ring, and 1 background. All 21 were manually classified as deliberate pins. The background case is
`disabled:hover:bg-pf-bg-1`, which preserves the disabled surface and is excluded. The
selected direct `hover`/`active` background strategy therefore reports zero current source
findings while still reproducing all three #1082 regressions.

The rejected all-utility strategy would report error text preserved on hover, error borders
preserved on focus, and selected borders preserved on hover. Ancestor analysis is unnecessary
for the narrower rule: `group-hover:` and `peer-hover:` are not direct element states and remain
green. Responsive and theme prefixes are retained as context, so
`dark:md:bg-pf-bg-1 dark:md:hover:bg-pf-bg-1` is still detected. Opacity suffixes are part of
the value, so `/55` to `/75` is a real change. Mutually exclusive ternary branches are analyzed
separately.

The lint rule analyzes only JSX `class`/`className` attributes; it does not resolve standalone
class constants. Within those attributes, strings are read from literals, templates, arrays,
object keys, and helper calls such as `clsx()` and `cn()`. Important markers, CSS-variable
shorthand, and color-typed arbitrary values are parsed without splitting colons inside brackets
or functions. Negative background utilities are ignored because they are positioning utilities
rather than colors.

If a direct hover or active background genuinely must stay pinned, document that exceptional
intent at the element:

```tsx
<button
  data-pf-allow-inert-bg="selected state deliberately stays pinned"
  className="bg-pf-accent-bg hover:bg-pf-accent-bg"
/>
```

The rule has no autofix because choosing a replacement background is a design decision.

## `local/pf-no-oversized-radius`

[`DESIGN-LANGUAGE.md`](../src/design-system/DESIGN-LANGUAGE.md) defines a five-step radius
scale (`--pf-radius-xs` 2px → `--pf-radius-lg` 8px, plus `--pf-radius-full`) and states that
**rectangular surfaces cap at 8px**; fully-round is reserved for shapes that are genuinely
circular or pill-shaped by design. Tailwind ships radii far above that ceiling
(`rounded-2xl` = 16px, `rounded-3xl` = 24px), and arbitrary values like `rounded-[1.75rem]`
bypass the scale entirely. This rule keeps both off rectangular surfaces.

It reports two independent families.

### 1. Over the ceiling

Any radius larger than `maxPx`, whether a named size (`rounded-xl` … `rounded-4xl`), an
arbitrary value (`rounded-[1.35rem]`), or a single-value arbitrary property
(`border-radius:12px` written as `[border-radius:12px]`).

The arbitrary-value reader follows the Tailwind-to-CSS forms that can be resolved safely:
optional `length:` hints, CSS comments, Tailwind underscore spaces, case-insensitive units,
leading `+`, scientific notation, and escaped `!important` identifiers. Invalid negative
radii are ignored because the browser drops them. Values that require runtime layout or
custom properties, and multi-value or elliptical `border-radius` shorthands, remain
unresolved rather than guessed.

Named sizes are **auto-fixable** — `--fix` rewrites them to `rounded-lg`. Arbitrary values
only get a *suggestion*, because the correct replacement is a judgement call: a 1.1rem inner
panel nested inside a 1.35rem card usually wants `rounded-sm`, not `rounded-lg`, to preserve
the concentric descent.

### 2. Fully round (`checkFullRound`)

`rounded-full` on an element that is not demonstrably circular. Off by default; on in the
scopes that have been migrated.

An element passes without any code change when its own classes prove it is round:
explicit matching `w-N`/`h-N`, `size-N`, `aspect-square`, or a spinner animation
(`animate-spin`, `animate-ping`, `pf-animate-spin`). That covers avatars, dots, spinners and
circular icon buttons automatically.

Everything else that legitimately needs a pill — tag chips and progress bars, both sanctioned
by `DESIGN-LANGUAGE.md` — declares it explicitly:

```tsx
<span data-pf-radius="full" className="rounded-full px-3 py-1">{tag.name}</span>
```

`data-pf-progress-track` and `data-pf-progress-fill` are honoured as waivers too, so progress
bars need no second annotation. This follows the existing `data-pf-button` convention: it is
greppable, it survives renames, and it cannot be satisfied by accident the way a className
substring match can.

### Options

```js
// eslint.config.js — the whole registration, repo-wide.
'local/pf-no-oversized-radius': ['error', { maxPx: 8, checkFullRound: true }]
```

| Option | Default | Meaning |
|---|---|---|
| `maxPx` | `8` | Largest permitted radius in px. `8` is the documented `--pf-radius-lg` ceiling. |
| `checkFullRound` | `false` | Also report non-circular `rounded-full`. |

### Current configuration

One tier, repo-wide: `maxPx: 8`, `checkFullRound: true` — the documented rule, applied everywhere.

It ran in two tiers while the backlog was cleared. A grandfathered 12px ceiling held the line
repo-wide, and the real setting applied only to the areas epic #1005 had already migrated, because
turning the strict settings on everywhere would have emitted ~170 reports at once — which would have
buried the signal and trained everyone to ignore it. 77 of those were `rounded-xl` and the rule
autofixed them; the other 93 were `rounded-full`, four of which were the rule's own blind spot,
leaving 89 that each needed a human decision about whether the shape is meant to be round. #1022
did that work, so the override block is gone and the ceiling is the one in DESIGN-LANGUAGE.

Adjudicating `rounded-full` meant deciding per site what the element actually is. Most were already
excused by shape evidence the rule can see for itself (`size-*`, matching `w-`/`h-`, `aspect-square`,
spinner animations). The rest were either genuine circles, which got that evidence or an explicit
`data-pf-radius="full"`, or rectangles wearing a pill, which became `rounded-xs`.

Shape evidence is resolved as a small CSS cascade. The resolver keeps selector target scope,
ordered state variants, and cumulative breakpoints separate. Host dimensions therefore do not
excuse a descendant radius, while the slider thumbs in `Slider.tsx` and `SettingRow.tsx` continue
to pass because their `w-*`, `h-*`, and `rounded-full` utilities share the same
`[&::-webkit-slider-thumb]` or `[&::-moz-range-thumb]` scope.

Arbitrary descendant selectors, child variants, and Tailwind's named pseudo-element variants
(`before`, `after`, `file`, `marker`, and related forms) establish distinct target scopes. State
conditions on either side of that scope remain distinct. Legacy single-colon pseudo-element
selectors such as `[&:before]` establish the same boundary, while host pseudo-classes such as
`[&:hover]` and `[&:has(...)]` do not. Mutually exclusive built-in states and media preferences
are not combined into impossible cascades.

Within one target scope, evidence applies when its ordered state condition is active where the
radius applies. State variants contribute selector specificity; media variants do not.
Breakpoints are cumulative and ordered, so an `md:` declaration still participates at `lg:`.
The resolver ranks the project’s known state order (`hover`, `focus`, and related built-ins),
the complete inside-out ordering of stacked state variants, axis-over-`size-*` utility order, and
numeric dimension candidates.
It does not use class-string order as a proxy for Tailwind's generated stylesheet. If two
applicable declarations require unsupported ordering knowledge — competing radius/aspect
candidates, different media families, arbitrary selector payloads, or cross-family dimension
candidates (including dimension keywords) — the result is ambiguous and the rule withholds a
report rather than risk a false positive. Width, height, and aspect ratio are resolved
independently before the rule decides whether the resulting box is circular. A radius is checked
in later breakpoint, media, and state conditions where it remains active, not only at the
condition where its class was declared.
Radius declarations are resolved per corner, so `rounded-t-lg` does not hide a surviving
`rounded-full` on the bottom corners.

The resolver remains deliberately conservative around dynamic dimensions. A winning width or
height containing `var()`, `if()`, `calc()`, `min()`, `max()`, or `clamp()` cannot be evaluated
without the browser's computed custom properties and layout context. The rule withholds a
`rounded-full` report in that case rather than risk a false positive. Semicolons invalidate a
dimension only when their nearest enclosing function is CSS math; `if()` branch separators remain
valid. Identical static dimensions, known aspect ratios, and state/media overrides are still
resolved normally.

### Reading class names

Class strings are collected recursively, so the rule sees radii inside `clsx()`/`cn()` calls,
nested arrays and object keys, template literal quasis, and plain `className` strings.
Responsive and state variants are stripped before matching the radius itself, so `md:rounded-2xl`
and `hover:rounded-2xl` are caught. Their ordered variant segments are retained for the cascade
resolver described above. Side-specific utilities are handled explicitly — `rounded-l` is the
left side, not a size.

Arbitrary values in units the rule cannot resolve statically (e.g. `rounded-[var(--x)]`) are
left alone rather than guessed at. Unterminated CSS comments are handled by Tailwind's
generated declaration order: variant layers are ordered before property families are compared,
so a base utility can swallow a later variant radius while a variant utility cannot swallow an
already-emitted base radius. Within one variant condition, a comment emitted before
`border-radius` can swallow the radius, while one emitted afterward cannot. A radius value
before its own unterminated comment still applies and is judged.
