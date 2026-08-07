import { RuleTester } from 'eslint'
import { describe, it } from 'vitest'
import rule from '../../../eslint-rules/pf-no-oversized-radius.js'

// RuleTester drives describe/it itself; vitest supplies them.
RuleTester.describe = describe
RuleTester.it = it

const OUT_OF_RANGE_CSS_ESCAPE = '\\110000'
const ESCAPED_BANG = '\\!'
const CSS_COMMENT = '/*c*/'
const OPEN_CSS_COMMENT = '/*c'

const ruleTester = new RuleTester({
  languageOptions: {
    ecmaVersion: 2022,
    sourceType: 'module',
    parserOptions: { ecmaFeatures: { jsx: true } },
  },
})

ruleTester.run('pf-no-oversized-radius', rule, {
  valid: [
    // Radii at or below --pf-radius-lg.
    { code: '<div className="rounded-md border" />' },
    { code: '<div className="rounded-lg p-4" />' },
    { code: '<div className="rounded-sm" />' },
    { code: '<div className="rounded" />' },
    // Side segments carry the default radius and must not be read as sizes.
    { code: '<div className="rounded-l" />' },
    { code: '<div className="rounded-t-md" />' },
    // An unrecognised size is never guessed at.
    { code: '<div className="rounded-bananas" />' },
    // Arbitrary values within the ceiling.
    { code: '<div className="rounded-[4px]" />' },
    { code: '<div className="rounded-[0.5rem]" />' },
    { code: '<div className="rounded-[length:4px]" />' },
    { code: '<div className="[border-radius:8px]" />' },
    { code: '<div className="[border-radius:0.5rem]" />' },
    // Unresolvable arbitrary values are never guessed at.
    { code: '<div className="rounded-[var(--pf-radius-md)]" />' },
    { code: '<div className="rounded-[calc(2px+2px)]" />' },
    { code: '<div className="[border-radius:var(--pf-radius-md)]" />' },
    // A comment before the hint prevents Tailwind from treating it as a hint.
    { code: `<div className="rounded-[${CSS_COMMENT}length:16px]" />` },
    // Negative radii are invalid CSS and therefore do not render oversized.
    { code: '<div className="rounded-[-16px]" />' },
    // Inline importance is read after comments and preserves the winning square.
    {
      code: `<div className="size-[16px${CSS_COMMENT}!important] h-[32px] rounded-full" />`,
    },
    {
      code: `<div className="size-[var(--x${ESCAPED_BANG}y,16px)] rounded-full" />`,
    },
    // A dimension-side escaping comment swallows the later radius declaration.
    {
      code: `<div className="w-[64px${OPEN_CSS_COMMENT}] rounded-[9999px] h-[32px]" />`,
    },
    // Competing radius escapers have ambiguous generated candidate order, so
    // neither can be judged safely.
    {
      code: `<div className="rounded-[9999px${OPEN_CSS_COMMENT}] rounded-[8px${OPEN_CSS_COMMENT}] w-[64px] h-[32px]" />`,
    },

    // Provably circular elements keep rounded-full with no annotation.
    { code: '<span className="w-2 h-2 rounded-full bg-pf-error" />' },
    { code: '<span className="h-2.5 w-2.5 rounded-full" />' },
    { code: '<div className="size-8 rounded-full" />' },
    { code: '<div className="aspect-square rounded-full" />' },
    { code: '<div className="animate-spin rounded-full h-5 w-5 border-b-2" />' },
    { code: '<div className="pf-animate-spin rounded-full h-12 w-12" />' },
    { code: '<span className="animate-ping absolute h-full w-full rounded-full" />' },

    // Shape evidence is matched within its own variant scope, so a scoped
    // radius is judged against the evidence that applies where it applies.
    // Real slider thumbs look exactly like this (Slider.tsx, SettingRow.tsx).
    {
      code: '<input className="[&::-webkit-slider-thumb]:w-4 [&::-webkit-slider-thumb]:h-4 [&::-webkit-slider-thumb]:rounded-full" />',
    },
    {
      code: '<input className="[&::-moz-range-thumb]:w-4 [&::-moz-range-thumb]:h-4 [&::-moz-range-thumb]:rounded-full" />',
    },
    {
      code: '<input className="[&::-webkit-slider-thumb]:w-5 [&::-webkit-slider-thumb]:h-5 [&::-webkit-slider-thumb]:rounded-full" />',
    },
    {
      code: '<input className="[&::-moz-range-thumb]:w-5 [&::-moz-range-thumb]:h-5 [&::-moz-range-thumb]:rounded-full" />',
    },
    // State placement around a selector scope is preserved. Same-scope evidence
    // still proves the descendant itself is circular.
    {
      code: '<div className="[&_img]:hover:w-4 [&_img]:hover:h-4 [&_img]:hover:rounded-full" />',
    },
    // An unconditional square is square in every scope, so unprefixed evidence
    // excuses a scoped radius.
    { code: '<div className="aspect-square md:rounded-full" />' },
    // Same scope, so both apply together or neither does.
    { code: '<div className="md:aspect-square md:rounded-full" />' },
    // A state selector has greater specificity than a media-only selector.
    {
      code: '<div className="hover:aspect-square md:aspect-video md:hover:rounded-full" />',
    },
    // Media-only conditions add no selector specificity.
    {
      code: '<div className="hover:aspect-square dark:aspect-video dark:hover:rounded-full" />',
    },
    // A custom-property dimension cannot be resolved statically. Withholding a
    // report avoids condemning a value that may resolve to the matching axis.
    { code: '<div className="w-[var(--avatar-size)] h-8 rounded-full" />' },
    // Tailwind emits static dimension candidates in value order, independent of
    // their order in the class attribute.
    { code: '<div className="h-8 w-8 w-4 rounded-full" />' },
    { code: '<div className="h-8 w-4 w-8 rounded-full" />' },
    { code: '<div className="h-min w-min w-auto rounded-full" />' },
    { code: '<div className="h-min w-auto w-min rounded-full" />' },
    // Mutually exclusive states must never be merged into an impossible cascade.
    {
      code: '<div className="h-8 enabled:w-8 disabled:w-4 enabled:rounded-full" />',
    },
    // A later all-corner radius removes rounded-full completely.
    { code: '<div className="rounded-full rounded-lg" />' },
    // Same-condition radius and aspect candidate order comes from Tailwind's
    // generated stylesheet, not class text. Unknown ties are not guessed.
    { code: '<div className="rounded-lg rounded-full" />' },
    { code: '<div className="aspect-video aspect-square rounded-full" />' },
    { code: '<div className="aspect-square aspect-video rounded-full" />' },
    // Cross-family dimension candidate order is likewise conservative.
    { code: '<div className="h-8 w-1/2 w-8 rounded-full" />' },
    // Selector variants whose payload changes specificity are not assigned a
    // guessed Tailwind source position.
    {
      code: '<div className="h-8 hover:w-8 data-[active]:w-4 hover:data-[active]:rounded-full" />',
    },
    {
      code: '<div className="h-8 hover:w-8 aria-[expanded=true]:w-4 hover:aria-[expanded=true]:rounded-full" />',
    },
    {
      code: '<div className="h-8 hover:w-8 has-[.active]:w-4 hover:has-[.active]:rounded-full" />',
    },
    // Different media families have no selector specificity and their relative
    // generated order is not inferred.
    {
      code: '<div className="md:aspect-video dark:aspect-square md:dark:rounded-full" />',
    },
    // Combinators inside a functional pseudo-class do not move the target away
    // from the host element.
    { code: '<div className="w-8 h-8 [&:has(>img)]:rounded-full" />' },
    // Legacy single-colon pseudo-element spelling remains same-scope when all
    // evidence targets that pseudo-element.
    {
      code: '<div className="[&:before]:w-4 [&:before]:h-4 [&:before]:rounded-full" />',
    },
    // Logical start/end corners map to their physical LTR corners.
    { code: '<div className="rounded-se-full rounded-tr-lg" />' },
    { code: '<div className="rounded-es-full rounded-bl-lg" />' },
    // Keyword candidate order is deliberately ambiguous rather than guessed.
    { code: '<div className="h-full w-fit w-full rounded-full" />' },
    // Tailwind compares stacked variants inside-out. focus:hover is emitted
    // before hover:active, so the latter w-8 wins and preserves the circle.
    {
      code: '<div className="h-8 focus:hover:w-4 hover:active:w-8 hover:focus:active:rounded-full" />',
    },

    // Shape evidence in a sibling fragment of the same clsx() call still counts.
    { code: '<div className={clsx("rounded-full border", "h-6 w-6 shrink-0")} />' },

    // Explicit, greppable waiver at the call site.
    { code: '<span data-pf-radius="full" className="px-3 py-1 rounded-full">tag</span>' },
    // The pre-existing progress markers are honoured without a second annotation.
    { code: '<div data-pf-progress-track className="w-full h-2 rounded-full" />' },

    // rounded-full checking can be switched off for the repo-wide pass.
    {
      code: '<div className="px-4 py-2 rounded-full" />',
      options: [{ checkFullRound: false }],
    },
    // A raised ceiling is honoured.
    {
      code: '<div className="rounded-[12px]" />',
      options: [{ maxPx: 16 }],
    },

    // Attributes other than className are untouched.
    { code: '<div title="rounded-2xl" />' },

    // A computed waiver cannot be judged statically; honour it rather than
    // reporting a line the author may already have reasoned about.
    { code: '<span data-pf-radius={shape} className="px-3 rounded-full">tag</span>' },
  ],

  invalid: [
    // rounded-xl is 12px in Tailwind's stock scale, which the project does not
    // remap -- so it is over the 8px ceiling too.
    {
      code: '<div className="rounded-xl border" />',
      output: '<div className="rounded-lg border" />',
      errors: [{ messageId: 'oversized', data: { token: 'rounded-xl', px: 12 } }],
    },
    // ...but it is legal under a raised ceiling. `maxPx` is what made the
    // staged migration possible, and it is still the knob for any future one.
    {
      code: '<div className="rounded-2xl" />',
      options: [{ maxPx: 12 }],
      output: '<div className="rounded-lg" />',
      errors: [{ messageId: 'oversized', data: { token: 'rounded-2xl', px: 16 } }],
    },
    {
      code: '<div className="rounded-2xl border p-4" />',
      output: '<div className="rounded-lg border p-4" />',
      errors: [{ messageId: 'oversized', data: { token: 'rounded-2xl', px: 16 } }],
    },
    {
      code: '<div className="rounded-3xl" />',
      output: '<div className="rounded-lg" />',
      errors: [{ messageId: 'oversized', data: { token: 'rounded-3xl', px: 24 } }],
    },
    // A side segment plus an oversized size: both parts survive the rewrite.
    {
      code: '<div className="rounded-tl-3xl" />',
      output: '<div className="rounded-tl-lg" />',
      errors: [{ messageId: 'oversized' }],
    },
    // Side-scoped and variant-prefixed tokens are matched on their base.
    {
      code: '<div className="rounded-t-2xl" />',
      output: '<div className="rounded-t-lg" />',
      errors: [{ messageId: 'oversized' }],
    },
    {
      code: '<div className="md:rounded-2xl" />',
      output: '<div className="md:rounded-lg" />',
      errors: [{ messageId: 'oversized' }],
    },
    // Arbitrary values above the ceiling: suggestion, not silent rewrite,
    // because rounded-md may be the better answer and only a human knows.
    {
      code: '<div className="rounded-[1.75rem]" />',
      output: null,
      errors: [
        {
          messageId: 'oversized',
          data: { token: 'rounded-[1.75rem]', px: 28 },
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output: '<div className="rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="rounded-[20px]" />',
      output: null,
      errors: [
        {
          messageId: 'oversized',
          data: { token: 'rounded-[20px]', px: 20 },
          suggestions: [
            { messageId: 'replaceWithLg', output: '<div className="rounded-lg" />' },
          ],
        },
      ],
    },
    // Arbitrary-property syntax emits the same border-radius declaration without
    // using a rounded-* utility, so it is judged through the same value reader.
    {
      code: '<div className="[border-radius:12px]" />',
      output: null,
      errors: [{ messageId: 'oversized', data: { token: '[border-radius:12px]', px: 12 } }],
    },
    {
      code: '<div className="md:[border-radius:1.5rem]" />',
      output: null,
      errors: [{ messageId: 'oversized', data: { token: 'md:[border-radius:1.5rem]', px: 24 } }],
    },
    {
      code: '<div className="[border-radius:9999px]" />',
      output: null,
      errors: [{ messageId: 'fullRound' }],
    },
    // Radius values follow CSS number spelling and comment rules.
    ...[
      '16PX',
      '+16px',
      '_16px_',
      `16px${CSS_COMMENT}`,
      `${CSS_COMMENT}16px`,
      `length:${CSS_COMMENT}16px`,
    ].map(value => ({
      code: `<div className="rounded-[${value}]" />`,
      output: null,
      errors: [{ messageId: 'oversized', suggestions: 1 }],
    })),
    {
      code: '<div className="rounded-[1e1px]" />',
      output: null,
      errors: [
        {
          messageId: 'oversized',
          data: { token: 'rounded-[1e1px]', px: 10 },
          suggestions: 1,
        },
      ],
    },
    // An out-of-range CSS escape is replacement text, not an exception, and the
    // invalid size declaration cannot prove the box is circular.
    {
      code: `<div className="size-[16px!${OUT_OF_RANGE_CSS_ESCAPE}] h-[32px] rounded-full" />`,
      output: null,
      errors: [{ messageId: 'fullRound', suggestions: 1 }],
    },
    // Comment stripping and importance must agree on which axis wins.
    {
      code: `<div className="size-[16px] h-[32px${CSS_COMMENT}!important] rounded-full" />`,
      output: null,
      errors: [{ messageId: 'fullRound', suggestions: 1 }],
    },
    // A lone unterminated comment in the radius token leaves the radius itself
    // live while later declarations are swallowed.
    {
      code: `<div className="rounded-[16px${OPEN_CSS_COMMENT}] w-[64px] h-[32px]" />`,
      output: null,
      errors: [
        {
          messageId: 'oversized',
          data: { token: 'rounded-[16px/*c]', px: 16 },
          suggestions: 1,
        },
      ],
    },
    // Utilities emitted after border-radius cannot swallow a live radius. One or
    // several later comments therefore do not suppress the radius report.
    {
      code: `<div className="rounded-[16px] opacity-[1${OPEN_CSS_COMMENT}]" />`,
      output: null,
      errors: [{ messageId: 'oversized', suggestions: 1 }],
    },
    {
      code: `<div className="rounded-[16px] bg-[red${OPEN_CSS_COMMENT}] opacity-[1${OPEN_CSS_COMMENT}]" />`,
      output: null,
      errors: [{ messageId: 'oversized', suggestions: 1 }],
    },
    // A radius escaper remains live even when another later utility also opens a
    // comment; only the radius token itself is judged.
    {
      code: `<div className="rounded-[16px${OPEN_CSS_COMMENT}] opacity-[1${OPEN_CSS_COMMENT}]" />`,
      output: null,
      errors: [
        {
          messageId: 'oversized',
          data: { token: 'rounded-[16px/*c]', px: 16 },
          suggestions: 1,
        },
      ],
    },
    // A content box with no shape evidence is the "bubble button" case.
    {
      code: '<button className="px-4 py-2 rounded-full text-white" />',
      output: null,
      errors: [
        {
          messageId: 'fullRound',
          data: { token: 'rounded-full' },
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output: '<button className="px-4 py-2 rounded-lg text-white" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="px-2 py-0.5 text-xs rounded-full bg-pf-bg-2" />',
      output: null,
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output: '<div className="px-2 py-0.5 text-xs rounded-lg bg-pf-bg-2" />',
            },
          ],
        },
      ],
    },
    // Mismatched width and height is not a circle.
    {
      code: '<div className="w-full h-2 rounded-full" />',
      output: null,
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            { messageId: 'replaceWithLg', output: '<div className="w-full h-2 rounded-lg" />' },
          ],
        },
      ],
    },
    // 9999px is rounded-full spelled differently.
    {
      code: '<div className="px-3 rounded-[9999px]" />',
      output: null,
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            { messageId: 'replaceWithLg', output: '<div className="px-3 rounded-lg" />' },
          ],
        },
      ],
    },
    // Inside clsx(), reported at the offending fragment.
    {
      code: '<div className={clsx("flex gap-3 rounded-2xl border", isActive && "bg-pf-bg-1")} />',
      output: '<div className={clsx("flex gap-3 rounded-lg border", isActive && "bg-pf-bg-1")} />',
      errors: [{ messageId: 'oversized' }],
    },
    // Inside a template literal.
    {
      code: '<div className={`rounded-2xl ${extra}`} />',
      output: '<div className={`rounded-lg ${extra}`} />',
      errors: [{ messageId: 'oversized' }],
    },
    // Two violations in one attribute are both reported.
    {
      code: '<div className="rounded-2xl md:rounded-3xl" />',
      output: '<div className="rounded-lg md:rounded-lg" />',
      errors: [{ messageId: 'oversized' }, { messageId: 'oversized' }],
    },
    // An unrelated data attribute is not a waiver.
    {
      code: '<div data-testid="chip" className="px-3 py-1 rounded-full" />',
      output: null,
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output: '<div data-testid="chip" className="px-3 py-1 rounded-lg" />',
            },
          ],
        },
      ],
    },
    // A waiver that says "not a pill" must not read as permission to be one.
    // `data-pf-radius="sm"` is the clearest way an author can state the
    // opposite; treating its presence as consent inverted its meaning.
    {
      code: '<div data-pf-radius="sm" className="px-3 py-1 rounded-full" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [{ messageId: 'replaceWithLg', output: '<div data-pf-radius="sm" className="px-3 py-1 rounded-lg" />' }],
        },
      ],
    },
    {
      code: '<div data-pf-radius={false} className="px-3 py-1 rounded-full" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [{ messageId: 'replaceWithLg', output: '<div data-pf-radius={false} className="px-3 py-1 rounded-lg" />' }],
        },
      ],
    },

    // Circularity has to be unconditional. `md:aspect-square` is a rectangle
    // below the breakpoint, so `rounded-full` is wrong there.
    {
      code: '<div className="md:aspect-square rounded-full px-4" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [{ messageId: 'replaceWithLg', output: '<div className="md:aspect-square rounded-lg px-4" />' }],
        },
      ],
    },
    // Evidence is paired by exact variant prefix, so two different states never
    // combine into a circle: `hover:w-4` and `focus:h-4` are not equal axes.
    {
      code: '<div className="hover:w-4 focus:h-4 hover:rounded-full" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            { messageId: 'replaceWithLg', output: '<div className="hover:w-4 focus:h-4 hover:rounded-lg" />' },
          ],
        },
      ],
    },
    {
      code: '<div className="w-6 md:h-6 rounded-full" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [{ messageId: 'replaceWithLg', output: '<div className="w-6 md:h-6 rounded-lg" />' }],
        },
      ],
    },
    // Breakpoints are cumulative: md:w-4 remains active at lg and overrides w-8.
    {
      code: '<div className="w-8 h-8 md:w-4 lg:rounded-full" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output: '<div className="w-8 h-8 md:w-4 lg:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Host dimensions cannot prove the descendant selected by an arbitrary
    // variant is circular.
    {
      code: '<div className="w-8 h-8 [&_img]:rounded-full" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output: '<div className="w-8 h-8 [&_img]:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // These ordered selectors target different conditions and must not share
    // shape evidence.
    {
      code: '<div className="[&_img]:hover:w-4 [&_img]:hover:h-4 hover:[&_img]:rounded-full" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output:
                '<div className="[&_img]:hover:w-4 [&_img]:hover:h-4 hover:[&_img]:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Equal-specificity declarations resolve by their declaration/source order.
    {
      code: '<div className="h-8 hover:w-8 focus:w-4 hover:focus:rounded-full" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output:
                '<div className="h-8 hover:w-8 focus:w-4 hover:focus:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Media conditions add no selector specificity, so hover:aspect-video wins
    // over md:aspect-square while both apply.
    {
      code: '<div className="md:aspect-square hover:aspect-video md:hover:rounded-full" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output:
                '<div className="md:aspect-square hover:aspect-video md:hover:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // A base radius remains active at later breakpoints and in additional
    // states, so later evidence must also be resolved.
    {
      code: '<div className="w-4 h-4 rounded-full md:w-8" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output: '<div className="w-4 h-4 rounded-lg md:w-8" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="w-4 h-4 hover:rounded-full focus:w-8" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output: '<div className="w-4 h-4 hover:rounded-lg focus:w-8" />',
            },
          ],
        },
      ],
    },
    // Tailwind emits focus after hover regardless of class attribute order.
    {
      code: '<div className="h-8 focus:w-4 hover:w-8 hover:focus:rounded-full" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output:
                '<div className="h-8 focus:w-4 hover:w-8 hover:focus:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Axis utilities are emitted after size utilities in Tailwind's utility
    // order, so w-8 wins even when size-4 appears later in the attribute.
    {
      code: '<div className="w-8 size-4 rounded-full" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output: '<div className="w-8 size-4 rounded-lg" />',
            },
          ],
        },
      ],
    },
    // A lower breakpoint remains active at the radius's larger breakpoint.
    {
      code: '<div className="w-4 h-4 lg:rounded-full md:hover:w-8" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output: '<div className="w-4 h-4 lg:rounded-lg md:hover:w-8" />',
            },
          ],
        },
      ],
    },
    // Two individually compatible state declarations can create a lozenge only
    // where both states are active, so their intersection must be evaluated.
    {
      code: '<div className="aspect-square rounded-full hover:w-8 focus:h-4" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output: '<div className="aspect-square rounded-lg hover:w-8 focus:h-4" />',
            },
          ],
        },
      ],
    },
    // Named pseudo-element variants establish their own selector target.
    {
      code: '<input className="w-4 h-4 file:rounded-full" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output: '<input className="w-4 h-4 file:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Legacy single-colon pseudo-elements are distinct from the host element.
    {
      code: '<div className="w-8 h-8 [&:before]:rounded-full" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output: '<div className="w-8 h-8 [&:before]:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // A side-specific override leaves the other rounded-full corners active.
    {
      code: '<div className="rounded-full rounded-t-lg" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output: '<div className="rounded-lg rounded-t-lg" />',
            },
          ],
        },
      ],
    },
    // Reversing the widths makes the later hover:active declaration a lozenge.
    {
      code: '<div className="h-8 focus:hover:w-8 hover:active:w-4 hover:focus:active:rounded-full" />',
      errors: [
        {
          messageId: 'fullRound',
          suggestions: [
            {
              messageId: 'replaceWithLg',
              output:
                '<div className="h-8 focus:hover:w-8 hover:active:w-4 hover:focus:active:rounded-lg" />',
            },
          ],
        },
      ],
    },
  ],
})
