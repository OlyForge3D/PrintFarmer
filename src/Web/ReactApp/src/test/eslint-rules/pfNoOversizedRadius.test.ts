import { RuleTester } from 'eslint'
import { describe, it } from 'vitest'
import rule from '../../../eslint-rules/pf-no-oversized-radius.js'

// RuleTester drives describe/it itself; vitest supplies them.
RuleTester.describe = describe
RuleTester.it = it

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
    // Unresolvable arbitrary values are never guessed at.
    { code: '<div className="rounded-[var(--pf-radius-md)]" />' },
    { code: '<div className="rounded-[calc(2px+2px)]" />' },

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
  ],
})
