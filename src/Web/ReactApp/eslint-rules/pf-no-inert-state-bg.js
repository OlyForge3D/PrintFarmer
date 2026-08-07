const INTERACTION_STAGES = new Map([
  ['hover', 1],
  ['active', 2],
])

const COLOR_KEYWORDS = new Set([
  'black',
  'current',
  'inherit',
  'transparent',
  'white',
])

const COLOR_PALETTES = new Set([
  'amber',
  'blue',
  'cyan',
  'emerald',
  'fuchsia',
  'gray',
  'green',
  'indigo',
  'lime',
  'neutral',
  'orange',
  'pink',
  'purple',
  'red',
  'rose',
  'sky',
  'slate',
  'stone',
  'teal',
  'violet',
  'yellow',
  'zinc',
])

const MAX_EXPRESSION_PATHS = 128
const WAIVER_ATTRIBUTE = 'data-pf-allow-inert-bg'

function combinePaths(left, right) {
  if (left.length * right.length > MAX_EXPRESSION_PATHS) return []
  return left.flatMap(leftPath => right.map(rightPath => [...leftPath, ...rightPath]))
}

function collectExpressionPaths(node) {
  if (!node) return [[]]

  if (node.type === 'Literal') {
    return typeof node.value === 'string' ? [[{ node, text: node.value }]] : [[]]
  }

  if (node.type === 'TemplateLiteral') {
    let paths = [[]]
    for (let index = 0; index < node.quasis.length; index += 1) {
      paths = paths.map(path => [
        ...path,
        { node: node.quasis[index], text: node.quasis[index].value.cooked ?? '' },
      ])
      if (node.expressions[index]) {
        paths = combinePaths(paths, collectExpressionPaths(node.expressions[index]))
        if (paths.length === 0) return []
      }
    }
    return paths
  }

  if (node.type === 'ConditionalExpression') {
    return [
      ...collectExpressionPaths(node.consequent),
      ...collectExpressionPaths(node.alternate),
    ].slice(0, MAX_EXPRESSION_PATHS)
  }

  if (node.type === 'LogicalExpression') {
    if (node.operator === '&&') {
      return [[], ...collectExpressionPaths(node.right)].slice(0, MAX_EXPRESSION_PATHS)
    }
    return [
      ...collectExpressionPaths(node.left),
      ...collectExpressionPaths(node.right),
    ].slice(0, MAX_EXPRESSION_PATHS)
  }

  if (node.type === 'CallExpression' || node.type === 'NewExpression') {
    let paths = [[]]
    for (const argument of node.arguments) {
      if (argument.type === 'SpreadElement') continue
      paths = combinePaths(paths, collectExpressionPaths(argument))
      if (paths.length === 0) return []
    }
    return paths
  }

  if (node.type === 'ArrayExpression') {
    let paths = [[]]
    for (const element of node.elements) {
      if (!element || element.type === 'SpreadElement') continue
      paths = combinePaths(paths, collectExpressionPaths(element))
      if (paths.length === 0) return []
    }
    return paths
  }

  if (node.type === 'ObjectExpression') {
    let paths = [[]]
    for (const property of node.properties) {
      if (property.type !== 'Property' || property.computed) continue
      const keyPaths = collectExpressionPaths(property.key)
      paths = combinePaths(paths, keyPaths)
      if (paths.length === 0) return []
    }
    return paths
  }

  if (node.type === 'BinaryExpression' && node.operator === '+') {
    return combinePaths(
      collectExpressionPaths(node.left),
      collectExpressionPaths(node.right),
    )
  }

  if (
    node.type === 'ChainExpression' ||
    node.type === 'TSAsExpression' ||
    node.type === 'TSNonNullExpression' ||
    node.type === 'TSSatisfiesExpression' ||
    node.type === 'TSTypeAssertion'
  ) {
    return collectExpressionPaths(node.expression)
  }

  return [[]]
}

function splitTailwindToken(token) {
  const parts = []
  let squareDepth = 0
  let roundDepth = 0
  let start = 0

  for (let index = 0; index < token.length; index += 1) {
    const character = token[index]
    if (character === '\\') {
      index += 1
      continue
    }
    if (character === '[') squareDepth += 1
    else if (character === ']') squareDepth = Math.max(0, squareDepth - 1)
    else if (character === '(') roundDepth += 1
    else if (character === ')') roundDepth = Math.max(0, roundDepth - 1)
    else if (character === ':' && squareDepth === 0 && roundDepth === 0) {
      parts.push(token.slice(start, index))
      start = index + 1
    }
  }

  parts.push(token.slice(start))
  return {
    variants: parts.slice(0, -1),
    utility: parts.at(-1) ?? '',
  }
}

