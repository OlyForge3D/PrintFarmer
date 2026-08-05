# PrintFarmer Theme System Architecture

## Overview

PrintFarmer ships eight selectable themes. Every colour in the app resolves through a
`--pf-*` CSS custom property, and each theme is one stylesheet that declares the full
token set. Components never hardcode colours; they consume tokens, either directly via
`var(--pf-…)` or through the Tailwind utilities generated from them (`bg-pf-accent`,
`text-pf-text-primary`, …).

## The cascade-layer asymmetry (read this first)

`src/index.css` imports the two stylesheet families differently, and the difference is
load-bearing:

```css
@import './styles/theme.css' layer(base);        /* layered   */
@import './design-system/themes/dark.css';       /* unlayered */
```

Cascade layers are resolved **before** specificity. An unlayered declaration beats a
layered one no matter how specific the layered selector is. `dark.css` opens with

```css
:root,
[data-theme="dark"] { … }
```

and that `:root` matches unconditionally, under every theme. So **any `--pf-*` token
declared under `src/styles/` is dead** — it will always lose to the design-system
themes.

This is not hypothetical. Three themes (`forge`, `github-dark`, `printfarmer-dark`)
lived under `src/styles/themes/` and none of their palettes ever painted; selecting
`forge` rendered the `dark` palette. Nothing failed — not the build, not lint, not the
type checker.

**The subtle half:** layer order only arbitrates *competing* declarations. A layered
rule that nothing competes with applies normally. `forge` also declared
`[data-theme="forge"] h1 { text-shadow: … }`, and since no other rule sets `text-shadow`
on headings, that *did* paint. So the legacy themes were not uniformly dead, which is
exactly why the problem survived several passes of inspection.

Two consequences for anyone working here:

1. Put theme tokens **only** in `src/design-system/themes/`. `themeRegistry.test.ts`
   fails if a `[data-theme=…]` selector appears anywhere under `src/styles/`.
2. Do not conclude a rule is dead from its layer alone. Check whether anything actually
   competes with it, and prefer measuring the built stylesheet in a browser over reading
   source.

## Where things live

| Path | Role |
|---|---|
| `src/design-system/themes/registry.ts` | **Single source of truth** — `SELECTABLE_THEMES`, `normalizeTheme`, `RETIRED_THEME_MAP` |
| `src/design-system/themes/base.css` | Theme-independent tokens: fonts, spacing, radii, z-index |
| `src/design-system/themes/<theme>.css` | One file per theme; declares the 142 `--pf-*` tokens |
| `src/index.css` | Imports the themes (unlayered) and the `@theme` block that turns tokens into Tailwind utilities |
| `src/contexts/ThemeContext.tsx` | Owns the `data-theme` attribute and persistence |
| `src/common/components/ThemeSwitcher.tsx` | The theme picker rendered in settings |
| `index.html` | Pre-paint boot script that applies the stored theme before React hydrates |
| `src/styles/theme.css` | Global `:focus-visible` rules only. Declares no tokens, by design |

## The token contract

Every theme declares **the same 142 tokens** — same names, no more, no fewer.
`themeRegistry.test.ts` enforces this by diffing each theme's token set against a
reference theme.

This matters because custom properties cascade. A theme that omits a token does not
error; it silently inherits whatever was set previously, which usually means the
previous theme's value. Legacy `forge` declared 97 of the 142 and was missing
`--pf-text-inverse` and `--pf-on-accent` among others — the tokens that make text
readable on solid status and accent surfaces.

Broad groups: surfaces, borders, text, accent, progress, the four semantic families
(success / warning / error / info), six status families, controls, four button
variants, validation, feedback (focus ring, overlays, selection), skeleton, glows,
domain-specific, and a block of legacy aliases kept for backward compatibility.

## Theme switching

`ThemeContext` is the only thing that should write `data-theme`:

```tsx
const { theme, setTheme, computedTheme } = useTheme();
setTheme('forge');
```

- `theme` is the stored preference and may be `'system'`.
- `computedTheme` is the theme actually rendering, with `'system'` resolved against
  `prefers-color-scheme`.
- Every theme has an explicit `data-theme` value. There is no "remove the attribute to
  get the default" behaviour — relying on a bare `:root` default is what made the
  retired themes so hard to see.

`normalizeTheme` runs on read, so a stored value that is unknown, or names a retired
theme, resolves to something renderable instead of leaving the app in an undefined
state.

## Adding a new theme

A theme must appear in **four** places. Miss one and the failure is quiet:

1. `src/design-system/themes/<id>.css` — copy an existing theme and restyle it, so the
   token set stays identical.
2. `@import` it in `src/index.css`, **unlayered**.
3. Add its id to `SELECTABLE_THEMES` in `registry.ts`.
4. Add an entry to `THEME_OPTIONS` in `ThemeSwitcher.tsx`, and to `VALID` in the
   `index.html` boot script.

Omitting the boot-script entry does not break the theme — it makes it flash `dark` on
every page load until React hydrates, which is easy to miss and annoying to diagnose.
That happened to `ratos`, `voron` and `farm`.

`themeRegistry.test.ts` checks all of these agree, and additionally that no two themes
share a core palette.

### Theme-specific visual effects

Themes may add rule-level styling beyond tokens — `matrix` has a mono body face, a
phosphor heading glow and CRT scanlines; `forge` has a copper heading glow and ember
progress bars. Keep these in the theme's own stylesheet, scope them with
`[data-theme="<id>"]`, and guard decorative effects with
`@media (prefers-reduced-motion: reduce)`.

Prefer expressing an effect through a token when one exists. `forge`'s original
`input:focus` override was dropped during migration because setting `--pf-focus-ring`
produces the same copper ring through the shared focus-ring rule, without bypassing its
contrast guarantees.

## Accessibility

**Focus.** `src/styles/theme.css` provides a baseline `*:focus-visible` outline and a
two-tone ring for interactive elements, built from `--pf-focus-ring` and
`--pf-focus-ring-offset` so it reads against both the control and the surface behind it.

**Contrast.** Themes are expected to meet WCAG AA (4.5:1) for text on its intended
surface. Measure it; do not eyeball it. Resolve tokens in a real browser against the
built stylesheet rather than computing from source hex, so `var()` indirection and the
cascade are exercised for real.

**Reduced motion.** Honoured via `@media (prefers-reduced-motion: reduce)`.

**Not currently supported:**

- **High contrast** — no theme implements `prefers-contrast`. A block that appeared to
  provide it was measurably inert. Tracked in issue #1125.
- **Print** — print styling is non-functional for the same layer reason. Tracked in
  issue #1126.

## Verifying theme changes

Source inspection is unreliable here; the failure modes are all invisible ones. Useful
techniques:

- Set `data-theme` in a real browser against the built `dist` and read
  `getComputedStyle`. Compare themes against each other, and compare a branch build
  against a base build.
- Compare **computed** values, not declared ones — the whole class of bug in this
  subsystem is declarations that never take effect.
- Custom properties are not the whole story. Diff the **selector set** of the built
  stylesheet too, or rule-level effects will slip past unnoticed.
- Beware that Tailwind's scanner reads comments and any file in the project tree,
  including scratch scripts — naming a class in prose can re-emit it into the build and
  invalidate the measurement. Keep measurement harnesses outside the scanned tree.
