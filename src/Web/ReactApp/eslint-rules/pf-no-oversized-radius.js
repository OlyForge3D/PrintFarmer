/**
 * ESLint rule: pf-no-oversized-radius
 *
 * DESIGN-LANGUAGE.md, "Border Radii":
 *   "never exceed --pf-radius-lg for rectangular surfaces. Fully-rounded is
 *    reserved for shapes that are circular by nature (avatars, dots, circular
 *    icon buttons) plus the pill-shaped exceptions listed below."
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
const ALL_CORNERS = ['tl', 'tr', 'br', 'bl']
const CORNERS_BY_SIDE = new Map([
  ['t', ['tl', 'tr']],
  ['r', ['tr', 'br']],
  ['e', ['tr', 'br']],
  ['b', ['br', 'bl']],
  ['l', ['tl', 'bl']],
  ['s', ['tl', 'bl']],
  ['tl', ['tl']],
  ['tr', ['tr']],
  ['br', ['br']],
  ['bl', ['bl']],
  ['ss', ['tl']],
  ['se', ['tr']],
  ['ee', ['br']],
  ['es', ['bl']],
])

function parseRounded(base) {
  const property = /^\[border-radius:([^\]]+)\]$/.exec(base)
  if (property) return { size: `[${property[1]}]`, corners: ALL_CORNERS }

  if (base !== 'rounded' && !base.startsWith('rounded-')) return null
  if (base === 'rounded') return { size: '', corners: ALL_CORNERS }

  let size = base.slice('rounded-'.length)
  const dash = size.indexOf('-')
  if (dash !== -1 && SIDES.has(size.slice(0, dash))) {
    const side = size.slice(0, dash)
    size = size.slice(dash + 1)
    return { size, corners: CORNERS_BY_SIDE.get(side) }
  } else if (SIDES.has(size)) {
    return { size: '', corners: CORNERS_BY_SIDE.get(size) }
  }
  return { size, corners: ALL_CORNERS }
}

function parseRoundedSize(base) {
  return parseRounded(base)?.size ?? null
}

const WAIVER_ATTRIBUTES = new Set([
  'data-pf-radius',
  'data-pf-progress-track',
  'data-pf-progress-fill',
])

const BREAKPOINTS = new Map([
  ['sm', 1],
  ['md', 2],
  ['lg', 3],
  ['xl', 4],
  ['2xl', 5],
])

const MEDIA_VARIANTS = new Map([
  ['dark', 1],
  ['motion-safe', 2],
  ['motion-reduce', 3],
  ['contrast-more', 4],
  ['contrast-less', 5],
  ['portrait', 6],
  ['landscape', 7],
  ['print', 8],
  ['forced-colors', 9],
])

const STATE_SOURCE_ORDER = new Map([
  ['focus-within', 1],
  ['hover', 2],
  ['focus', 3],
  ['focus-visible', 4],
  ['active', 5],
  ['enabled', 6],
  ['disabled', 7],
])

const PSEUDO_ELEMENT_VARIANTS = new Set([
  'after',
  'backdrop',
  'before',
  'file',
  'first-letter',
  'first-line',
  'marker',
  'placeholder',
  'selection',
])

const LEGACY_PSEUDO_ELEMENTS = new Set(['after', 'before', 'first-letter', 'first-line'])
const IMPORTANT_TAIL = /^[\s_]*(?:\\(?:[0-9a-fA-F]{1,6}[\s_]?|[^])|[^\\\s_])+[\s_]*$/
const CSS_ESCAPE = /\\(?:([0-9a-fA-F]{1,6})[\s_]?|([^]))/g
const stripCssComments = text => text.replace(/\/\*[^]*?(?:\*\/|$)/g, ' ')

function decodeCssEscapes(text) {
  return text.replace(CSS_ESCAPE, (_whole, hex, literal) => {
    if (hex === undefined) return literal
    const point = Number.parseInt(hex, 16)
    if (point === 0 || point > 0x10ffff || (point >= 0xd800 && point <= 0xdfff)) {
      return '\uFFFD'
    }
    return String.fromCodePoint(point)
  })
}

function arbitraryImportance(utility) {
  if (!utility.endsWith(']')) return { utility, important: false }
  const clean = stripCssComments(utility)
  if (!clean.endsWith(']')) return { utility, important: false }

  let bang = -1
  for (let index = 0; index < clean.length; index += 1) {
    if (clean[index] === '\\') {
      index += 1
      continue
    }
    if (clean[index] === '!') bang = index
  }
  if (bang < 0) return { utility, important: false }

  const tail = clean.slice(bang + 1, -1)
  if (!IMPORTANT_TAIL.test(tail)) return { utility, important: false }
  if (decodeCssEscapes(tail).trim().replace(/^_+|_+$/g, '').toLowerCase() !== 'important') {
    return { utility, important: false }
  }

  const open = clean.indexOf('[')
  const head = clean.slice(open + 1, bang).trim()
  return { utility: `${clean.slice(0, open + 1)}${head}]`, important: true }
}

/**
 * Split a utility into its ordered variants and base token. Colons inside
 * arbitrary variants are selector syntax, not separators.
 */