function splitModifier(value) {
  let squareDepth = 0
  let roundDepth = 0

  for (let index = 0; index < value.length; index += 1) {
    const character = value[index]
    if (character === '\\') {
      index += 1
      continue
    }
    if (character === '[') squareDepth += 1
    else if (character === ']') squareDepth = Math.max(0, squareDepth - 1)
    else if (character === '(') roundDepth += 1
    else if (character === ')') roundDepth = Math.max(0, roundDepth - 1)
    else if (character === '/' && squareDepth === 0 && roundDepth === 0) {
      return value.slice(0, index)
    }
  }

  return value
}

function isColorBackground(utility) {
  const withoutImportant = utility.replace(/^!/, '').replace(/!$/, '')
  if (withoutImportant.startsWith('-') || !withoutImportant.startsWith('bg-')) return false

  const value = splitModifier(withoutImportant.slice(3))
  if (value.startsWith('pf-') || COLOR_KEYWORDS.has(value)) return true
  if (/^\[(?:color:)?(?:#|var\(|--|(?:rgb|hsl|hwb|lab|lch|oklab|oklch|color)\()/.test(value)) {
    return true
  }

  const palette = /^([a-z]+)-\d{2,3}$/.exec(value)
  return Boolean(palette && COLOR_PALETTES.has(palette[1]))
}

function parseBackgroundToken(raw, fragment) {
  const { variants, utility } = splitTailwindToken(raw)
  if (!isColorBackground(utility) || variants.includes('disabled')) return undefined

  const interactions = variants.filter(variant => INTERACTION_STAGES.has(variant))
  if (interactions.length > 1) return undefined

  const interaction = interactions[0]
  return {
    raw,
    fragment,
    utility,
    stage: interaction ? INTERACTION_STAGES.get(interaction) : 0,
    state: interaction ?? 'base',
    context: variants
      .filter(variant => !INTERACTION_STAGES.has(variant) && variant !== 'enabled')
      .join(':'),
  }
}

function tokenizePath(fragments) {
  return fragments.flatMap(fragment =>
    [...fragment.text.matchAll(/\S+/g)]
      .map(match => parseBackgroundToken(match[0], fragment))
      .filter(Boolean),
  )
}

function uniqueStageToken(tokens, stage) {
  const candidates = tokens.filter(token => token.stage === stage)
  const utilities = new Set(candidates.map(token => token.utility))
  return utilities.size === 1 ? candidates.at(-1) : undefined
}

function findInertPairs(tokens) {
  const pairs = []
  const contexts = new Set(tokens.map(token => token.context))

  for (const context of contexts) {
    const scoped = tokens.filter(token => token.context === context)
    const base = uniqueStageToken(scoped, 0)
    const hover = uniqueStageToken(scoped, 1)
    const active = uniqueStageToken(scoped, 2)

    if (base && hover && base.utility === hover.utility) {
      pairs.push({ base, variant: hover })
    }

    const activeBase = hover ?? base
    if (activeBase && active && activeBase.utility === active.utility) {
      pairs.push({ base: activeBase, variant: active })
    }
  }

  return pairs
}

function hasWaiver(openingElement) {
  return Boolean(openingElement?.attributes?.some(attribute => {
    if (attribute.type !== 'JSXAttribute' || attribute.name?.name !== WAIVER_ATTRIBUTE) {
      return false
    }
    if (!attribute.value) return true
    if (attribute.value.type === 'Literal') {
      return attribute.value.value === true ||
        (typeof attribute.value.value === 'string' && attribute.value.value.trim().length > 0)
    }
    if (attribute.value.type !== 'JSXExpressionContainer') return false
    return attribute.value.expression?.type === 'Literal' &&
      attribute.value.expression.value === true
  }))
}

export default {
  meta: {
    type: 'problem',
    docs: {
      description:
        'Disallow direct hover/active background variants that repeat the background already active at that interaction stage',
      recommended: true,
    },
    messages: {
      inert:
        '"{{variant}}" repeats "{{base}}", so the {{state}} state has no background change. Use a distinct background utility or remove the inert variant. If this direct-state pin is intentional, document it with data-pf-allow-inert-bg.',
    },
    schema: [],
  },

  create(context) {
    return {
      JSXAttribute(node) {
        if (node.name.name !== 'className' && node.name.name !== 'class') return
        if (!node.value || hasWaiver(node.parent)) return

        const expression = node.value.type === 'JSXExpressionContainer'
          ? node.value.expression
          : node.value
        const paths = collectExpressionPaths(expression)
        const reported = new Set()

        for (const path of paths) {
          for (const pair of findInertPairs(tokenizePath(path))) {
            const key = `${pair.variant.fragment.node.range?.join(':')}:${pair.variant.raw}`
            if (reported.has(key)) continue
            reported.add(key)

            context.report({
              node: pair.variant.fragment.node,
              messageId: 'inert',
              data: {
                base: pair.base.raw,
                variant: pair.variant.raw,
                state: pair.variant.state,
              },
            })
          }
        }
      },
    }
  },
}
