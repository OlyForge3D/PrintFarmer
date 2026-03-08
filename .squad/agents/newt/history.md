# Newt — History

## Project Context

**PrintFarmer** — React TypeScript dashboard for managing multiple 3D printers. C# .NET 10 API backend, React 19 frontend with Tailwind CSS v4, SignalR real-time updates. Owner: Jeff Papiez.

**Stack:** Tailwind CSS v4 with custom `pf-` design tokens, shared UI component library at `@/common/components/ui`, MDI icons, `clsx` for class composition, `sonner` for toasts.

## Learnings

### Systematic Token Sweep (2026-03-11)
- **978 token replacements** across **117 files** — every hardcoded Tailwind color class in `features/`, `components/`, `common/`, `services/`, `types/` replaced with semantic `pf-*` design tokens
- **Mapping rules established:**
  - `text-red-*` → `text-pf-error`, `bg-red-*` (tinted) → `bg-pf-error/10`, `bg-red-*` (solid) → `bg-pf-error`
  - `text-green-*` / `text-emerald-*` → `text-pf-success`, `bg-green-*` → `bg-pf-success` / `bg-pf-success/10`
  - `text-blue-*` → `text-pf-accent`, `bg-blue-*` → `bg-pf-accent-bg` / `bg-pf-accent-bg/15`
  - `text-yellow-*` / `text-amber-*` / `text-orange-*` → `text-pf-warning`, `bg-*` → `bg-pf-warning/10`
  - `text-gray-300-400` → `text-pf-text-secondary/tertiary`, `text-gray-700-900` → `text-pf-text-primary`
  - `bg-gray-100-200` → `bg-pf-bg-1/2`, `bg-gray-800-900` → `bg-pf-bg-0/1`
  - `border-gray-*` → `border-pf-border`, `border-red-*` → `border-pf-error(/30)`
  - `bg-slate-400` → `bg-pf-disabled`
  - Purple/indigo/teal/cyan → nearest semantic token (`pf-accent` or `pf-success`)
- **dark: variants removed entirely** — pf-* tokens handle theme switching via CSS custom properties, making `dark:text-gray-400`, `dark:bg-red-900/20` etc. redundant
- **Intentionally excluded:**
  - `colorFamilies.ts` — literal filament swatch colors (12 references) that represent actual material colors, not UI chrome
  - `bg-black/50` overlays — standard backdrop dimming pattern, not a design token concern
  - `text-white` — kept for contrast on accent/solid-color buttons
- **Lesson: NEVER apply `re.sub(r'  +', ' ', content)` to entire file content** — it destroys indentation. First pass had this bug, corrupted 472 files. Caught and reverted immediately. Fixed script to only modify class token strings, not whitespace.
- **Lesson: Two-pass approach works well for large sweeps** — Pass 1 handles common patterns (628 matches), Pass 2 handles edge cases (75 matches with uncommon shades like `bg-red-950/30`, `text-emerald-300/80`, `from-purple-500 to-pink-500`)
- **Validation:** 1,233/1,233 tests pass, 0 lint errors, bead PFarm1-xsg closed
