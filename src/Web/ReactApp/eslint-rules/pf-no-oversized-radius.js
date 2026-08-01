/**
 * ESLint rule: pf-no-oversized-radius
 *
 * DESIGN-LANGUAGE.md, "Border Radii":
 *   "never exceed --pf-radius-lg for rectangular surfaces.
 *    The only fully-rounded shapes are avatars and dots."
 *
 * The operative phrase is *rectangular surfaces*. The same document sanctions
 * `--pf-radius-full` for avatars and circular icon buttons (Border Radii table),
 * tag chips (Badges / Status Pills) and progress bars (Progress Bars). So this
 * rule does not treat `rounded-full` as wrong on sight -- it treats it as wrong
 * on a box that holds content, which is the "bubble button" this rule exists to
 * stop.
 *
 * Two families are reported:
 *
 *   1. Over the ceiling -- any named size above `maxPx` (`rounded-xl` at 12px,
 *      `rounded-2xl` at 16px, `rounded-3xl` at 24px) and arbitrary values that
 *      resolve above it. These are only ever applied to rectangular surfaces,
 *      so they are unconditionally wrong. Named sizes map onto the scale
 *      deterministically and are auto-fixable to `rounded-lg`; arbitrary values
 *      only get a suggestion, because the right replacement may be `md` or `sm`
 *      and only a human knows which.
 *
 *   2. Fully round -- `rounded-full` where the element is not demonstrably a
 *      circle and carries no explicit waiver.
 *
 * An element escapes (2) by being provably circular, which costs no code churn:
 *   - matching explicit width and height (`w-8 h-8`, `h-2.5 w-2.5`, `w-full h-full`)
 *   - `size-*` (Tailwind sets both axes)
 *   - `aspect-square`
 *   - `animate-spin` / `animate-ping` / `pf-animate-spin` (spinners and ripples)
 *
 * ...or by carrying an explicit marker attribute, which keeps the waiver visible
 * and greppable at the call site rather than buried in a config file's path
 * exemption list:
 *
 *   <span data-pf-radius="full" className="px-3 py-1 rounded-full">tag</span>
 *
 * The existing `data-pf-progress-track` / `data-pf-progress-fill` markers are
 * honoured too, so progress bars need no second annotation.
 *
 * Variants are understood, so `md:rounded-2xl`, `hover:rounded-full` and
 * `[&::-webkit-slider-thumb]:rounded-full` are all matched on their base token.
 */

const MAX_RADIUS_PX = 8 // --pf-radius-lg

/**
 * Tailwind's radius scale in pixels. The project defines --pf-radius-* in
 * base.css but does not remap Tailwind's --radius-* theme vars, so utilities
 * resolve to these stock values. xs/sm/md/lg happen to line up exactly with
 * --pf-radius-xs/sm/md/lg, which is why the Quick Reference card in
 * DESIGN-LANGUAGE.md lists them as equivalent.
 */
const NAMED_RADII = {
  none: 0,
  xs: 2,
  sm: 4,
  '': 4, // bare `rounded`
  md: 6,
  lg: 8,
  xl: 12,
  '2xl': 16,
  '3xl': 24,
  '4xl': 32,
}

const SIDES = new Set(['t', 'r', 'b', 'l', 's', 'e', 'tl', 'tr', 'br', 'bl', 'ss', 'se', 'ee', 'es'])

/**
 * Split a `rounded*` utility into its size, or return null if it is not one.
 * Handles the optional side segment explicitly rather than leaning on regex
 * backtracking, so `rounded-l` (left side, default radius) and `rounded-lg`
 * (large radius, all sides) cannot be confused.
 */
function parseRoundedSize(base) {
  if (base !== 'rounded' && !base.startsWith('rounded-')) return null
  if (base === 'rounded') return ''

  let size = base.slice('rounded-'.length)
  const dash = size.indexOf('-')
  if (dash !== -1 && SIDES.has(size.slice(0, dash))) {
    size = size.slice(dash + 1)
  } else if (SIDES.has(size)) {
    return '' // e.g. `rounded-l`: a side with the default radius
  }
  return size
}

const WAIVER_ATTRIBUTES = new Set([
  'data-pf-radius',
  'data-pf-progress-track',
  'data-pf-progress-fill',
])