function splitToken(token) {
  const parts = []
  let depth = 0
  let start = 0

  for (let index = 0; index < token.length; index += 1) {
    const character = token[index]
    if (character === '[') depth += 1
    if (character === ']') depth = Math.max(0, depth - 1)
    if (character === ':' && depth === 0) {
      parts.push(token.slice(start, index))
      start = index + 1
    }
  }

  parts.push(token.slice(start))
  const marked = parts.at(-1) ?? ''
  const stripped = marked.replace(/^!+/, '').replace(/!+$/, '')
  const inner = arbitraryImportance(stripped)
  return {
    variants: parts.slice(0, -1),
    base: inner.utility,
    important: marked !== stripped || inner.important,
  }
}

/**
 * Resolve an arbitrary radius like `[1.75rem]` to pixels.
 * Returns `Infinity` for values that mean "fully round", and `null` when the
 * value cannot be resolved (`var()`, `calc()`), in which case nothing is
 * reported -- an unprovable violation is not a violation.
 */
function arbitraryToPx(value) {
  const hinted = value.slice(1, -1).replace(/^[a-z-]+:(?!:)/i, '')
  const inner = stripCssComments(hinted).replace(/_/g, ' ').trim()
  if (/^(?:9999(?:px|rem)|100%|50%|100vmax)$/i.test(inner)) return Infinity
  const match = /^\+?(\d*\.?\d+(?:e[+-]?\d+)?)(px|rem|em)$/i.exec(inner)
  if (!match) return null
  const scalar = Number.parseFloat(match[1])
  return match[2].toLowerCase() === 'px' ? scalar : scalar * 16
}

function hasUnescapedBang(value) {
  for (let index = 0; index < value.length; index += 1) {
    if (value[index] === '\\') {
      index += 1
      continue
    }
    if (value[index] === '!') return true
  }
  return false
}

function normalizeDimension(value) {
  if (!value.startsWith('[') || !value.endsWith(']')) return value
  const hinted = value.slice(1, -1).replace(/^[a-z-]+:(?!:)/i, '')
  const inner = stripCssComments(hinted).replace(/_/g, ' ').trim()
  if (hasUnescapedBang(inner)) return undefined

  const match = /^\+?(\d*\.?\d+(?:e[+-]?\d+)?)(px|rem|em)$/i.exec(inner)
  if (!match) return value
  const scalar = Number.parseFloat(match[1])
  const px = match[2].toLowerCase() === 'px' ? scalar : scalar * 16
  return `[${px}px]`
}

