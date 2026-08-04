import { describe, expect, it } from 'vitest'
import { compile } from 'tailwindcss'

import { __repairedValueForTest as repairedValue } from '../../../eslint-rules/pf-no-oversized-radius.js'

/**
 * `repairedValue` reproduces the value Tailwind emits for an arbitrary class, and
 * almost every hard bug in `pf-no-oversized-radius` has been a place where that
 * reproduction was wrong. Three rounds of review derived it by hand from measured
 * examples, and each time the result agreed with every case anyone had thought to
 * try and was still wrong in general -- the last version spaced `calc((e+pi)*1px)`
 * into something valid, when Tailwind emits it glued and the browser rejects it,
 * so a zero-height element was being excused as a circle.
 *
 * So this does not pin examples. It compiles each value with the real Tailwind and
 * asserts the rule's reproduction is character-identical to what comes out. A pin
 * can only be as good as the case someone imagined; this fails the moment the two
 * disagree, including when Tailwind itself changes.
 *
 * Values are written exactly as they appear inside the brackets of a class name,
 * underscores and all, and the same string is handed to both sides. Feeding the
 * rule a pre-spaced copy while feeding Tailwind the escaped one silently retires
 * the whole escape mechanism from the test: deleting the underscore handling
 * outright left every case green. Raised by Vasquez.
 */
const VALUES = [
  // Plain arithmetic, and the operand kinds that make an operator space.
  'calc(1px+2px)',
  'calc(1px*2)',
  'calc(50%+2px)',
  'calc(var(--a)+1px)',
  'calc(1rem+1px)',
  'calc(100%-var(--x))',
  'calc(1px*(2+3))',
  'calc(1px+.5px)',
  // Unary signs, and runs where only the first operator is spaced.
  'calc(+1px)',
  'min(1px,+2px)',
  'clamp(1rem,-2px,3rem)',
  'calc(1px++2px)',
  'calc(1px--1px)',
  'calc(-1px+2px)',
  'calc(1px*-1)',
  // Scientific notation: the sign belongs to the exponent, but only after a digit.
  'calc(1e+2px+1px)',
  'calc(1e-2px+1px)',
  'calc(1E+2px+1px)',
  'calc(1px+1e2px)',
  // Bare identifiers are not dimensions, and a sign between two of them is left
  // glued -- which is what the hand-written model got wrong.
  'calc((e+pi)*1px)',
  'calc(1px*e+1px)',
  'calc(pi*1px)',
  'calc(1px*infinity)',
  // Which functions are repaired, and how nesting and grouping propagate.
  'abs(1px+2px)',
  'sign(1px+2px)',
  'abs((1px+2px))',
  'calc((16px+16px))',
  'calc(1px*pow(1+1,2))',
  'calc(1px*sqrt(1+1))',
  'calc(1px*abs(sqrt(1+1)))',
  'calc(tan(abs(atan2(1+1,1)))*10px)',
  'calc(1px*log(2+1))',
  'calc(1px*exp(0+0))',
  'mod(5px,2px+1px)',
  'rem(5px,2px+1px)',
  'hypot(3px+1px,3px)',
  'clamp(1px,2px+1px,9px)',
  'round(up,1px+2px,1px)',
  // How the name in front of `(` is read: the scan stops at a hyphen, takes a
  // leading digit, and is lowercase-only.
  'foo-calc(1px/*)',
  '2calc(1px/*)',
  'CALC(1px+2px)',
  'calc(CALC(1px+2px))',
  // Where repair decides whether a comment exists at all.
  'calc(1px/*)',
  'calc(1px/*2)',
  'calc(_/*2)',
  'min(1px,_/*2)',
  'calc(1px*/*2)',
  // The space escape, and spacing the author wrote themselves.
  'calc(1px_+_2px)',
  'abs(1px_+_2px)',
  'min(1px,_2px)',
  // Which underscores survive is decided by the parse, not by the text. A URL is
  // opaque, so its whole subtree keeps them; a custom property name keeps them but
  // a fallback does not; and the exemptions match a suffix, so `my_var(` inherits.
  // All raised by Hicks.
  'url(foo_bar)',
  'url("foo_bar")',
  'calc(url(a_b)+1px)',
  'my_url(a_b)',
  'var(--x_y)',
  'calc(var(--foo_bar)+1px)',
  'var(--x_y,2px)',
  'var(--x_y,2px_3px)',
  'calc(var(--a,var(--b_c))+1px)',
  'my_var(--a_b)',
  // `+` is not a separator to the parser, so this is one call named `1px+var`,
  // which is neither `var` nor a `_var` suffix and so loses the exemption. The
  // same `var()` written first keeps it, four lines up.
  'calc(1px+var(--x_y,2px_3px))',
  'foo_bar',
  'calc(1px+\\_2px)',
  // Things repair cannot fix, which must come back unchanged rather than mended.
  'calc(1px+.)',
  'calc(1px_2px)',
  'min(1px_+_,_2px)',
  // A quoted string carrying the declaration's own terminator. Reading the
  // emission with `[^;]+` truncates here and compares this against `'a`.
  "'a;b'",
  "';_a'",
  // A semicolon really does end the declaration, and one inside a closed comment
  // really does not.
  'calc(1px+2px;3px)',
  'abs(1px/*x;y*/)',
]

/**
 * Values Tailwind refuses to emit at all. These are listed rather than skipped
 * because an early `return` inside `it.each` is reported as a pass, so a value
 * that quietly stopped emitting would look pinned while asserting nothing.
 * Raised by Vasquez.
 */
const NOT_EMITTED: string[] = ['theme(--a_b)']

/**
 * Read back the value Tailwind wrote into the `width` declaration.
 *
 * This recovers the *emitted string*, which is not the same question as what the
 * browser then makes of it -- `calc(1px+2px;3px)` is emitted whole and only
 * afterwards truncated by the CSS parser, and it is `provablyInvalidValue`'s job,
 * not this one's, to model that. Conflating the two made this stop at the
 * semicolon and demand the model return a value Tailwind never wrote.
 *
 * Tailwind terminates the declaration with the last semicolon in the rule, and a
 * `w-` utility emits exactly one declaration, so that semicolon is unambiguous.
 * Reading to the *first* one instead truncates inside anything that legitimately
 * contains one: a quoted string (`w-['a;b']`, raised by Vasquez) or a closed
 * comment (`abs(1px/*x;y*\/)`, raised by Hicks).
 */
function widthDeclaration(css: string): string | null {
  const start = css.indexOf('width:')
  if (start === -1) return null
  const from = start + 'width:'.length
  const end = css.lastIndexOf(';')
  if (end < from) return null
  return css.slice(from, end).trim()
}

async function emittedWidth(value: string): Promise<string | null> {
  // A fresh compiler per value: one of these opens a CSS comment that swallows the
  // rest of the sheet, and a shared compiler would make every later value read
  // back the first one's declaration -- which briefly made this agree with itself.
  const compiler = await compile('@tailwind utilities;', { base: process.cwd() })
  return widthDeclaration(compiler.build([`w-[${value}]`]))
}

describe('repairedValue reproduces Tailwind emission', () => {
  it.each(VALUES)('%s', async (value) => {
    const emitted = await emittedWidth(value)
    expect(emitted).not.toBeNull()
    expect(repairedValue(value)).toBe(emitted)
  })

  it.each(NOT_EMITTED)('%s is not emitted at all', async (value) => {
    expect(await emittedWidth(value)).toBeNull()
  })
})