/** Strip Tailwind variant prefixes: `md:`, `hover:`, `[&::-webkit-slider-thumb]:`. */
function stripVariants(token) {
  let rest = token
  for (;;) {
    const arbitrary = /^\[[^\]]*\]:/.exec(rest)
    if (arbitrary) {
      rest = rest.slice(arbitrary[0].length)
      continue
    }
    const named = /^[\w-]+:/.exec(rest)
    if (named) {
      rest = rest.slice(named[0].length)
      continue
    }
    return rest
  }
}

/**
 * Resolve an arbitrary radius like `[1.75rem]` to pixels.
 * Returns `Infinity` for values that mean "fully round", and `null` when the
 * value cannot be resolved (`var()`, `calc()`), in which case nothing is
 * reported -- an unprovable violation is not a violation.
 */
function arbitraryToPx(value) {
  const inner = value.slice(1, -1).trim()
  if (/^(?:9999(?:px|rem)|100%|50%|100vmax)$/.test(inner)) return Infinity
  const match = /^(\d*\.?\d+)(px|rem|em)$/.exec(inner)
  if (!match) return null
  const scalar = Number.parseFloat(match[1])
  return match[2] === 'px' ? scalar : scalar * 16
}

/** True when the flattened class text proves the element renders as a circle. */
function looksCircular(classText) {
  const widths = new Set()
  const heights = new Set()

  for (const raw of classText.split(/\s+/)) {
    if (!raw) continue
    // Deliberately NOT `stripVariants`. A circle has to be unconditional to
    // justify `rounded-full`: `md:aspect-square` is a rectangle below `md`, and
    // exempting it there is exactly the bug this rule exists to catch.
    const token = raw

    if (token === 'aspect-square') return true
    if (/^size-\S+$/.test(token)) return true
    if (token === 'animate-spin' || token === 'animate-ping' || token === 'pf-animate-spin') {
      return true
    }

    const width = /^w-(\S+)$/.exec(token)
    if (width) widths.add(width[1])
    const height = /^h-(\S+)$/.exec(token)
    if (height) heights.add(height[1])
  }

  for (const value of widths) {
    if (heights.has(value)) return true
  }
  return false
}

/** Collect every string literal and template quasi beneath a className value. */
function collectStringNodes(node, found = []) {
  if (!node || typeof node !== 'object') return found

  if (node.type === 'Literal' && typeof node.value === 'string') {
    found.push({ node, text: node.value, quasi: null })
    return found
  }
  if (node.type === 'TemplateLiteral') {
    for (const quasi of node.quasis) {
      found.push({ node: quasi, text: quasi.value.cooked ?? '', quasi })
    }
    for (const expression of node.expressions) collectStringNodes(expression, found)
    return found
  }

  for (const key of Object.keys(node)) {
    if (key === 'parent' || key === 'loc' || key === 'range') continue
    const child = node[key]
    if (Array.isArray(child)) {
      for (const entry of child) collectStringNodes(entry, found)
    } else if (child && typeof child === 'object' && typeof child.type === 'string') {
      collectStringNodes(child, found)
    }
  }
  return found
}

/**
 * True when the element carries a real waiver.
 *
 * The attribute's *presence* is not enough. `data-pf-radius="sm"` and
 * `data-pf-radius={false}` read as "this element is deliberately not full
 * radius", yet a name-only check treated both as permission to be full — so the
 * clearest way to say no was also the way to opt out of the rule. A waiver has
 * to actually assert the pill shape.
 */
function hasWaiverAttribute(openingElement) {
  return Boolean(
    openingElement?.attributes?.some(attr => {
      if (attr.type !== 'JSXAttribute') return false
      if (!WAIVER_ATTRIBUTES.has(attr.name?.name)) return false

      const value = attr.value
      // Bare `data-pf-progress-track` — a boolean-ish marker, and the only
      // sensible reading of it is "yes".
      if (value === null || value === undefined) return true
      if (value.type === 'Literal') return value.value === 'full' || value.value === true
      if (value.type === 'JSXExpressionContainer') {
        const inner = value.expression
        if (inner?.type === 'Literal') return inner.value === 'full' || inner.value === true
        // A computed waiver cannot be judged statically. Honour it rather than
        // reporting a line the author may have already reasoned about.
        return true
      }
      return false
    }),
  )
}