function hasEscapingComment(rawCandidate) {
  let quote = null
  for (let index = 0; index < rawCandidate.length; index += 1) {
    const character = rawCandidate[index]
    if (character === '\\') {
      index += 1
      continue
    }
    if (quote) {
      if (character === quote) quote = null
      continue
    }
    if (character === '"' || character === "'") {
      quote = character
      continue
    }
    if (character === '/' && rawCandidate[index + 1] === '*') {
      const close = rawCandidate.indexOf('*/', index + 2)
      if (close === -1) return true
      index = close + 1
    }
  }
  return false
}

function escapingCommentOrder(rawCandidate) {
  const { base } = splitToken(rawCandidate)
  if (parseRounded(base)) return 'radius'
  if (/^(?:size|w|h|aspect)-/.test(base)) return 'before'
  if (/^(?:bg|opacity|shadow|text)-/.test(base)) return 'after'

  const property = /^\[([^:]+):/.exec(base)?.[1]
  if (/^(?:width|height|aspect-ratio)$/.test(property)) return 'before'
  if (/^(?:background|background-color|color|opacity|box-shadow|text-shadow)$/.test(property)) {
    return 'after'
  }
  return 'unknown'
}

function escapingCommentMayPrecede(escaper, radiusVariants) {
  const escaperVariants = escaper.variants
  if (escaperVariants.length === 0 && radiusVariants.length > 0) return true
  if (escaperVariants.length > 0 && radiusVariants.length === 0) return false
  if (
    escaperVariants.length !== radiusVariants.length ||
    escaperVariants.some((variant, index) => variant !== radiusVariants[index])
  ) {
    return undefined
  }
  if (escaper.order === 'before') return true
  if (escaper.order === 'after') return false
  return undefined
}

function changesSelectorScope(variant) {
  if (PSEUDO_ELEMENT_VARIANTS.has(variant) || variant === '*' || variant === '**') {
    return true
  }
  if (!variant.startsWith('[') || !variant.endsWith(']')) return false

  const selector = variant.slice(1, -1)
  const subject = selector.indexOf('&')
  if (subject === -1) return false

  let parentheses = 0
  let brackets = 0
  for (let index = subject + 1; index < selector.length; index += 1) {
    const character = selector[index]
    if (character === '\\') {
      index += 1
      continue
    }
    if (character === '(') parentheses += 1
    else if (character === ')') parentheses = Math.max(0, parentheses - 1)
    else if (character === '[') brackets += 1
    else if (character === ']') brackets = Math.max(0, brackets - 1)
    else if (parentheses === 0 && brackets === 0) {
      if (character === ':' && selector[index + 1] === ':') return true
      if (character === ':') {
        const pseudo = /^:([\w-]+)/.exec(selector.slice(index))?.[1]
        if (pseudo && LEGACY_PSEUDO_ELEMENTS.has(pseudo)) return true
      }
      if (/[>+~_\s]/.test(character)) return true
    }
  }
  return false
}

/**
 * Parse the independent parts of a Tailwind condition.
 *
 * Breakpoints are cumulative media conditions. Scope-changing variants identify
 * the element being styled. State variants remain ordered in the zones before,
 * between and after scope changes; this keeps `[&_img]:hover:` distinct from
 * `hover:[&_img]:` without preventing `hover:` evidence from applying under
 * `hover:focus:`.
 */
function parseCondition(variants) {
  const scope = []
  const stateZones = [[]]
  const media = []
  let breakpoint = 0

  for (const variant of variants) {
    const breakpointOrder = BREAKPOINTS.get(variant)
    if (breakpointOrder !== undefined) {
      breakpoint = Math.max(breakpoint, breakpointOrder)
      continue
    }
    if (MEDIA_VARIANTS.has(variant)) {
      media.push(variant)
      continue
    }
    if (changesSelectorScope(variant)) {
      scope.push(variant)
      stateZones.push([])
      continue
    }
    stateZones.at(-1).push(variant)
  }

  return {
    scopeKey: scope.join('\u0000'),
    stateZones,
    media,
    breakpoint,
    specificity: stateZones.reduce((total, zone) => total + zone.length, 0),
    specificityReliable: stateZones
      .flat()
      .every(variant => STATE_SOURCE_ORDER.has(variant)),
    variantOrder: stateZones
      .flat()
      .map(variant => STATE_SOURCE_ORDER.get(variant) ?? 0),
    mediaOrder: Math.max(0, ...media.map(variant => MEDIA_VARIANTS.get(variant) ?? 0)),
  }
}

function conditionSubset(required, active) {
  const available = new Map()
  for (const variant of active) available.set(variant, (available.get(variant) ?? 0) + 1)
  for (const variant of required) {
    const count = available.get(variant) ?? 0
    if (count === 0) return false
    available.set(variant, count - 1)
  }
  return true
}

function conditionApplies(condition, target) {
  if (condition.scopeKey !== target.scopeKey || condition.breakpoint > target.breakpoint) {
    return false
  }
  if (!conditionSubset(condition.media, target.media)) return false
  return condition.stateZones.every((zone, index) =>
    conditionSubset(zone, target.stateZones[index] ?? []),
  )
}

function conditionsConflict(left, right) {
  const variants = new Set([...left, ...right])
  if (
    [
      ['enabled', 'disabled'],
      ['optional', 'required'],
      ['valid', 'invalid'],
      ['in-range', 'out-of-range'],
      ['read-only', 'read-write'],
      ['motion-safe', 'motion-reduce'],
      ['portrait', 'landscape'],
      ['contrast-more', 'contrast-less'],
    ].some(group => group.every(variant => variants.has(variant)))
  ) {
    return true
  }
  return [...variants].some(variant => {
    if (variant.startsWith('not-')) return variants.has(variant.slice(4))
    return variants.has(`not-${variant}`)
  })
}

function mergeConditions(base, extension) {
  if (base.scopeKey !== extension.scopeKey) return undefined
  const stateZones = base.stateZones.map((zone, index) => {
    const other = extension.stateZones[index] ?? []
    if (conditionsConflict(zone, other)) return undefined
    return [...zone, ...other.filter(variant => !zone.includes(variant))]
  })
  if (stateZones.some(zone => zone === undefined)) return undefined
  const media = [...base.media, ...extension.media.filter(variant => !base.media.includes(variant))]
  if (conditionsConflict(base.media, extension.media)) return undefined

  return {
    scopeKey: base.scopeKey,
    stateZones,
    media,
    breakpoint: Math.max(base.breakpoint, extension.breakpoint),
    specificity: stateZones.reduce((total, zone) => total + zone.length, 0),
    variantOrder: stateZones
      .flat()
      .map(variant => STATE_SOURCE_ORDER.get(variant) ?? 0),
    mediaOrder: Math.max(base.mediaOrder, extension.mediaOrder),
  }
}

function sameConditionPart(left, right) {
  return JSON.stringify(left) === JSON.stringify(right)
}

function compareOrderLists(left, right) {
  const length = Math.max(left.length, right.length)
  for (let offset = 1; offset <= length; offset += 1) {
    const leftValue = left.at(-offset) ?? -1
    const rightValue = right.at(-offset) ?? -1
    if (leftValue !== rightValue) return leftValue > rightValue ? 1 : -1
  }
  return 0
}

function comparePrecedence(candidate, winner) {
  if (candidate.important !== winner.important) return candidate.important ? 1 : -1
  if (candidate.condition.specificity !== winner.condition.specificity) {
    if (!candidate.condition.specificityReliable || !winner.condition.specificityReliable) {
      return undefined
    }
    return candidate.condition.specificity > winner.condition.specificity ? 1 : -1
  }
  if (!sameConditionPart(candidate.condition.media, winner.condition.media)) {
    return undefined
  }
  if (candidate.condition.breakpoint !== winner.condition.breakpoint) {
    return candidate.condition.breakpoint > winner.condition.breakpoint ? 1 : -1
  }
  if (!sameConditionPart(candidate.condition.stateZones, winner.condition.stateZones)) {
    if (!candidate.condition.specificityReliable || !winner.condition.specificityReliable) {
      return undefined
    }
    const variantComparison = compareOrderLists(
      candidate.condition.variantOrder,
      winner.condition.variantOrder,
    )
    if (variantComparison !== 0) return variantComparison
  }
  if (candidate.utilityOrder !== winner.utilityOrder) {
    return candidate.utilityOrder > winner.utilityOrder ? 1 : -1
  }
  if (
    candidate.valueGroup !== undefined &&
    candidate.valueGroup === winner.valueGroup &&
    candidate.valueOrder !== winner.valueOrder
  ) {
    return candidate.valueOrder > winner.valueOrder ? 1 : -1
  }
  return candidate.value === winner.value ? 0 : undefined
}

function resolveDeclaration(declarations, target) {
  let frontier = []
  for (const declaration of declarations) {
    if (!conditionApplies(declaration.condition, target)) continue

    const comparisons = frontier.map(winner => comparePrecedence(declaration, winner))
    if (comparisons.some(comparison => comparison === -1)) continue
    frontier = frontier.filter((_, index) => comparisons[index] !== 1)
    frontier.push(declaration)
  }
  if (frontier.length === 0) return undefined

  const values = new Set(frontier.map(declaration => declaration.value))
  return {
    value: values.size === 1 ? frontier[0].value : undefined,
    ambiguous: values.size > 1,
  }
}

function isUnresolvedDimension(value) {
  return /(?:var|calc|min|max|clamp)\(/.test(value) || value.startsWith('(--')
}

function dimensionSourceOrder(value) {
  const numeric = /^\d*\.?\d+$/.exec(value)
  if (numeric) return { group: 'numeric', order: Number(value) }
  return { group: undefined, order: 0 }
}

function aspectKind(value) {
  if (value === 'square') return 'square'
  if (value === 'video') return 'nonsquare'
  if (!value.startsWith('[') || !value.endsWith(']')) return 'unknown'

  const ratio = /^(\d*\.?\d+)\/(\d*\.?\d+)$/.exec(value.slice(1, -1))
  if (!ratio) return 'unknown'
  return Number(ratio[1]) === Number(ratio[2]) ? 'square' : 'nonsquare'
}

/**
 * Build a per-property cascade for shape evidence and return a predicate for a
 * rounded-full condition. Selector specificity wins before the supported
 * Tailwind breakpoint, variant, utility and candidate source ordering.
 * Unsupported ties remain ambiguous and cannot produce a report. Evidence
 * never crosses a selector-target boundary.
 */
function circularAt(classText) {
  const widths = []
  const heights = []
  const aspects = []
  const animations = []
  const radii = new Map(ALL_CORNERS.map(corner => [corner, []]))
  let order = 0

  const add = (
    declarations,
    condition,
    value,
    utilityOrder = 0,
    valueSource = { group: undefined, order: 0 },
    important = false,
  ) => {
    declarations.push({
      condition,
      value,
      important,
      utilityOrder,
      valueGroup: valueSource.group,
      valueOrder: valueSource.order,
      order,
    })
  }

  for (const raw of classText.split(/\s+/)) {
    if (!raw) continue
    const { variants, base, important } = splitToken(raw)
    const condition = parseCondition(variants)

    const size = /^size-(\S+)$/.exec(base)
    if (size) {
      const value = normalizeDimension(size[1])
      if (value !== undefined) {
        add(widths, condition, value, 0, dimensionSourceOrder(value), important)
        add(heights, condition, value, 0, dimensionSourceOrder(value), important)
      }
    }
    const width = /^w-(\S+)$/.exec(base)
    if (width) {
      const value = normalizeDimension(width[1])
      if (value !== undefined) {
        add(widths, condition, value, 1, dimensionSourceOrder(value), important)
      }
    }
    const height = /^h-(\S+)$/.exec(base)
    if (height) {
      const value = normalizeDimension(height[1])
      if (value !== undefined) {
        add(heights, condition, value, 1, dimensionSourceOrder(value), important)
      }
    }
    const aspect = /^aspect-(\S+)$/.exec(base)
    if (aspect) add(aspects, condition, aspectKind(aspect[1]), 0, undefined, important)
    if (base === 'animate-spin' || base === 'animate-ping' || base === 'pf-animate-spin') {
      add(animations, condition, true, 0, undefined, important)
    }
    const radius = parseRounded(base)
    if (radius) {
      const isArbitrary = radius.size.startsWith('[') && radius.size.endsWith(']')
      const px = isArbitrary ? arbitraryToPx(radius.size) : NAMED_RADII[radius.size]
      const value = radius.size === 'full' || px === Infinity ? 'full' : 'other'
      for (const corner of radius.corners) {
        add(radii.get(corner), condition, value, 0, undefined, important)
      }
    }
    order += 1
  }

  const shapeDeclarations = [...widths, ...heights, ...aspects, ...animations]

  const isCircularAt = target => {
    if (resolveDeclaration(animations, target)?.value) return true

    const resolvedWidth = resolveDeclaration(widths, target)
    const resolvedHeight = resolveDeclaration(heights, target)
    const resolvedAspect = resolveDeclaration(aspects, target)
    const width = resolvedWidth?.value
    const height = resolvedHeight?.value
    const aspect = resolvedAspect?.value

    if (
      resolvedWidth?.ambiguous ||
      resolvedHeight?.ambiguous ||
      resolvedAspect?.ambiguous ||
      (width !== undefined && isUnresolvedDimension(width)) ||
      (height !== undefined && isUnresolvedDimension(height)) ||
      aspect === 'unknown'
    ) {
      return true
    }
    if (width !== undefined && height !== undefined) return width === height
    return aspect === 'square'
  }

  return radiusCondition => {
    const contexts = [radiusCondition]
    const contextKeys = new Set([JSON.stringify(radiusCondition)])
    for (const declaration of shapeDeclarations) {
      const existingContexts = [...contexts]
      for (const context of existingContexts) {
        const merged = mergeConditions(context, declaration.condition)
        if (!merged) continue
        const key = JSON.stringify(merged)
        if (!contextKeys.has(key)) {
          contextKeys.add(key)
          contexts.push(merged)
        }
      }
    }

    return contexts.every(target => {
      const hasFullCorner = [...radii.values()].some(
        declarations => {
          const resolved = resolveDeclaration(declarations, target)
          return !resolved?.ambiguous && resolved?.value === 'full'
        },
      )
      if (!hasFullCorner) return true
      return isCircularAt(target)
    })
  }
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
      } else if (replacement !== rawToken) {
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
        // clsx()/template call, so gather it from the whole element -- but
        // index it by variant scope, so a scoped radius is judged against the
        // evidence that applies where it applies.
        const classText = strings.map(entry => entry.text).join(' ')
        const escaping = classText
          .split(/\s+/)
          .filter(hasEscapingComment)
          .map(rawToken => ({
            rawToken,
            variants: splitToken(rawToken).variants,
            order: escapingCommentOrder(rawToken),
          }))
        const isCircular = circularAt(classText)
        const waived = hasWaiverAttribute(node.parent)

        for (const { node: stringNode, text, quasi } of strings) {
          for (const match of text.matchAll(/\S+/g)) {
            const rawToken = match[0]
            const { variants, base } = splitToken(rawToken)
            const size = parseRoundedSize(base)
            if (size === null) continue
            if (
              escaping.some(
                entry =>
                  entry.rawToken !== rawToken &&
                  escapingCommentMayPrecede(entry, variants) !== false,
              )
            ) {
              continue
            }

            const reportFullRound = () => {
              if (!checkFullRound || waived || isCircular(parseCondition(variants))) return
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
