import { execFileSync } from 'node:child_process'
import { readdir, readFile } from 'node:fs/promises'
import path from 'node:path'
import ts from 'typescript'

const reactRoot = process.cwd()
const sourceRoot = path.join(reactRoot, 'src')
const stateVariants = new Set([
  'active',
  'checked',
  'disabled',
  'enabled',
  'focus',
  'focus-visible',
  'focus-within',
  'hover',
  'open',
  'target',
  'visited',
])

async function findSources(directory) {
  const entries = await readdir(directory, { withFileTypes: true })
  const files = []
  for (const entry of entries) {
    const fullPath = path.join(directory, entry.name)
    if (entry.isDirectory()) files.push(...await findSources(fullPath))
    else if (/\.(?:ts|tsx)$/.test(entry.name)) files.push(fullPath)
  }
  return files
}

function combinePaths(left, right) {
  return left.flatMap(leftPath => right.map(rightPath => [...leftPath, ...rightPath]))
}

function collectPaths(node) {
  if (ts.isStringLiteralLike(node)) return [[{ node, text: node.text }]]

  if (ts.isTemplateExpression(node)) {
    let paths = [[{ node: node.head, text: node.head.text }]]
    for (const span of node.templateSpans) {
      paths = combinePaths(paths, collectPaths(span.expression))
      paths = paths.map(entries => [...entries, { node: span.literal, text: span.literal.text }])
    }
    return paths
  }

  if (ts.isConditionalExpression(node)) {
    return [...collectPaths(node.whenTrue), ...collectPaths(node.whenFalse)]
  }

  if (
    ts.isBinaryExpression(node) &&
    (
      node.operatorToken.kind === ts.SyntaxKind.AmpersandAmpersandToken ||
      node.operatorToken.kind === ts.SyntaxKind.BarBarToken ||
      node.operatorToken.kind === ts.SyntaxKind.QuestionQuestionToken
    )
  ) {
    return [[], ...collectPaths(node.right)]
  }

  let paths = [[]]
  node.forEachChild(child => {
    paths = combinePaths(paths, collectPaths(child))
  })
  return paths
}

function splitToken(token) {
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
  return { variants: parts.slice(0, -1), utility: parts.at(-1) ?? '' }
}

function utilityFamily(utility) {
  const normalized = utility.replace(/^!/, '').replace(/!$/, '').replace(/^-/, '')
  return /^(bg|border|fill|from|ring|stroke|text|to|via)-/.exec(normalized)?.[1]
}

function analyzePath(entries, sourceFile, filePath) {
  const tokens = entries.flatMap(entry =>
    [...entry.text.matchAll(/\S+/g)].map(match => {
      const { variants, utility } = splitToken(match[0])
      return {
        entry,
        raw: match[0],
        utility,
        family: utilityFamily(utility),
        states: variants.filter(variant => stateVariants.has(variant)),
        context: variants.filter(variant => !stateVariants.has(variant)).join(':'),
      }
    }),
  ).filter(token => token.family)

  const matches = []
  for (let leftIndex = 0; leftIndex < tokens.length; leftIndex += 1) {
    for (let rightIndex = leftIndex + 1; rightIndex < tokens.length; rightIndex += 1) {
      const left = tokens[leftIndex]
      const right = tokens[rightIndex]
      if (
        left.utility !== right.utility ||
        left.context !== right.context ||
        left.states.join(':') === right.states.join(':') ||
        (left.states.length === 0 && right.states.length === 0)
      ) {
        continue
      }

      const variant = right.states.length > 0 ? right : left
      const base = variant === right ? left : right
      const location = sourceFile.getLineAndCharacterOfPosition(variant.entry.node.getStart(sourceFile))
      matches.push({
        file: path.relative(sourceRoot, filePath).replaceAll('\\', '/'),
        line: location.line + 1,
        family: variant.family,
        base: base.raw,
        variant: variant.raw,
      })
    }
  }
  return matches
}

const rawMatches = []
for (const filePath of await findSources(sourceRoot)) {
  const text = await readFile(filePath, 'utf8')
  const sourceFile = ts.createSourceFile(
    filePath,
    text,
    ts.ScriptTarget.Latest,
    true,
    filePath.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
  )

  function visit(node) {
    if (ts.isJsxAttribute(node) && ['class', 'className'].includes(node.name.getText(sourceFile))) {
      const initializer = node.initializer
      const expression = initializer && (
        ts.isJsxExpression(initializer) ? initializer.expression : initializer
      )
      if (expression) {
        for (const entries of collectPaths(expression)) {
          rawMatches.push(...analyzePath(entries, sourceFile, filePath))
        }
      }
    }
    node.forEachChild(visit)
  }

  visit(sourceFile)
}

const matches = [...new Map(rawMatches.map(match => [
  [match.file, match.line, match.base, match.variant].join('\u0000'),
  match,
])).values()]
const production = matches.filter(match =>
  !match.file.includes('/test/') && !/\.(?:test|spec)\.[tj]sx?$/.test(match.file),
)
const byFamily = Object.fromEntries(
  [...Map.groupBy(production, match => match.family)]
    .map(([family, familyMatches]) => [family, familyMatches.length]),
)

let dirty = false
try {
  execFileSync('git', ['diff', '--quiet'], { cwd: reactRoot })
  execFileSync('git', ['diff', '--cached', '--quiet'], { cwd: reactRoot })
} catch {
  dirty = true
}

console.log(JSON.stringify({
  scope: 'src/**/*.{ts,tsx} JSX class/className attributes',
  head: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: reactRoot, encoding: 'utf8' }).trim(),
  dirty,
  totals: {
    production: production.length,
    tests: matches.length - production.length,
    byFamily,
  },
  production,
}, null, 2))