export default {
  meta: {
    type: 'problem',
    docs: {
      description:
        'Keep border radii within --pf-radius-lg (8px) on rectangular surfaces, per DESIGN-LANGUAGE.md',
      recommended: true,
      url: 'file:///src/Web/ReactApp/src/design-system/DESIGN-LANGUAGE.md',
    },
    fixable: 'code',
    hasSuggestions: true,
    messages: {
      oversized:
        '"{{token}}" is {{px}}px. DESIGN-LANGUAGE caps rectangular surfaces at --pf-radius-lg (8px): use rounded-lg, rounded-md (cards) or rounded-sm (controls).',
      fullRound:
        '"{{token}}" makes a rectangular surface into a pill. DESIGN-LANGUAGE reserves --pf-radius-full for avatars, circular icon buttons, dots, tag chips and progress bars. Give the element equal width and height (or size-*/aspect-square) if it is a circle, otherwise use rounded-lg. If a pill is genuinely intended, declare it with data-pf-radius="full".',
      replaceWithLg: 'Replace "{{token}}" with "{{replacement}}".',
    },
    schema: [
      {
        type: 'object',
        properties: {
          maxPx: { type: 'number' },
          checkFullRound: { type: 'boolean' },
        },
        additionalProperties: false,
      },
    ],
  },

  create(context) {
    const options = context.options[0] ?? {}
    const maxPx = typeof options.maxPx === 'number' ? options.maxPx : MAX_RADIUS_PX
    const checkFullRound = options.checkFullRound !== false

    /** Build the replacement token, preserving variants and side. */
    function toLargeToken(rawToken) {
      return rawToken.replace(/-(?:4xl|3xl|2xl|xl|full|\[[^\]]+\])$/, '-lg')
    }

    function reportToken({ stringNode, quasi, rawToken, offset, messageId, data, autofix }) {
      // A template quasi's own range includes the backtick or `${`, so the raw
      // text starts one character in. Literals carry their quote at index 0 too.
      const base = stringNode.range[0] + 1
      const start = base + offset
      const range = [start, start + rawToken.length]
      const replacement = toLargeToken(rawToken)

      const descriptor = {
        node: quasi ?? stringNode,
        loc: {
          start: context.sourceCode.getLocFromIndex(start),
          end: context.sourceCode.getLocFromIndex(range[1]),
        },
        messageId,
        data,
      }

      if (autofix) {
        descriptor.fix = fixer => fixer.replaceTextRange(range, replacement)
      } else {
        descriptor.suggest = [
          {
            messageId: 'replaceWithLg',
            data: { token: rawToken, replacement },
            fix: fixer => fixer.replaceTextRange(range, replacement),
          },
        ]
      }

      context.report(descriptor)
    }

    return {
      JSXAttribute(node) {
        if (node.name.name !== 'className' && node.name.name !== 'class') return
        if (!node.value) return

        const strings = collectStringNodes(
          node.value.type === 'JSXExpressionContainer' ? node.value.expression : node.value,
        )
        if (strings.length === 0) return

        // The shape evidence may live in a sibling fragment of the same
        // clsx()/template call, so judge circularity against the whole element.
        const classText = strings.map(entry => entry.text).join(' ')
        const circular = looksCircular(classText)
        const waived = hasWaiverAttribute(node.parent)

        for (const { node: stringNode, text, quasi } of strings) {
          for (const match of text.matchAll(/\S+/g)) {
            const rawToken = match[0]
            const size = parseRoundedSize(stripVariants(rawToken))
            if (size === null) continue

            const reportFullRound = () => {
              if (!checkFullRound || circular || waived) return
              reportToken({
                stringNode,
                quasi,
                rawToken,
                offset: match.index,
                messageId: 'fullRound',
                data: { token: rawToken },
                autofix: false,
              })
            }

            if (size === 'full') {
              reportFullRound()
              continue
            }

            const isArbitrary = size.startsWith('[') && size.endsWith(']')
            const px = isArbitrary ? arbitraryToPx(size) : NAMED_RADII[size]

            // An unresolvable value (var(), calc()) or an unrecognised size is
            // not reported: a violation that cannot be proven is not a violation.
            if (px === null || px === undefined) continue

            if (px === Infinity) {
              reportFullRound()
              continue
            }

            if (px > maxPx) {
              reportToken({
                stringNode,
                quasi,
                rawToken,
                offset: match.index,
                messageId: 'oversized',
                data: { token: rawToken, px: Math.round(px * 100) / 100 },
                // Named sizes map onto the scale deterministically, so they are
                // safe to rewrite. An arbitrary value may have wanted `md` or
                // `sm`, so it only gets a suggestion a human has to accept.
                autofix: !isArbitrary,
              })
            }
          }
        }
      },
    }
  },
}
