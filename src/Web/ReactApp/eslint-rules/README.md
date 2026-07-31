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

## `local/pf-no-oversized-radius`

[`DESIGN-LANGUAGE.md`](../src/design-system/DESIGN-LANGUAGE.md) defines a five-step radius
scale (`--pf-radius-xs` 2px → `--pf-radius-lg` 8px, plus `--pf-radius-full`) and states that
**rectangular surfaces cap at 8px**; fully-round is reserved for shapes that are genuinely
circular or pill-shaped by design. Tailwind ships radii far above that ceiling
(`rounded-2xl` = 16px, `rounded-3xl` = 24px), and arbitrary values like `rounded-[1.75rem]`
bypass the scale entirely. This rule keeps both off rectangular surfaces.

It reports two independent families.

### 1. Over the ceiling

Any radius larger than `maxPx`, whether a named size (`rounded-xl` … `rounded-4xl`) or an
arbitrary value (`rounded-[1.35rem]`).

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
'local/pf-no-oversized-radius': ['error', { maxPx: 12, checkFullRound: false }]
```

| Option | Default | Meaning |
|---|---|---|
| `maxPx` | `8` | Largest permitted radius in px. `8` is the documented `--pf-radius-lg` ceiling. |
| `checkFullRound` | `false` | Also report non-circular `rounded-full`. |

### Current configuration, and how to finish the migration

The repo is linted in two tiers, because the two families have very different migration costs:

- **Repo-wide:** `maxPx: 12`, `checkFullRound: false`. This blocks everything that is
  unambiguously outside the scale (`2xl` and above, oversized arbitrary values) while
  grandfathering the ~80 existing `rounded-xl` call sites.
- **`features/admin`, `features/settings`, `design-system`:** `maxPx: 8`,
  `checkFullRound: true` — the actual documented rule, applied to the areas migrated by
  epic #1005.

The tiering is deliberate: the lint baseline is zero errors *and* zero warnings, and it is
worth keeping that way. Turning the strict settings on repo-wide today would emit ~90
`rounded-full` reports at once — each needing a human decision about whether the shape is
meant to be round — which would bury the signal and train everyone to ignore it.

To finish the job: adjudicate the remaining `rounded-full` sites (mark the genuine pills with
`data-pf-radius="full"`, flatten the rest), migrate the `rounded-xl` sites, then drop the
repo-wide config to `{ maxPx: 8, checkFullRound: true }` and delete the override block.

### Reading class names

Class strings are collected recursively, so the rule sees radii inside `clsx()`/`cn()` calls,
nested arrays and object keys, template literal quasis, and plain `className` strings.
Responsive and state variants are stripped before matching, so `md:rounded-2xl` and
`hover:rounded-2xl` are caught. Side-specific utilities are handled explicitly — `rounded-l`
is the left side, not a size.

Arbitrary values in units the rule cannot resolve statically (e.g. `rounded-[var(--x)]`) are
left alone rather than guessed at.
