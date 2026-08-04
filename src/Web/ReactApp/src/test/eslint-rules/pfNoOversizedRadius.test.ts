import { RuleTester } from "eslint";
import { describe, it } from "vitest";
import rule from "../../../eslint-rules/pf-no-oversized-radius.js";

// RuleTester drives describe/it itself; vitest supplies them.
RuleTester.describe = describe;
RuleTester.it = it;

const ruleTester = new RuleTester({
  languageOptions: {
    ecmaVersion: 2022,
    sourceType: "module",
    parserOptions: { ecmaFeatures: { jsx: true } },
  },
});

ruleTester.run("pf-no-oversized-radius", rule, {
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
    // Tailwind's explicit data-type hint resolves to the same CSS as the bare
    // form; skipping it would let any oversized radius through unseen.
    { code: '<div className="rounded-[length:4px]" />' },
    // Unresolvable arbitrary values are never guessed at.
    { code: '<div className="rounded-[var(--pf-radius-md)]" />' },
    { code: '<div className="rounded-[calc(2px+2px)]" />' },

    // Provably circular elements keep rounded-full with no annotation.
    { code: '<span className="w-2 h-2 rounded-full bg-pf-error" />' },
    { code: '<span className="h-2.5 w-2.5 rounded-full" />' },
    { code: '<div className="size-8 rounded-full" />' },
    { code: '<div className="aspect-square rounded-full" />' },
    // Only one axis is overridden, so nothing contradicts `aspect-square`: the
    // element may still be square, and the rule fails toward excusing.
    { code: '<div className="aspect-square hover:w-4 hover:rounded-full" />' },
    { code: '<div className="aspect-square w-8 h-8 rounded-full" />' },
    // Arbitrary-property radii within the ceiling.
    { code: '<div className="[border-radius:8px]" />' },
    { code: '<div className="[border-radius:0.5rem]" />' },
    { code: '<div className="[border-radius:var(--pf-radius-md)]" />' },
    {
      code: '<div className="animate-spin rounded-full h-5 w-5 border-b-2" />',
    },
    { code: '<div className="pf-animate-spin rounded-full h-12 w-12" />' },
    {
      code: '<span className="animate-ping absolute h-full w-full rounded-full" />',
    },

    // Shape evidence in a sibling fragment of the same clsx() call still counts.
    {
      code: '<div className={clsx("rounded-full border", "h-6 w-6 shrink-0")} />',
    },

    // Evidence carrying the same variant prefix as the radius proves the shape
    // in the state that radius applies to. A range input's thumb is the case
    // that forces this: the host <input> is a rectangle, so no honest waiver
    // exists for it.
    {
      code: '<input className={clsx("[&::-webkit-slider-thumb]:w-4", "[&::-webkit-slider-thumb]:h-4", "[&::-webkit-slider-thumb]:rounded-full")} />',
    },
    { code: '<div className="md:size-8 md:rounded-full" />' },
    { code: '<div className="hover:aspect-square hover:rounded-full" />' },
    // Unprefixed evidence applies in every state, including a prefixed one.
    { code: '<div className="size-8 md:rounded-full" />' },
    // The cascade resolves per axis: on hover this really is 4x4.
    { code: '<div className="w-8 h-4 hover:w-4 hover:rounded-full" />' },

    // Explicit, greppable waiver at the call site.
    {
      code: '<span data-pf-radius="full" className="px-3 py-1 rounded-full">tag</span>',
    },
    // The pre-existing progress markers are honoured without a second annotation.
    {
      code: '<div data-pf-progress-track className="w-full h-2 rounded-full" />',
    },

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
    {
      code: '<span data-pf-radius={shape} className="px-3 rounded-full">tag</span>',
    },

    // Variants are split on top-level colons only, so a bracketed colon inside a
    // variant must not break tokenization. Each of these is a genuine circle and
    // must stay silent -- but the failure mode being guarded is the opposite one,
    // in `invalid` below: a mis-split token resolves to nothing and is never
    // examined at all.
    { code: '<div className="md:size-8 md:hover:rounded-full" />' },
    { code: '<div className="md:w-8 md:hover:h-8 md:hover:rounded-full" />' },
    {
      code: '<div className="supports-[display:grid]:size-8 supports-[display:grid]:rounded-full" />',
    },
    {
      code: '<div className="group-hover/item:size-8 group-hover/item:rounded-full" />',
    },
    { code: '<div className="@max-md:size-8 @max-md:rounded-full" />' },

    // Evidence applies when its condition is a subset of the radius's, because an
    // unprefixed class is still in force inside a variant.
    { code: '<div className="size-8 hover:rounded-full" />' },

    // Raised by Vasquez: `auto` is indefinite, and CSS only ignores
    // `aspect-ratio` when both axes are definite. This really is a circle, so
    // recording `auto` as a value that disagrees with `full` was a false report.
    { code: '<div className="w-full h-auto aspect-square rounded-full" />' },
    { code: '<div className="h-full w-auto aspect-square rounded-full" />' },
    // `size-auto` is indefinite on both axes, so the ratio is not overruled.
    { code: '<div className="size-auto aspect-square rounded-full" />' },

    // Raised by Hicks: the CSS-wide keywords leave the axis indefinite too,
    // because the initial value of `width`/`height` is `auto`. `inherit` is
    // included because the inherited computed value is unknowable here, and
    // treating it as unproven errs toward excusing.
    {
      code: '<div className="w-full h-[initial] aspect-square rounded-full" />',
    },
    {
      code: '<div className="w-full h-[inherit] aspect-square rounded-full" />',
    },
    { code: '<div className="w-[unset] h-full aspect-square rounded-full" />' },
    { code: '<div className="size-[revert] aspect-square rounded-full" />' },
    // Raised by Hicks: CSS keywords are case-insensitive, and a bare `var()` may
    // itself hold `auto`. A `var()` nested in `calc()` cannot, since `calc()`
    // never produces a keyword, so that stays on the comparability path.
    {
      code: '<div className="h-[INITIAL] w-full aspect-square rounded-full" />',
    },
    { code: '<div className="w-full h-[Auto] aspect-square rounded-full" />' },
    {
      code: '<div className="w-full h-[var(--h)] aspect-square rounded-full" />',
    },
    // Raised by Hicks: substitution happens before parsing, so if `--x` holds
    // `auto` then `calc(auto + 0px)` is invalid at computed-value time and the
    // height falls back to its initial value, which is `auto`. Any `var()`,
    // wherever it sits, therefore leaves the axis indefinite.
    {
      code: '<div className="w-full h-[calc(var(--x)+0px)] aspect-square rounded-full" />',
    },

    // Proving squareness needs *comparable* lengths, not merely equal spelling.
    // The spacing scale, `px`, absolute arbitrary lengths and same-unit viewport
    // values all resolve to one length on either axis.
    { code: '<div className="w-2.5 h-2.5 rounded-full" />' },
    { code: '<div className="w-px h-px rounded-full" />' },
    { code: '<div className="w-[12px] h-[12px] rounded-full" />' },
    {
      code: '<div className="w-[length:1rem] h-[length:1rem] rounded-full" />',
    },
    { code: '<div className="w-dvh h-dvh rounded-full" />' },

    // Raised by Hicks: the caller only consults the classifier after the two
    // value *strings* have matched exactly, so any axis-independent unit is
    // comparable — the axis-dependent ones cannot slip through by unit alone,
    // because `w-[10cqw]` and `h-[10cqh]` are different strings and never match.
    { code: '<div className="w-[1lh] h-[1lh] rounded-full" />' },
    { code: '<div className="w-[10cqw] h-[10cqw] rounded-full" />' },
    { code: '<div className="w-[10cqmin] h-[10cqmin] rounded-full" />' },
    { code: '<div className="w-[2vmin] h-[2vmin] rounded-full" />' },
    {
      code: '<div className="w-[min(10px,2rem)] h-[min(10px,2rem)] rounded-full" />',
    },
    {
      code: '<div className="w-[clamp(1rem,2vw,3rem)] h-[clamp(1rem,2vw,3rem)] rounded-full" />',
    },
    // Tailwind inserts the whitespace CSS Values requires around `+` and `-`, so
    // both spellings compile to `calc(2rem + 4px)` and both are a real 36x36.
    // The unspaced form was briefly pinned as *reporting*, on the reading that it
    // tokenises as two juxtaposed values -- true of the class name, false of the
    // stylesheet. Hicks produced the compiled output; Bishop and Vasquez had both
    // checked the CSS grammar and neither had checked the emission.
    {
      code: '<div className="w-[calc(2rem+4px)] h-[calc(2rem+4px)] rounded-full" />',
    },
    {
      code: '<div className="w-[calc(2rem_+_4px)] h-[calc(2rem_+_4px)] rounded-full" />',
    },
    // A leading `+` is legal on a CSS number and Tailwind emits it untouched, so
    // `width: +1px` applies and this really is 1x1. Reported by both Hicks and
    // Bishop, independently, as a regression: base excused it. Four numeric
    // patterns in the rule admitted a leading `-` and none admitted a `+`.
    // Three of the four are pinned by the mixed-spelling pairs below. The fourth
    // -- `lengthDegree`'s -- was twice judged unfalsifiable, by me and by Bishop
    // measuring his own mutant, and it is `size-[+2rem]` that proves otherwise:
    // one dimension means no twin, so nothing intercepts the value on the
    // comparison path and it reaches the validity check, where the narrow
    // pattern condemns a real length and reports a real square. A surviving
    // mutant means no pin distinguishes the change, which is as often a missing
    // pin as it is dead code, and the two are only told apart by constructing
    // the case that separates them.
    { code: '<div className="w-[+1px] h-[+1px] rounded-full" />' },
    { code: '<div className="size-[+2rem] rounded-full" />' },
    // The sign amnesty has to keep excusing a *well-formed* sign in every math
    // function, not only condemn the malformed ones: `min(+1px, 2px)` computes to
    // 1px in Chromium, so this really is square.
    { code: '<div className="size-[min(+1px,2px)] rounded-full" />' },
    // `_` is how Tailwind spells a space inside an arbitrary value, and the rest
    // of this rule already reads it as one. The operand-flanking check did not,
    // so the idiomatic spelling of the very expressions the amnesty exists to
    // protect was condemned: Chromium computes both of these to 3px on each
    // axis, and both were reported. Found by Vasquez.
    { code: '<div className="size-[abs(1px_+_2px)] rounded-full" />' },
    { code: '<div className="size-[min(1px_+_2px,9rem)] rounded-full" />' },
    // Tailwind rewrites nothing inside `abs()` and `sign()`, so there a sign has
    // to carry whitespace on both sides already, exactly as CSS requires. These
    // four compute; the glued spellings below do not. Raised by Hicks.
    { code: '<div className="size-[abs(+1px)] rounded-full" />' },
    { code: '<div className="size-[abs(2*1px)] rounded-full" />' },
    { code: '<div className="size-[calc(1px+.5px)] rounded-full" />' },
    // Tailwind's repair is scoped to the *nearest* enclosing function, so nesting
    // decides the outcome: `abs(calc(1px+2px))` is repaired on the inside and
    // computes to 3px, while `calc(abs(1px+2px))` is not and drops (pinned in the
    // invalid list). Classifying by the outermost call got both backwards.
    { code: '<div className="size-[abs(calc(1px+2px))] rounded-full" />' },
    // A bare grouping paren inherits the repairability of whatever encloses it,
    // so the glued sign inside one is repaired when the group sits in `calc()`.
    // The `abs((1px+2px))` mirror is in the invalid list; only the pair proves
    // inheritance rather than a blanket rule in either direction.
    { code: '<div className="size-[calc((16px_+16px))] rounded-full" />' },
    { code: '<div className="size-[calc(2*(1px+2px))] rounded-full" />' },
    {
      code: '<div className="w-[calc((8px+8px))] h-[calc((8px+8px))] rounded-full" />',
    },
    {
      code: '<div className="w-[min((1px+2px),9rem)] h-[min((1px+2px),9rem)] rounded-full" />',
    },
    // Repair is not blanket. A unary sign after `(` or a comma is left alone, and
    // a second operator in a run is not an operand for the first, so only the
    // first is spaced. Every one of these is the measured emission.
    { code: '<div className="size-[calc(+16px)] rounded-full" />' },
    { code: '<div className="size-[min(16px,+16px)] rounded-full" />' },
    { code: '<div className="size-[calc((1px)+2px)] rounded-full" />' },
    { code: '<div className="size-[calc(1px_+2px)] rounded-full" />' },
    // Repair covers every CSS math function except `abs()` and `sign()`, measured
    // one at a time. An earlier list held only the six that had come up in review,
    // so `pow`, `sqrt`, `log`, `exp` and the trigonometric family were read as
    // unrepairable and their values condemned though the browser computes them.
    // The results have to be brought back to a length to be observable here: a
    // bare `size-[pow(1+1,2)]` is a unitless number, which Chromium rejects as a
    // width, so it reports either way and would have pinned nothing. Raised by
    // Hicks, whose own example had that flaw.
    { code: '<div className="size-[calc(1px*pow(1+1,2))] rounded-full" />' },
    { code: '<div className="size-[calc(1px*sqrt(1+1))] rounded-full" />' },
    { code: '<div className="size-[hypot(3px+1px,3px)] rounded-full" />' },
    // Repair reaches into a repairable function even when a non-repairable one
    // encloses it: `abs(sqrt(1+1))` is emitted with its interior spaced, and the
    // whole expression computes to 1.41px. `abs` withholds repair from its own
    // operators, not from its children's.
    {
      code: '<div className="size-[calc(1px*abs(sqrt(1+1)))] rounded-full" />',
    },
    // A sign inside scientific notation belongs to the exponent. `calc(1e+2px+1px)`
    // is emitted as `calc(1e+2px + 1px)` and computes to 101px; spacing the first
    // sign split the number in half and condemned a real circle. Raised by Hicks.
    { code: '<div className="size-[calc(1e+2px+1px)] rounded-full" />' },
    // The exponent exemption requires a *digit* before the `e`, because CSS also
    // has `e` as a bare math constant and there the following sign really is an
    // operator: `calc(1px*e+1px)` is emitted as `calc(1px * e + 1px)` and computes
    // to 3.718px. Exempting every `e` would have withheld that space, and Chromium
    // drops `calc(1px * e+1px)` outright.
    { code: '<div className="size-[calc(1px*e+1px)] rounded-full" />' },
    // `atan2` returns an angle, so it cannot form a length on its own -- but fed
    // through `tan` it can: `calc(tan(abs(atan2(1+1,1)))*10px)` computes to 20px.
    // I had reported the trailing digit in the function-name scan as unfalsifiable;
    // Hicks constructed this, which reaches it. Recorded because the claim was
    // mine and it was wrong.
    {
      code: '<div className="size-[calc(tan(abs(atan2(1+1,1)))*10px)] rounded-full" />',
    },
    // A semicolon inside a *closed* comment terminates nothing: the browser never
    // sees it, and `abs(1px/*x;y*/)` is a perfectly good 1px on both axes. This is
    // the discriminator against condemning every semicolon in the text rather than
    // every semicolon that survives comment-stripping.
    { code: '<div className="size-[abs(1px/*x;y*/)] rounded-full" />' },
    // A semicolon is fatal inside a math function and ordinary inside `if()`,
    // which uses it to separate branches -- so the condemnation ought to be scoped
    // to the math calls, and it is not. A stack-based scoped version was written
    // and then deleted: `if()` is not a function this rule can read, so an `if()`
    // value is refused for opacity long before its semicolons are consulted, and
    // the scoped test agreed with the blunt one on every pin here. Those cases are
    // in the invalid list, with the semicolon-free spelling beside them as the
    // evidence that the semicolon is not what condemns them. Raised by Hicks,
    // whose observation was right and whose diagnosis was not.
    // Importance declared inside the arbitrary value is the same mechanism as the
    // class-level `!` marker, because Tailwind copies the brackets into the
    // declaration: `size-[16px!important]` emits `width: 16px!important`, which
    // beats `h-[32px]` outright, and the element measures 16x16. Reported as a
    // lozenge until Hicks measured it. The `_` spelling is the discriminator
    // against a whitespace class that forgets Tailwind writes a space as `_`.
    { code: '<div className="size-[16px!important] h-[32px] rounded-full" />' },
    {
      code: '<div className="size-[16px_!important] h-[32px] rounded-full" />',
    },
    {
      code: '<div className="size-[16px!/*c*/important] h-[32px] rounded-full" />',
    },
    // The keyword tolerates whitespace and comments on its right as well, and
    // Tailwind copies either through: `width: 16px!important/*c*/` and
    // `width: 16px!important ` are both important, measured.
    {
      code: '<div className="size-[16px!important/*c*/] h-[32px] rounded-full" />',
    },
    {
      code: '<div className="size-[16px!important_] h-[32px] rounded-full" />',
    },
    // A comment may also *contain* a `!important`, and then the last `!` in the
    // raw text is the decoy one. Comments are discarded before the marker is
    // looked for at all, so this is a real important 16px and a real 16x16
    // circle, measured. Found by Hicks against a version that searched the raw
    // text from the right; searching from the left instead gets this case right
    // and its `h-` twin in the invalid list wrong, which is what settled that
    // neither quantifier was the answer and the value had to be read rather than
    // pattern-matched.
    {
      code: '<div className="size-[16px!important/*!important/**/] h-[32px] rounded-full" />',
    },
    // CSS identifiers may carry escapes and Tailwind passes them through
    // verbatim, so all four of these are important and all four measure 16x16.
    // The literal forms are Vasquez's, the numeric form is Hicks's; every one was
    // a false report against a genuine circle before the value was decoded rather
    // than matched. `\69` is `i`, and `_` terminates a numeric escape because
    // Tailwind writes a space that way.
    { code: '<div className="size-[16px!\\important] h-[32px] rounded-full" />' },
    { code: '<div className="size-[16px!imp\\ortant] h-[32px] rounded-full" />' },
    { code: '<div className="size-[16px!IMPOR\\TANT] h-[32px] rounded-full" />' },
    { code: '<div className="size-[16px!\\69mportant] h-[32px] rounded-full" />' },
    // `_` is Tailwind's space, so it terminates a numeric escape exactly as a
    // space does and may sit between the `!` and the keyword. Both measure
    // 16x16. The first is the discriminator against terminating numeric escapes
    // on whitespace only, the second against trimming nothing from the front of
    // the keyword.
    { code: '<div className="size-[16px!\\69_mportant] h-[32px] rounded-full" />' },
    { code: '<div className="size-[16px!_important] h-[32px] rounded-full" />' },
    // `stripCssComments` substitutes a space, so an interior comment used to
    // leave `size-[16px ]` behind and no reader could parse it. Measured 16x16.
    { code: '<div className="size-[16px/*c*/!important] h-[32px] rounded-full" />' },
    { code: '<div className="size-[/*c*/16px!important] h-[32px] rounded-full" />' },
    // A negative radius is invalid, so CSS drops the declaration and the element
    // measures r=0. The `+` twin in the invalid list is a real 16px radius.
    { code: '<div className="rounded-[-16px]" />' },
    // Case-insensitive units apply to the full-round vocabulary too: measured
    // r=9999px on a 64x64 box, which is a circle and correctly excused.
    { code: '<div className="rounded-[9999PX] size-[64px]" />' },
    // A comment before the data-type hint is not a hint at all -- Tailwind honours
    // it only at the literal start of the value, and this computes to r=0. Its
    // twin, with the comment after the hint, is a real 16px radius and is pinned
    // in the invalid list. Stripping comments before the hint collapsed the two.
    { code: '<div className="rounded-[/**/length:16px]" />' },
    { code: '<div className="rounded-[border-radius:length:16px]" />' },
    // A marker written *entirely* inside a comment is not a marker: this is a
    // plain `width: 16px` losing to an `h-[32px]`, and it measures 16x32. The
    // rule excuses it anyway -- but for the documented reason, not an importance
    // one. The comment-free twin on the next line is the control: it also
    // measures 16x32 and is also excused, because which of two equally ranked
    // utilities wins is deliberately not modelled and the tie fails toward
    // excusing (limitation 2, #1064). Raised by Hicks inside the importance
    // finding; the importance half is fixed and this half is pre-existing.
    {
      code: '<div className="size-[16px/*!important/**/] h-[32px] rounded-full" />',
    },
    { code: '<div className="size-[16px] h-[32px] rounded-full" />' },
    // Two `!important`s in one value is not important twice, it is an invalid
    // declaration: it is dropped, `w-`/`h-` win uncontested, and the element
    // measures 32x32. The second line is the control -- one `!important` is valid,
    // beats both, and measures 16x16. Both are squares, so both are excused, and
    // the pair pins the outcome without depending on which dimension was read.
    {
      code: '<div className="size-[16px!important!important] w-[32px] h-[32px] rounded-full" />',
    },
    {
      code: '<div className="size-[16px!important] w-[32px] h-[32px] rounded-full" />',
    },
    // The name in front of `(` is read as digits and lowercase letters, stopping
    // at anything else -- so `2calc` is not a call, the value is emitted verbatim,
    // and its `/*` really does open a comment that swallows the rule that follows.
    // The element is skipped, because the browser has already discarded its radius.
    // Reading the name as `calc` here invented a repair and reported a flat-cornered
    // element as a bubble. Raised by Hicks.
    {
      code: '<div className="w-4 h-8 rounded-full before:w-[2calc(1px/*)]" />',
    },
    { code: '<div className="size-[calc(1e-2px+1px)] rounded-full" />' },
    // A paren inside a CSS string is a character, not structure, so the element is
    // still judged -- and this one is a genuine circle. Raised by Hicks.
    { code: "<div className=\"before:content-['('] size-8 rounded-full\" />" },
    // Repair decides whether a comment exists, so the exclusion set is observable
    // precisely here. `/` is left glued when an operator, an open paren or a comma
    // precedes it, and the `/*` that survives escapes into the stylesheet and
    // swallows the radius -- so the element is skipped even though it reads as a
    // 4x8 lozenge. Measured emissions: `calc( /*2)`, `min(1px, /*2)`,
    // `calc(1px * /*2)`. Contrast the invalid pin for `calc(1px/*2)`, where an
    // operand precedes, the `/` is spaced to `calc(1px / *2)`, no comment is left
    // and the value simply drops.
    { code: '<div className="w-4 h-8 rounded-full size-[calc(_/*2)]" />' },
    { code: '<div className="w-4 h-8 rounded-full size-[min(1px,_/*2)]" />' },
    { code: '<div className="w-4 h-8 rounded-full size-[calc(1px*/*2)]" />' },
    // Unbalanced parens are skipped outright rather than guessed at: Tailwind
    // balances `calc((1px+2px)` into a valid 3px square but emits `calc(1px+2px`
    // as `calc()1px+2px`, which drops. The two differ by one inner paren and
    // point opposite ways, so neither can be read off the class. Raised by Hicks.
    { code: '<div className="size-[calc((1px+2px)] rounded-full" />' },
    {
      code: '<div className="w-4 h-8 rounded-full focus:w-[calc(1px+2px] focus:h-[calc(1px+2px]" />',
    },
    // Negative zero is not a negative length. `-0px` computes to exactly 0px,
    // indistinguishable from `0`, so the drop gate has to test the magnitude and
    // not merely the presence of a sign. A 0x0 box is still square. Raised by Hicks.
    { code: '<div className="size-[-0px] rounded-full" />' },
    { code: '<div className="w-[-0rem] h-[-0rem] rounded-full" />' },
    // Tailwind has no double negation: `not-not-hover:` and `not-not-starting:`
    // both emit nothing at all, so neither radius ever reaches the page. Reading
    // `not-` only once treated the second as an ordinary variant name and
    // reported a radius that does not exist. Raised by Hicks.
    { code: '<div className="w-4 h-8 not-not-starting:rounded-full" />' },
    { code: '<div className="w-4 h-8 not-not-hover:rounded-full" />' },
    // The same recursion has to run *through* a negation, not merely into one.
    // `not-group-print:`, `has-not-starting:` and `group-not-not-hover:` all emit
    // nothing, so the radius they carry never lands on this 16x32 lozenge.
    { code: '<div className="w-4 h-8 not-group-print:rounded-full" />' },
    { code: '<div className="w-4 h-8 has-not-starting:rounded-full" />' },
    { code: '<div className="w-4 h-8 group-not-not-hover:rounded-full" />' },
    // A comment that opens in one candidate and closes in another swallows every
    // class between the two, radius included -- and once a comment is open the
    // tokenizer stops recognising quotes, so the `*\/` inside `content-['*\/']`
    // really does close it. Both spellings leave the element unreadable, so it is
    // excused rather than judged on evidence the stylesheet has destroyed.
    {
      code: "<div className=\"w-4 h-[1px/*] rounded-full before:content-['*/']\" />",
    },
    // A backslash escapes the next character wherever it sits, so `\\"` is a literal
    // quote that opens no string and the `/*` behind it starts a real comment.
    // Chromium proves it: a sheet containing `width: \\"/*;` parses one rule instead
    // of two and the next rule is swallowed whole. Treating the escaped quote as a
    // string opener hid the comment and let the lozenge be judged on CSS that never
    // arrives. Raised by Vasquez.
    { code: "<div className='w-4 h-8 rounded-full w-[\\\"/*]' />" },
    // The tail-only trim in `dimensionReading` is load-bearing after all, and
    // this is the case that shows it: the malformed height is dropped, the
    // `aspect-square` beside it then governs, and the element really is a circle.
    // Trimming the leading space would read `length:1rem` as a valid height,
    // contradict the ratio and report. Built by Hicks after Vasquez and I each
    // failed to find a discriminator and I had written the comment off as
    // unfalsifiable.
    {
      code: '<div className="w-[32px] h-[/**/length:1rem] aspect-square rounded-full" />',
    },
    // The same length spelled with and without the sign. This is the pair that
    // isolates the sign-blindness: it has to survive the comparability gate and
    // then be read as a number, so it holds two of the four patterns at once.
    { code: '<div className="w-[+1px] h-[1px] rounded-full" />' },
    { code: '<div className="w-[2rem] h-[+2rem] rounded-full" />' },
    // A CSS comment is whitespace, and Tailwind copies it straight through:
    // `w-[1px/**/]` emits `width: 1px/**/`, which computes to 1px. Reading the
    // comment as part of the value made an ordinary length unrecognisable, and
    // an unrecognisable length was then condemned as invalid and treated as
    // dropped -- so a genuine 1x1 circle was reported. Raised by Hicks.
    { code: '<div className="w-[1px/**/] h-[1px/**/] rounded-full" />' },
    // A comment at either edge really is trailing whitespace, so both of these
    // are 1px and the circle is genuine. The interior spelling `1/**\/px` is
    // *not* -- it is pinned as a report below.
    { code: '<div className="w-[/**/1px] h-[1px/**/] rounded-full" />' },
    // The symmetric spellings above are excused by the *twin* comparison, which
    // strips comments -- not by the read itself, which did not. That left the
    // mixed spelling, where the two axes carry the same length written two ways,
    // dropped at the read and reported as a non-square despite computing to a
    // real 16x16 circle in the browser. Raised by Bishop.
    { code: '<div className="w-[16px/**/] h-[16px] rounded-full" />' },
    { code: '<div className="w-[16px] h-[16px/**/] rounded-full" />' },
    { code: '<div className="w-[/**/16px] h-[16px] rounded-full" />' },
    // `not-` negating a *selector* variant is still a selector: `not-hover:`
    // emits `&:not(:hover)` and keeps its class, so it ties with `focus:` and the
    // tie is unioned into an excuse. Contrast the at-rule spelling pinned below.
    {
      code: '<div className="w-8 h-8 not-hover:w-8 focus:w-4 not-hover:focus:rounded-full" />',
    },
    // Variants Tailwind compiles to nothing at all. `@starting-style` has no
    // negated form, and `group-`/`peer-` need a selector to hang their marker
    // on, so none of these three emits a single rule. Weighing them as if they
    // were `not-print:` let a dead class win a comparison and report a radius
    // that never ships. Each was settled by compiling it, not by reasoning about
    // what ought to be negatable. Raised by Hicks.
    {
      code: '<div className="w-8 h-8 not-starting:w-8 focus:w-4 not-starting:focus:rounded-full" />',
    },
    {
      code: '<div className="w-8 h-8 group-not-print:w-8 focus:w-4 group-not-print:focus:rounded-full" />',
    },
    {
      code: '<div className="w-8 h-8 group-print:w-8 focus:w-4 group-print:focus:rounded-full" />',
    },
    // Being unweighable was a half-measure: it let a dead class excuse, but did
    // not stop a dead *radius* being reported. Tailwind emits nothing for any of
    // these, so the radius under discussion is not on the page at all and there
    // is nothing to report on a 16x32 box. Hicks rejected the unranked reading
    // and was right. The bracket spelling is the same fact arriving by a
    // different branch: only `@media`, `@supports` and `@container` have a
    // negated form, so every other at-rule named this way is inert.
    { code: '<div className="w-[16px] h-[32px] not-starting:rounded-full" />' },
    {
      code: '<div className="w-[16px] h-[32px] group-not-print:rounded-full" />',
    },
    { code: '<div className="w-[16px] h-[32px] group-print:rounded-full" />' },
    { code: '<div className="w-4 h-8 not-[@starting-style]:rounded-full" />' },
    { code: '<div className="w-4 h-8 not-[@layer]:rounded-full" />' },
    // Tailwind's whitespace repair is not confined to `calc()`: `min(1rem+2px,9rem)`
    // emits `min(1rem + 2px, 9rem)` and computes. Scoping the sign amnesty to
    // `calc()` alone condemned this genuine square. Raised by Hicks.
    {
      code: '<div className="w-[min(1rem+2px,9rem)] h-[min(1rem+2px,9rem)] rounded-full" />',
    },
    // An unterminated `/*` is not a local problem. Tailwind emits `width: 1px/*`
    // verbatim and CSS then swallows everything up to the next `*/` -- here the
    // `border-radius` rule itself, which Chromium computes as 0. The element
    // cannot be read off its class list at all, so it is left alone rather than
    // reported on evidence the stylesheet has already destroyed. Raised by Hicks.
    { code: '<div className="w-[1px/*] h-[2px] rounded-full" />' },
    // The same suppression, now with the measurement behind it rather than the
    // argument: `w-[64px/*c] rounded-[9999px] h-[32px]` renders r=**0px** in
    // Chromium, because the comment really does eat the `border-radius` rule.
    // Reporting it would be a bubble that is not on the page.
    { code: '<div className="w-[64px/*c] rounded-[9999px] h-[32px]" />' },
    // Two escaping comments swallow each other's rules, and the survivor is the
    // 8px one: measured r=8px, inside budget. Neither radius can be judged, so
    // the element is skipped even though one of its tokens is a radius.
    {
      code: '<div className="rounded-[9999px/*c] rounded-[8px/*d] w-[64px] h-[32px]" />',
    },
    // But a *lone* escaping comment inside the radius token is judged, not
    // skipped, and when the box is square the judgement is an excuse: 64x64 with
    // a 9999px radius is a circle, measured.
    { code: '<div className="rounded-[9999px/*c] size-[64px]" />' },
    // A lone escaping comment in a radius token still suppresses every *other*
    // token, and this is the pin for it: the comment swallows the following
    // `rounded-[9999px]` rule outright, so the element measures r=8px and
    // reporting the 9999px would be a bubble that is not on the page -- a false
    // report, the one direction these approximations must never take.
    { code: '<div className="rounded-[8px/*c] rounded-[9999px] w-[64px] h-[32px]" />' },
    // Zero is the one unitless value that is a valid length.
    { code: '<div className="w-[0] h-[0] rounded-full" />' },
    // Raised by Hicks: the inline- and block-axis viewport units are the same
    // absolute length whichever property they land on, and CSS accepts
    // scientific notation, so `[1e2px]` is a valid 100px length.
    { code: '<div className="w-[10vi] h-[10vi] rounded-full" />' },
    { code: '<div className="w-[10vb] h-[10vb] rounded-full" />' },
    { code: '<div className="w-[1e2px] h-[1e2px] rounded-full" />' },
    { code: '<div className="w-[1.5e-1rem] h-[1.5e-1rem] rounded-full" />' },
    // The root-relative font units are absolute once the root font is resolved.
    { code: '<div className="w-[10rch] h-[10rch] rounded-full" />' },
    { code: '<div className="w-[10ric] h-[10ric] rounded-full" />' },

    // Raised by Hicks: `[2/2]` is 1:1 just as surely as `[1/1]` is, so the square
    // test divides rather than pattern-matching the spelling.
    {
      code: '<div className="aspect-square hover:aspect-[2/2] hover:rounded-full" />',
    },
    { code: '<div className="aspect-[3/3] animate-spin rounded-full" />' },
    // A definite square pair overrides the ratio, so the box really is a circle
    // and the non-square ratio stops contradicting the animation.
    {
      code: '<div className="animate-spin aspect-[2/1] w-4 h-4 rounded-full" />',
    },
    // The override does not reach the base state the radius sits in.
    {
      code: '<div className="animate-spin hover:aspect-[2/1] rounded-full" />',
    },
    // A definite pair overrides the ratio even when the two axes are incomparable,
    // so the ratio stops proving anything — and an incomparable pair is not proof
    // of difference either, which leaves the animation standing.
    {
      code: '<div className="animate-spin aspect-[2/1] w-full h-8 rounded-full" />',
    },
    // Raised by Hicks: replacing a ratio is not the same as proving a shape. Each
    // of these removes the `aspect-ratio` without saying what the box became, so
    // none of them may contradict the animation. CSS treats a degenerate ratio as
    // `auto`, and a `var()` ratio is simply unreadable here.
    { code: '<div className="animate-spin aspect-auto rounded-full" />' },
    { code: '<div className="animate-spin aspect-[var(--r)] rounded-full" />' },
    { code: '<div className="animate-spin aspect-[0/1] rounded-full" />' },
    { code: '<div className="animate-spin aspect-[1/0] rounded-full" />' },
    // A negative component makes the declaration invalid, so CSS drops it and the
    // earlier `aspect-square` still stands. The sign test has to look at the
    // components, since `[-1/-1]` divides to 1.
    {
      code: '<div className="aspect-square hover:aspect-[-1/-1] hover:rounded-full" />',
    },
    // Raised by Hicks: an invalid value is dropped by CSS, so the earlier ratio
    // still stands. `[0x2]` is JavaScript's idea of a number, not CSS's.
    {
      code: '<div className="aspect-square hover:aspect-[banana] hover:rounded-full" />',
    },
    {
      code: '<div className="aspect-square hover:aspect-[0x2/0x2] hover:rounded-full" />',
    },
    // Raised by Hicks: a finer replacement cancels a coarser proof. On hover the
    // ratio really is `auto`, so nothing contradicts the animation there.
    {
      code: '<div className="animate-spin aspect-[2/1] hover:aspect-auto hover:rounded-full" />',
    },
    // A function is valid CSS but unreadable here, so it replaces without proving.
    {
      code: '<div className="animate-spin aspect-[calc(1/2)] rounded-full" />',
    },
    // At a tie the rule unions the readings (#1064) and takes the one that fails
    // toward excusing: an `unknown` alongside a `nonsquare` withdraws the proof,
    // which is also what the cascade does here, since the later value wins.
    {
      code: '<div className="animate-spin aspect-[2/1] aspect-auto rounded-full" />',
    },
    // The same withdrawal across two different conditions of equal specificity,
    // where the cascade genuinely is unresolved from here.
    {
      code: '<div className="animate-spin md:aspect-[2/1] hover:aspect-auto md:hover:rounded-full" />',
    },
    // Tailwind accepts a bare fraction as well as a bracketed one.
    { code: '<div className="aspect-2/2 rounded-full" />' },
    { code: '<div className="aspect-[.5/.5] rounded-full" />' },

    // Two *different* conditions of equal specificity are unioned rather than
    // resolved (#1064). Raised by Vasquez. These two are no longer such a tie: a
    // media variant adds no specificity, so `hover:` wins outright and the ratio
    // it sets is the one CSS applies. The reporting half now lives in `invalid`.
    {
      code: '<div className="hover:aspect-square md:aspect-video md:hover:rounded-full" />',
    },
    // Media variants add no specificity however many are stacked, so `hover:w-4`
    // beats `md:lg:w-8` and this really is a 16x16 circle at md AND lg AND hover.
    // Raised by Bishop, whose probe found the count proxy reporting a real circle.
    {
      code: '<div className="h-4 md:lg:w-8 hover:w-4 md:lg:hover:rounded-full" />',
    },
    // The same, through the opaque pair: the `hover:` pair wins, and it is equal
    // on both axes, so the element is square wherever CSS actually applies it.
    {
      code: '<div className="hover:w-[calc(1px*2)] hover:h-[calc(1px*2)] md:lg:w-8 md:lg:hover:rounded-full" />',
    },
    // An opaque winner withdraws the axis instead of letting the declaration it
    // overrode stand: at `:hover` the width is `calc(32px*1)`, not the discarded
    // 4px, so `aspect-square` is never contradicted. Raised by Bishop.
    {
      code: '<div className="aspect-square w-[4px] h-[32px] hover:w-[calc(32px*1)] hover:rounded-full" />',
    },
    {
      code: '<div className="aspect-square w-[4px] h-[32px] hover:w-[var(--s)] hover:rounded-full" />',
    },

    // A later `aspect-*` only replaces the ratio in the states it reaches, so
    // the unprefixed radius still sits on a square box.
    {
      code: '<div className="w-8 aspect-square hover:aspect-[2/1] rounded-full" />',
    },
    {
      code: '<div className="hover:aspect-[2/1] aspect-square rounded-full" />',
    },
    // An overriding ratio that is itself square changes nothing.
    {
      code: '<div className="aspect-square hover:aspect-[1/1] hover:rounded-full" />',
    },
    {
      code: '<div className="aspect-square hover:aspect-[1] hover:rounded-full" />',
    },

    // An animation is a heuristic, not a declaration, so a definite-but-
    // incomparable pair does not overrule it. This is the live pulse indicator
    // pattern: a `h-full w-full` child of a square `h-2 w-2` parent, which the
    // rule cannot see. Reporting it was a false positive.
    {
      code: '<div className="animate-ping absolute inline-flex h-full w-full rounded-full" />',
    },
    { code: '<div className="pf-animate-spin w-full h-full rounded-full" />' },

    // Tailwind's important marker, in both the v3 and v4 positions.
    { code: '<div className="size-8 rounded-full!" />' },
    { code: '<div className="size-8 !rounded-full" />' },

    // Raised by Hicks: the order of two classes in the attribute does not decide
    // which declaration wins, because Tailwind emits each utility group in its own
    // sorted order — `aspect-video aspect-square` and `aspect-square aspect-video`
    // compile to byte-identical CSS. Both orderings must therefore reach the same
    // verdict, and an unresolvable tie fails toward excusing (#1064).
    { code: '<div className="aspect-square aspect-video rounded-full" />' },
    { code: '<div className="aspect-video aspect-square rounded-full" />' },
    // `!important` is the one tie CSS does settle, and it outranks specificity
    // rather than merely adding to it, so it beats a finer normal selector too.
    { code: '<div className="aspect-square! aspect-video rounded-full" />' },
    {
      code: '<div className="aspect-square! hover:aspect-video hover:rounded-full" />',
    },
    { code: '<div className="w-8! w-4 h-8 rounded-full" />' },
    { code: '<div className="animate-spin! rounded-full" />' },
    // Raised by Hicks: CSS accepts a leading `+` on a number.
    { code: '<div className="aspect-[+1/+1] rounded-full" />' },
    // Raised by Hicks: `banana()` parses as a function but no such function
    // exists, so the declaration is invalid and the earlier square stands. This is
    // the distinction between *unreadable* and *invalid*: a known value function
    // would have replaced the ratio, an unknown name leaves it untouched.
    {
      code: '<div className="aspect-square hover:aspect-[banana()] hover:rounded-full" />',
    },
    // Raised by Hicks: `1.` is a valid JavaScript number and not a valid CSS one,
    // so the declaration is dropped and the earlier square survives.
    {
      code: '<div className="aspect-square hover:aspect-[1./1.] hover:rounded-full" />',
    },
    // Raised by Hicks: the grammar is `auto || <ratio>`, so a bare `auto` may sit
    // beside a ratio. It may prove squareness -- which errs toward excusing, and
    // is wrong only for a non-square replaced element -- but never a lozenge; see
    // the `auto 2/1` case below.
    { code: '<div className="aspect-[auto_1/1] rounded-full" />' },
    // `auto` is a standalone term in `auto || <ratio>` and may appear at most
    // once, so a repeat is invalid CSS and the earlier square survives untouched.
    {
      code: '<div className="aspect-square hover:aspect-[auto_auto] hover:rounded-full" />',
    },
    // A same-key tie inside a variant is unresolvable for the same reason as in
    // the base state, so it excuses rather than reports.
    {
      code: '<div className="hover:aspect-square hover:aspect-video hover:rounded-full" />',
    },

    // Raised by Hicks. `width`/`height` and `aspect-ratio` are resolved in
    // separate cascades and only then combined, so specificity cannot arbitrate
    // between them; used-value computation simply drops the ratio whenever both
    // axes are definite. A pair therefore beats a ratio however coarsely it was
    // selected, and neither of these is provably a lozenge.
    {
      code: '<div className="animate-spin w-full h-8 hover:aspect-[2/1] hover:rounded-full" />',
    },
    {
      code: '<div className="animate-spin w-1/2 h-8 aspect-[2/1] rounded-full" />',
    },

    // Raised by Hicks. `auto || <ratio>` is not proof on a replaced element: for
    // an `<img>`, `auto` selects the natural ratio and the specified one applies
    // only in its absence, and the rule cannot see the element type. So `auto`
    // may still prove squareness -- erring toward excusing -- but never a
    // lozenge, and this no longer contradicts the animation.
    { code: '<div className="animate-spin aspect-[auto_2/1] rounded-full" />' },

    // Raised by Hicks. A known function called with no arguments is not valid
    // CSS, so the declaration is dropped and the base-state proof stands rather
    // than being withdrawn as unreadable.
    {
      code: '<div className="aspect-square hover:aspect-[calc()] hover:rounded-full" />',
    },

    // Raised by Hicks. An arbitrary value carrying a non-length unit is not a
    // width or a height: CSS drops `height:10deg`, the axis falls back to `auto`
    // and the square ratio governs, so both of these are circles.
    { code: '<div className="w-8 h-[10deg] aspect-square rounded-full" />' },
    { code: '<div className="w-8 h-[10bogus] aspect-square rounded-full" />' },

    // Raised by Hicks. An indefinite reading has to *win its own cascade*, not be
    // dropped: skipping `hover:w-auto` left the coarser `w-4` standing, so the
    // rule read 16x32 on hover when `width:auto` had replaced it, leaving a 32x32
    // circle.
    {
      code: '<div className="w-4 h-8 hover:w-auto hover:aspect-square hover:rounded-full" />',
    },

    // Raised by Hicks. A radius scoped to a descendant is judged on that
    // element's dimensions; the host's `size-4` says nothing about the `<img>`,
    // and pairing the two invented a lozenge that exists on neither element.
    {
      code: '<div className="size-4 [&_img]:h-8 [&_img]:aspect-square [&_img]:rounded-full" />',
    },

    // Raised by Hicks. A unitless number inside a function is a scalar, not a
    // malformed length: `calc(2*1rem)` is a perfectly comparable 32px on both
    // axes, so this is a circle.
    {
      code: '<div className="w-[calc(2*1rem)] h-[calc(2*1rem)] rounded-full" />',
    },

    // Raised by Hicks. `height:banana` is discarded by the browser, so the height
    // falls back to `auto` and the square ratio governs.
    { code: '<div className="w-8 h-[banana] aspect-square rounded-full" />' },

    // Raised by Hicks, and it falsified a claim I had made: the depth guard in
    // `splitTopLevel` is *not* unfalsifiable. With it this is three parts and so
    // invalid, leaving the base square standing; without it the stray `)` drives
    // the depth negative, the `/` is never treated as top-level, and the whole
    // thing reads as an unreadable function that withdraws the proof. Pinned so
    // the guard cannot be removed silently.
    {
      code: '<div className="aspect-square hover:aspect-[calc(1))/2/3] hover:rounded-full" />',
    },

    // Settled by Bishop, Hicks and Vasquez, against my own initial reading. A
    // finer ratio the rule cannot *read* no longer withdraws a coarser proof of
    // squareness. Tailwind normalises `aspect-[calc(1)]` to `aspect-ratio:
    // calc(1)`, which is 1, so withdrawing on it reported a genuine circle —
    // the one direction the rule swears off, because the waiver it would provoke
    // (`data-pf-radius="full"`) is documented as the ledger of deliberate
    // *pills*, and filing a circle there corrupts it for every later reader.
    // The accepted cost is the last case: `calc(1/2)` really is a 2:1 lozenge
    // and now passes silently, because the rule cannot evaluate the arithmetic.
    {
      code: '<div className="w-8 aspect-square hover:aspect-[calc(1)] hover:rounded-full" />',
    },
    {
      code: '<div className="w-8 aspect-square hover:aspect-[var(--r)] hover:rounded-full" />',
    },
    {
      code: '<div className="aspect-square hover:aspect-[calc((1/2)/(3/4))] hover:rounded-full" />',
    },
    {
      code: '<div className="w-8 aspect-square hover:aspect-[calc(1/2)] hover:rounded-full" />',
    },

    // Raised by Bishop. A percentage `height` computes to `auto` when the
    // parent's own height depends on its content (CSS 2.1 §10.5), so `h-full` is
    // only conditionally definite and cannot overrule a ratio the rule can read.
    // These lay out as real squares whenever the parent's height is indefinite.
    { code: '<div className="w-full h-full aspect-square rounded-full" />' },
    { code: '<div className="size-full aspect-square rounded-full" />' },
    {
      code: '<div className="w-[100%] h-[100%] aspect-square rounded-full" />',
    },

    // Raised by Bishop. Equality of length is decided by value, not by spelling:
    // `1e2px` and `100px` are the same 100px, so this is a circle.
    { code: '<div className="w-[1e2px] h-[100px] rounded-full" />' },

    // Raised by Bishop and Hicks independently. A math function whose operands
    // are all unitless resolves to a `<number>`, which is invalid for `width`, so
    // CSS drops the declaration and the readable ratio governs.
    {
      code: '<div className="w-[calc(2*3)] h-8 aspect-square rounded-full" />',
    },
    { code: '<div className="w-[min(2,3)] h-8 aspect-square rounded-full" />' },
    {
      code: '<div className="w-[clamp(1,2,3)] h-8 aspect-square rounded-full" />',
    },

    // Raised by Hicks. CSS *discards* an invalid declaration, which is not the
    // same as one that sets an indefinite value: `height:banana` never lands, so
    // the earlier `h-8` still stands and this is a 32x32 circle. Contrast
    // `hover:h-auto`, which does land and does unpin — pinned in `invalid`.
    { code: '<div className="w-8 h-8 hover:h-[banana] hover:rounded-full" />' },
    { code: '<div className="w-8 h-8 hover:h-[10deg] hover:rounded-full" />' },

    // Raised by Hicks. Valid on `flex-basis` or `max-width`, but not a `width` or
    // `height` value at all, so CSS drops them and the ratio governs.
    { code: '<div className="w-8 h-[none] aspect-square rounded-full" />' },
    { code: '<div className="w-8 h-[content] aspect-square rounded-full" />' },
    {
      code: '<div className="w-8 h-[fill-available] aspect-square rounded-full" />',
    },

    // Raised by Hicks. Valid, but not lengths: `min-content` may or may not be
    // 32px, so it cannot overrule a ratio the way a real length does.
    {
      code: '<div className="w-8 h-[min-content] aspect-square rounded-full" />',
    },
    {
      code: '<div className="w-8 h-[fit-content] aspect-square rounded-full" />',
    },
    { code: '<div className="w-8 h-[stretch] aspect-square rounded-full" />' },

    // Raised by Hicks. Tailwind matches its data-type hints case-sensitively, so
    // `LENGTH:` is not a hint; the declaration is dropped and the ratio governs.
    {
      code: '<div className="w-4 h-[LENGTH:1rem] aspect-square rounded-full" />',
    },

    // Same-scope child evidence is read on its own terms: a square child inside a
    // lozenge host is still a circle.
    { code: '<div className="w-4 h-8 *:size-8 *:rounded-full" />' },
    { code: '<div className="w-4 h-8 [&_img]:size-8 [&_img]:rounded-full" />' },

    // Raised by Hicks, and my first probes were too weak to see it: a child that
    // proves its own squareness must not be judged on the host's width. This is
    // the case `*:size-8` short-circuited past, because it set both axes at once.
    {
      code: '<div className="w-4 *:inline-block *:h-8 *:aspect-square *:rounded-full" />',
    },

    // The same fix in the other direction. `[.group:hover_&]` puts the
    // combinator *before* the `&`, so the target is still this element and its
    // own `size-8` governs -- that is what `group-hover:` compiles to.
    { code: '<div className="size-8 [.group:hover_&]:rounded-full" />' },

    // Raised by Hicks. The rule cannot type-check CSS math, so a dimension it
    // cannot evaluate must not withdraw a proof: CSS drops `calc(1px/1px)` (a
    // length over a length is a number), leaving the 32x32 circle standing.
    // `-1px` is likewise invalid on `height` and likewise dropped.
    ...[
      "hover:h-[calc(1px/1px)]",
      "hover:h-[calc(1px*1px)]",
      "hover:h-[calc(1px_2px)]",
      "hover:h-[min(1px,2)]",
      "hover:h-[clamp(1px,2px)]",
      "hover:h-[-1px]",
      "hover:h-[calc(0)]",
      // Valid CSS this parser does not know. Unsupported-by-the-rule must not
      // mean dropped-by-CSS, and either way it may not withdraw the proof.
      "hover:h-[anchor-size(width)]",
      "hover:h-[-webkit-fill-available]",
      "hover:h-[env(safe-area-inset-top)]",
    ].map((override) => ({
      code: `<div className="w-8 h-8 ${override} hover:rounded-full" />`,
    })),

    // ...and the same values may not overrule a ratio the rule *can* read.
    {
      code: '<div className="w-[calc(1px/1px)] h-8 aspect-square rounded-full" />',
    },
    { code: '<div className="w-[-1px] h-8 aspect-square rounded-full" />' },

    // The one power an unreadable dimension keeps: the same value on both axes
    // is still a square, because CSS resolves it identically twice.
    {
      code: '<div className="w-[calc(1px*2)] h-[calc(1px*2)] rounded-full" />',
    },
    {
      code: '<div className="w-[calc(1px_+_2px)] h-[calc(1px_+_2px)] rounded-full" />',
    },

    // Raised by Hicks. That shortcut is specificity-aware in both directions:
    // here the opaque pair is the *more* specific declaration, so on hover the
    // box really is `calc(1px*2)` square and the base `w-8 h-8` is overruled.
    // The stale-pair counterpart is pinned as a report below.
    {
      code: '<div className="w-8 h-8 hover:w-[calc(1px*2)] hover:h-[calc(1px*2)] hover:rounded-full" />',
    },

    // Raised by Hicks. A combinator anywhere after `&` moves the target off the
    // host, not merely one adjacent to it: `[&:hover_img]` emits `&:hover img`,
    // so the host's `w-4` says nothing about the `img` that carries the radius,
    // and the evidence in that same scope proves it square. `details-content:`
    // emits `&::details-content`, a separate box for the same reason.
    {
      code: '<div className="w-4 [&:hover_img]:h-8 [&:hover_img]:aspect-square [&:hover_img]:rounded-full" />',
    },
    {
      code: '<div className="w-4 details-content:h-8 details-content:aspect-square details-content:rounded-full" />',
    },
    // ...while a combinator that is only *inside* a payload leaves the target on
    // the host, one level in as well as at the top: this one really is a
    // descendant `img`, so the host's `w-4` stays out of it.
    {
      code: '<div className="w-4 [&:not(.x)_img]:h-8 [&:not(.x)_img]:aspect-square [&:not(.x)_img]:rounded-full" />',
    },
    // The square *proof* is scoped too, not just the contradiction. A host pair
    // carrying more variants than the descendant's own used to win the cascade
    // outright and displace it, so host evidence condemned a descendant
    // indirectly — by defeating its proof rather than by contradicting it. In
    // all three of these the radius element really is a circle in the state the
    // radius applies. Raised by Bishop.
    {
      code: '<div className="hover:focus:w-4 hover:focus:h-8 [&_img]:w-8 [&_img]:h-8 [&_img]:hover:focus:rounded-full" />',
    },
    {
      code: '<div className="hover:focus:w-4 hover:focus:h-8 before:w-8 before:h-8 before:hover:focus:rounded-full" />',
    },
    {
      code: '<div className="[&_img]:w-[calc(1px*2)] [&_img]:h-[calc(1px*2)] hover:focus:w-[calc(1px*3)] hover:focus:h-[calc(1px*5)] [&_img]:hover:focus:rounded-full" />',
    },
    // The other half of that scoping, and a tolerated false *negative*: the proof
    // is still tried against the whole cascade first, so a host pair outranking
    // the descendant's own excuses it. Here the `<img>` really is 16x32 and
    // really is round, and the rule says nothing. This is the documented "host
    // evidence may excuse but may not condemn" leniency, pinned so that scoping
    // the proof outright cannot happen by accident — that would be a stricter
    // rule than this codebase agreed to, and strictness is the direction that
    // manufactures dishonest waivers.
    {
      code: '<div className="hover:focus:w-8 hover:focus:h-8 [&_img]:w-4 [&_img]:h-8 [&_img]:hover:focus:rounded-full" />',
    },
    // Round seventeen. Four constructions that a genuine circle can be spelled
    // with, each of which the specificity model inverted the cascade on and so
    // reported. All four report on this branch before the fix and say nothing on
    // the base commit, so each pin is a killing mutant rather than a restatement.
    //
    // An arbitrary at-rule variant is a media query, not a selector, and weighs
    // nothing -- but it is spelled with brackets, so it slipped past the at-rule
    // list and was parsed as a selector, where the colon in `hover:hover` bought
    // it a pseudo-class it does not have. Raised by Vasquez.
    {
      code: '<div className="w-8 h-8 [@media(hover:hover)]:w-4 [div_&]:w-8 [@media(hover:hover)]:[div_&]:rounded-full" />',
    },
    // `:is()` weighs its most specific argument and nothing for itself, so
    // `:is(.a,.b)` is one class. Tallying the text made it three and beat a real
    // two-class condition. Raised by Vasquez.
    {
      code: '<div className="x y w-8 h-8 [&:is(.a,.b)]:w-4 [.x.y_&]:w-8 [&:is(.a,.b)]:[.x.y_&]:rounded-full" />',
    },
    // The mirror of the arbitrary-variant count, for the *named* spelling:
    // `has-[.a.b.c]` is three classes and outranks `hover:focus:`, but was
    // counted as one and lost. Raised by Hicks.
    {
      code: '<div className="w-8 h-8 has-[.a.b.c]:w-8 hover:focus:w-4 has-[.a.b.c]:hover:focus:rounded-full" />',
    },
    // `abs()` preserves its argument's dimension and does not care which axis it
    // lands on, so two identical `abs()` values are the same length. It was not
    // on the comparable-function list, so the squareness proof was refused and a
    // plain circle was reported. Raised by Hicks.
    { code: '<div className="w-[abs(32px)] h-[abs(32px)] rounded-full" />' },
    // Round eighteen, all three raised by Hicks against the round-seventeen fixes
    // and all three reporting on this branch while saying nothing on base.
    //
    // `group-[...]` and `peer-[...]` carry a selector as well: Tailwind wraps the
    // marker class in `:where()`, so the payload is the whole weight and
    // `group-[.a.b.c]` outranks `hover:focus:` rather than losing to it.
    {
      code: '<div className="w-8 h-8 group-[.a.b.c]:w-8 hover:focus:w-4 group-[.a.b.c]:hover:focus:rounded-full" />',
    },
    // `:nth-child(An+B of S)` is one pseudo-class plus the most specific selector
    // in `S`, not one per entry: two classes here, where a flat tally made four
    // and beat a genuine three-class condition.
    {
      code: '<div className="w-8 h-8 [&:nth-child(2_of_.a,.b,.c)]:w-4 [.x.y.z_&]:w-8 [&:nth-child(2_of_.a,.b,.c)]:[.x.y.z_&]:rounded-full" />',
    },
    // Making `abs()` comparable was not enough on its own: a value whose
    // arithmetic cannot be *followed* stays opaque without withdrawing its axis,
    // so the base `w-[16px]` survived under the override and the circle
    // `aspect-square` proves was reported anyway. `hypot()` takes the same path.
    {
      code: '<div className="w-[16px] h-[32px] aspect-square hover:w-[abs(32px)] hover:rounded-full" />',
    },
    {
      code: '<div className="w-[16px] h-[32px] aspect-square hover:w-[hypot(3rem,4rem)] hover:rounded-full" />',
    },
    // Arity is part of following the arithmetic, exactly as it is for `clamp()`:
    // `abs()` takes one argument, CSS drops the declaration outright otherwise,
    // and the base survives as the proven 32x32 circle it is.
    {
      code: '<div className="w-8 h-8 hover:w-[abs(1px,2px)] hover:rounded-full" />',
    },
    // Round nineteen, all three raised by Hicks against the round-eighteen
    // fixes and all three reporting on this branch while saying nothing on base.
    //
    // Tailwind wraps a `group-[…]` payload in `:is()`, so a selector list takes
    // its most specific entry rather than the sum of all of them.
    {
      code: '<div className="x y w-8 h-8 group-[.a,.b,.c]:w-4 [.x.y_&]:w-8 group-[.a,.b,.c]:[.x.y_&]:rounded-full" />',
    },
    // A named group or peer carries a `/name` modifier after the bracket, which
    // is not part of the selector and must not stop the payload being weighed.
    {
      code: '<div className="w-8 h-8 group-[.a.b.c]/item:w-8 hover:focus:w-4 group-[.a.b.c]/item:hover:focus:rounded-full" />',
    },
    {
      code: '<div className="w-8 h-8 group-has-[.a.b.c]/item:w-8 hover:focus:w-4 group-has-[.a.b.c]/item:hover:focus:rounded-full" />',
    },
    // `round()` takes an optional rounding strategy keyword first. It is not an
    // operand, so it neither makes the value invalid nor makes two identically
    // spelled lengths differ, and dropping it leaves the two real arguments the
    // arity check expects.
    {
      code: '<div className="w-[round(up,32px,1px)] h-[round(up,32px,1px)] rounded-full" />',
    },
    {
      code: '<div className="w-[16px] h-[32px] aspect-square hover:w-[round(up,32px,1px)] hover:rounded-full" />',
    },
    {
      code: '<div className="w-8 h-8 hover:w-[round(up,32px,1px,2px)] hover:rounded-full" />',
    },
    // A numeric-returning call is only collapsed when its argument is a *type*
    // CSS accepts, not merely a count it accepts. `sin()`/`cos()`/`tan()` take a
    // number or an angle and `pow()`/`sqrt()`/`log()`/`exp()` take numbers, so
    // each of these is a type error CSS drops -- which leaves the base `w-8`
    // standing and the element a genuine 32x32 circle. Collapsing them regardless
    // read the width as a definite `1px` and reported it. Raised by Vasquez.
    {
      code: '<div className="w-8 h-8 hover:w-[calc(1px*sin(10px))] hover:rounded-full" />',
    },
    {
      code: '<div className="w-8 h-8 hover:w-[calc(1px*sin(50%))] hover:rounded-full" />',
    },
    {
      code: '<div className="w-8 h-8 hover:w-[calc(1px*pow(2px,2))] hover:rounded-full" />',
    },
    {
      code: '<div className="w-8 h-8 hover:w-[calc(1px*sqrt(4px))] hover:rounded-full" />',
    },
    {
      code: '<div className="w-8 h-8 hover:w-[calc(1px*exp(2px))] hover:rounded-full" />',
    },
    {
      code: '<div className="w-8 h-8 hover:w-[calc(1px*log(4px,2))] hover:rounded-full" />',
    },
    // The collapse speaks about type and not magnitude, so a substituted argument
    // needs no guard of its own: the same expression on both axes is square
    // whatever it resolves to. Guarding on `var()` reported this.
    {
      code: '<div className="w-[calc(32px*sin(var(--a)))] h-[calc(32px*sin(var(--a)))] rounded-full" />',
    },
    // `dark:` compiles to `@media (prefers-color-scheme: dark)` in this project,
    // which carries no specificity, so `dark:w-8` cannot overrule the `hover:w-4`
    // that beats it in CSS. Counting it as a class excused this 16x32 lozenge.
    // Raised by Hicks. The companion report is in `invalid`.
    // A namespaced type selector is still a type: `.a *|section &` weighs one
    // class and one type, which loses to `md:hover:`'s two classes... except that
    // `md:` is an at-rule, so `md:hover:` is one class and the type column
    // decides for the arbitrary variant. Missing `*|section` entirely dropped
    // that column and reported a real 32x32 circle. Raised by Hicks.
    {
      code: '<div className="w-8 h-8 md:hover:w-4 [.a_*|section_&]:w-8 md:hover:[.a_*|section_&]:rounded-full" />',
    },
    // Round nineteen, raised by Vasquez: CSS's math constants are `<number>`s of
    // fixed value, so they are scalars wherever they appear and cannot make a
    // value axis-dependent. Refusing them as bare identifiers dropped both axes
    // and reported a real circle.
    {
      code: '<div className="w-[calc(1px*pi)] h-[calc(1px*pi)] rounded-full" />',
    },
    {
      code: '<div className="w-[calc(1px*e)] h-[calc(1px*e)] rounded-full" />',
    },
    {
      code: '<div className="w-[16px] h-[32px] aspect-square hover:w-[calc(1px*pi)] hover:rounded-full" />',
    },
    // A constant is a scalar, so it does not rescue a value whose degree is
    // wrong: an area is still dropped and the base 32x32 circle still stands.
    {
      code: '<div className="w-8 h-8 hover:w-[calc(1px*1px*pi)] hover:rounded-full" />',
    },
    // Round twenty-one, raised by Vasquez: `sin()` and its numeric siblings
    // return a `<number>`, and angle units are legal only inside them, so the
    // whole call is a scalar and `calc(100px*sin(90deg))` is an ordinary length.
    // Refusing it on sight of `deg` dropped both axes and reported a real circle.
    {
      code: '<div className="w-[calc(100px*sin(90deg))] h-[calc(100px*sin(90deg))] rounded-full" />',
    },
    {
      code: '<div className="w-[calc(2rem*cos(0deg))] h-[calc(2rem*cos(0deg))] rounded-full" />',
    },
    {
      code: '<div className="w-[16px] h-[32px] aspect-square hover:w-[calc(100px*sin(90deg))] hover:rounded-full" />',
    },
    // Round twenty-one, raised by Hicks. An at-rule contributes nothing to a
    // selector, so it must not break a specificity tie either: `md:print:` was
    // counting two segments and outranking `group-[section]:`, whose payload is a
    // type selector and so weighs nothing in the class column but really does win.
    {
      code: '<div className="w-8 h-8 md:print:w-4 group-[section]:w-8 md:print:group-[section]:rounded-full" />',
    },
    {
      code: '<div className="w-8 h-8 md:print:w-4 [section_&]:w-8 md:print:[section_&]:rounded-full" />',
    },
    // A bare arbitrary variant is emitted as written, and a selector *list* is
    // matched one entry at a time, so it weighs its most specific entry rather
    // than their sum. This is the plain-variant twin of the `group-[…]` fix
    // above: `[.a,.b,.c_&]` is one class, not three, and loses to `[.x.y_&]`.
    {
      code: '<div className="x y w-8 h-8 [.a,.b,.c_&]:w-4 [.x.y_&]:w-8 [.a,.b,.c_&]:[.x.y_&]:rounded-full" />',
    },
    // Tailwind puts the whole ancestor selector of an `in-*` variant inside
    // `:where()` -- `in-[.a]` emits `:where(*:is(.a)) &` -- so it weighs nothing
    // and cannot outrank an at-rule that also weighs nothing. Falling through to
    // the single-class default let it win outright and pick the lozenge.
    {
      code: '<div className="w-8 h-8 md:w-8 in-[.a]:w-4 md:in-[.a]:rounded-full" />',
    },
    // `in-range:` is a genuine pseudo-class that happens to start with the same
    // three characters, and weighs a full class. Testing for the bracket keeps
    // the two apart. Raised by Hicks.
    {
      code: '<div className="w-8 h-8 hover:w-4 in-range:w-8 in-range:hover:rounded-full" />',
    },
    // A payload can carry a class *and* a type. When the class columns *tie*, the
    // type decides and the payload wins outright -- see the `[section_&]` and
    // `[.a_*|section_&]` pins above. When a higher column has already settled it,
    // the type is never consulted, which is the invalid pin below.
    {
      code: '<div className="w-8 h-8 hover:w-4 group-[.a_section]:w-8 group-[.a_section]:hover:rounded-full" />',
    },
    // `:host()` and `:host-context()` weigh their most specific entry, not its
    // sum, exactly as `:is()` does. Summing made `:is(.a,.b,.c)` inside one four
    // classes where CSS says two, which beat the `.x.y.z` ancestor that really
    // wins and picked `w-4`: a false 16x32. Raised by Vasquez; the descendant
    // spelling is Hicks's, because `[&:host(...)]` cannot match a shadow host at
    // all and so pins nothing. `:host()` itself takes a single
    // `<compound-selector>` rather than a list, so the list is written inside an
    // `:is()` -- spelled `:host(.a,.b,.c)` this pinned behaviour on a selector
    // Edge rejects outright. Also raised by Hicks.
    {
      code: '<div className="w-8 h-8 [:host(:is(.a,.b,.c))_&]:w-4 [.x.y.z_&]:w-8 [:host(:is(.a,.b,.c))_&]:[.x.y.z_&]:rounded-full" />',
    },
    // ...and they weigh a class for *themselves* on top of that entry, so
    // `:host(.a)` is two, which is what beats a one-class `md:hover:` that would
    // otherwise win the segment-count tie-break and pick `w-4`.
    {
      code: '<div className="w-8 h-8 md:hover:w-4 [:host(.a)_&]:w-8 md:hover:[:host(.a)_&]:rounded-full" />',
    },
    // A `:where()` spelled in capitals is the same zero-specificity wrapper --
    // pseudo-class names are ASCII case-insensitive -- so it has to excuse the
    // square its lowercase twin above excuses. A case-sensitive guard let the
    // flat counter tally the wrapper's contents and report it. Raised by Hicks.
    {
      code: '<div className="w-8 h-8 [&_:WHERE(.a,.b)]:w-4 [&_:WHERE(.a,.b)]:rounded-full" />',
    },
    // A numeric call is only collapsed at an argument count CSS accepts, so a
    // well-formed one still excuses.
    {
      code: '<div className="w-[calc(8px*pow(2,2))] h-[calc(8px*pow(2,2))] rounded-full" />',
    },
    {
      code: '<div className="w-[calc(8px*log(8,2))] h-[calc(8px*log(8,2))] rounded-full" />',
    },
    {
      code: '<div className="w-[calc(8px*log(8))] h-[calc(8px*log(8))] rounded-full" />',
    },
    // Only a *bare* dimension is type-checked: units inside an argument may
    // cancel, and `sin(1px/1px)` is a legal dimensionless number. Scanning every
    // dimension token in the argument rejected it, left both axes opaque and
    // reported a square that is square by construction. Raised by Vasquez.
    {
      code: '<div className="w-[calc(1px*sin(1px/1px))] h-[calc(1px*sin(1px/1px))] rounded-full" />',
    },
    // The `:has()` family carries the same missing type-selector column as the
    // bare and `group-` payloads, and is left unrankable for the same reason.
    {
      code: '<div className="w-8 h-8 md:print:w-4 has-[section]:w-8 md:print:has-[section]:rounded-full" />',
    },
    // A `:where()` anywhere inside a variant zeroes part of the selector, and the
    // sorted condition key has already discarded the order that decides how much,
    // so the condition is unrankable however it is spelled -- including inside a
    // named arbitrary payload, which is not weighed as a selector at all.
    {
      code: '<div className="w-8 h-8 aria-[:where(.a)]:w-4 aria-[:where(.a)]:rounded-full" />',
    },
    // The other half of the `:is()` and `has-` weights, so that "weigh the
    // argument" cannot be mistaken for "weigh nothing": here the functional
    // pseudo genuinely is the more specific side and the lozenge is real.
    // These two sit in the invalid block; their presence is noted here because
    // the pairs only discriminate together.
    //
    // `calc()` subtraction. The bare-identifier branch of the arbitrary-value
    // tokeniser used to match a standalone `-`, so these two identical 31px axes
    // were rejected as incomparable and a real circle was reported -- while the
    // `+` spelling, which the pattern never matched at all, was fine. Raised by
    // Hicks. Both are pinned so the asymmetry cannot come back.
    {
      code: '<div className="w-[calc(32px_-_1px)] h-[calc(32px_-_1px)] rounded-full" />',
    },
    {
      code: '<div className="w-[calc(32px_+_1px)] h-[calc(32px_+_1px)] rounded-full" />',
    },
    // `:is()` standing alone as its own compound is a selector *list*, not a
    // predicate, so its argument is the subject: `[:is(&_section)]` emits
    // `:is(& section)` and targets the `<section>` exactly as `[&_section]` does.
    // Stripping the payload hid the combinator and the host's dimensions were
    // used to condemn a descendant. Raised by Hicks.
    {
      code: '<div className="w-4 h-8 aspect-square [:is(&_section)]:rounded-full" />',
    },
    {
      code: '<div className="w-4 h-8 aspect-square [:where(&_section)]:rounded-full" />',
    },
    // A *bare* negative length is invalid on `width`/`height`, so CSS drops the
    // declaration and the base survives as a proven 32x32 circle. The computed
    // form behaves differently and is pinned in `invalid`. Raised by Hicks.
    { code: '<div className="w-8 h-8 hover:h-[-1rem] hover:rounded-full" />' },
    // Unranked ties rather than losing: were the mixed `:where()` weighed as zero
    // it would lose to the base and leave a stale 16px height contradicting a real
    // 32x32. Ties union the two heights instead, and the matching one is accepted.
    {
      code: '<div className="w-8 h-4 [:where(&:hover)]:md:h-8 [:where(&:hover)]:md:rounded-full" />',
    },
    // A `#` inside a quoted attribute value is not an id: weighed in the id column
    // the attribute variant outranks `hover:focus:` and its stale 16px width
    // condemns a genuine 32x32. Raised by Vasquez.
    {
      code: "<div className=\"h-8 [&[href='#top']]:w-4 hover:focus:w-8 [&[href='#top']]:hover:focus:rounded-full\" />",
    },
    // A `:where()` combined with another variant is order-dependent: whether the
    // later variant lands inside the wrapper or outside it decides the weight, and
    // the sorted condition key has discarded that order. Weighing it as zero
    // reported this genuine 32x32. Raised by Hicks.
    {
      code: '<div className="[:where(&:focus)]:w-8 [:where(&:focus)]:h-4 [:where(&:focus)]:hover:h-8 [:where(&:focus)]:hover:rounded-full" />',
    },
    // An arbitrary variant is a whole selector, not one class: `[.a.b.c_&]` weighs
    // three classes and beats `hover:focus:`, so this really is a 32x32 circle
    // under an `.a.b.c` ancestor. Counting it as one selector reported it.
    // Raised by Hicks.
    {
      code: '<div className="[.a.b.c_&]:w-8 [.a.b.c_&]:h-8 hover:focus:w-8 hover:focus:h-4 [.a.b.c_&]:hover:focus:rounded-full" />',
    },
    // A valid `min()` of two lengths withdraws the width the base declared rather
    // than letting that stale 4px contradict `aspect-square`. Raised by Hicks.
    {
      code: '<div className="aspect-square w-[4px] h-[32px] hover:w-[min(32px,64px)] hover:rounded-full" />',
    },
    // `:where()` contributes no specificity at all, so the bare `w-4`/`h-4` pair
    // wins and this really is a 16x16 circle. Raised by Hicks.
    {
      code: '<div className="w-4 h-4 [:where(&:hover)]:w-8 [:where(&:hover)]:rounded-full" />',
    },
    // Named breakpoint range variants are at-rules exactly as `md:` is, so they
    // add no specificity and `hover:` still wins. Raised by Hicks.
    {
      code: '<div className="h-4 max-lg:max-md:w-8 hover:w-4 max-lg:max-md:hover:rounded-full" />',
    },
    // An id outranks any number of classes and pseudo-classes, because CSS weighs
    // ids in their own column, so the `[#id]` pair wins and this is a circle.
    // Raised by Vasquez.
    {
      code: '<div className="hover:focus:w-8 hover:focus:h-4 [#id]:w-8 [#id]:h-8 hover:focus:[#id]:rounded-full" />',
    },

    // Raised by Hicks. `inherit`, `revert` and `revert-layer` expose a parent or
    // lower-cascade ratio the rule cannot see, so under the reading agreed with
    // Bishop and Vasquez they are unreadable and leave the coarser proof intact.
    // `initial`/`unset` differ: both compute to `auto`, a *readable* removal, and
    // stay pinned as reports below.
    ...["inherit", "revert", "revert-layer"].map((keyword) => ({
      code: `<div className="w-8 aspect-square hover:aspect-[${keyword}] hover:rounded-full" />`,
    })),
  ],

  invalid: [
    // The counterparts to the `DROPPED` and percentage-height cases above, so
    // neither fix can quietly widen into a false negative. `hover:h-auto` *is*
    // applied and does unpin the axis, unlike the discarded `hover:h-[banana]`;
    // a percentage width stays definite, so a 100%-by-32px box is still a
    // lozenge; and a scalar-only pair proves nothing on either axis.
    ...[
      "w-8 h-8 hover:h-auto hover:rounded-full",
      "w-[calc(2)] h-[calc(2)] rounded-full",
      "w-[1rem] h-[16px] rounded-full",
      "w-8 h-8 *:w-4 *:h-8 *:rounded-full",
      // The counterpart to the scope fix: `[&:hover]` is this element in a
      // state, so its own 16x32 pair still condemns. Raised by Hicks; keying the
      // scope test on `&` had let this escape.
      "w-4 h-8 aspect-square [&:hover]:rounded-full",
      // Unitless is not a length, so both axes are dropped and nothing is
      // proved -- the identity shortcut above must not rescue it.
      "w-[7] h-[7] rounded-full",
    ].map((code) => {
      const token = code
        .split(/\s+/)
        .find((part) => part.endsWith("rounded-full"));
      return {
        code: `<div className="${code}" />`,
        output: null,
        errors: [
          {
            messageId: "fullRound",
            data: { token },
            suggestions: [
              {
                messageId: "replaceWithLg",
                output: `<div className="${code.replace(`${token}`, token.replace("rounded-full", "rounded-lg"))}" />`,
              },
            ],
          },
        ],
      };
    }),
    // The scope restriction cuts only across scopes: same-scope evidence still
    // condemns, so a descendant proved to be a lozenge is still reported.
    {
      code: '<div className="[&_img]:w-4 [&_img]:h-8 [&_img]:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "[&_img]:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="[&_img]:w-4 [&_img]:h-8 [&_img]:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // The other half of the round-seventeen argument weights. Weighing a
    // functional pseudo's most specific argument has to be able to make it *win*
    // as well as lose, or "weigh the argument" collapses into "weigh nothing" and
    // the valid pins above would pass with the specificity model deleted.
    // `:is(.a.b.c)` is three classes and really does beat `.x.y`, and
    // `has-[.a]` is one class and really does lose to `hover:focus:`, so both of
    // these are genuine lozenges.
    {
      code: '<div className="x y w-8 h-8 [&:is(.a.b.c)]:w-4 [.x.y_&]:w-8 [&:is(.a.b.c)]:[.x.y_&]:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "[&:is(.a.b.c)]:[.x.y_&]:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="x y w-8 h-8 [&:is(.a.b.c)]:w-4 [.x.y_&]:w-8 [&:is(.a.b.c)]:[.x.y_&]:rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="w-8 h-8 has-[.a]:w-8 hover:focus:w-4 has-[.a]:hover:focus:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "has-[.a]:hover:focus:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-8 h-8 has-[.a]:w-8 hover:focus:w-4 has-[.a]:hover:focus:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // The same pairing for the round-eighteen weights: `group-[.a]` is one class
    // and really does lose to `hover:focus:`, and a `:nth-child(... of .a.b.c)`
    // is four columns' worth and really does beat `.x.y.z`. Without these, both
    // valid pins above would pass with the weights deleted.
    {
      code: '<div className="w-8 h-8 group-[.a]:w-8 hover:focus:w-4 group-[.a]:hover:focus:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "group-[.a]:hover:focus:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-8 h-8 group-[.a]:w-8 hover:focus:w-4 group-[.a]:hover:focus:rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="x y z w-8 h-8 [&:nth-child(2_of_.a.b.c)]:w-4 [.x.y.z_&]:w-8 [&:nth-child(2_of_.a.b.c)]:[.x.y.z_&]:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "[&:nth-child(2_of_.a.b.c)]:[.x.y.z_&]:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="x y z w-8 h-8 [&:nth-child(2_of_.a.b.c)]:w-4 [.x.y.z_&]:w-8 [&:nth-child(2_of_.a.b.c)]:[.x.y.z_&]:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // The other side of the numeric-collapse guards. `sign()` takes any type, so
    // `calc(1px*sign(2rem))` really is a 1px width against a 32px height; a
    // nested call is reached on a later pass, so `sin(sign(2rem))` collapses too;
    // and `round()`'s interval is optional, so `round(2)` is an ordinary 2 rather
    // than an unreadable call. Without these, refusing to collapse anything at
    // all would satisfy every valid pin above.
    {
      code: '<div className="w-8 h-8 hover:w-[calc(1px*sign(2rem))] hover:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-8 h-8 hover:w-[calc(1px*sign(2rem))] hover:rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="w-8 h-8 hover:w-[calc(1px*sin(sign(2rem)))] hover:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-8 h-8 hover:w-[calc(1px*sin(sign(2rem)))] hover:rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="w-8 h-8 hover:w-[calc(1px*round(2))] hover:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-8 h-8 hover:w-[calc(1px*round(2))] hover:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // An empty argument list is a parse error, not a zero-argument call, so
    // `sin()` drops the declaration and leaves `w-4 h-8` standing. Collapsing it
    // to a definite `32px` on both axes excused this real 16x32 lozenge -- the
    // one direction the type guard alone does not cover. Raised by Hicks.
    {
      code: '<div className="w-4 h-8 hover:w-[calc(32px*sin())] hover:h-[calc(32px*sin())] hover:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 hover:w-[calc(32px*sin())] hover:h-[calc(32px*sin())] hover:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // ...and a substituted argument stays unprovable when the two axes are not
    // the same expression, so dropping the `var()` guard did not buy the excuse
    // above by giving up this report.
    {
      code: '<div className="w-[calc(32px*sin(var(--a)))] h-[32px] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-[calc(32px*sin(var(--a)))] h-[32px] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // A compound argument to a numeric function still has to type-check:
    // `sin(1px*2)` resolves to a length where CSS wants a `<number>` or an
    // `<angle>`, so the declaration is dropped and the lozenge below it stands.
    // Checking only bare dimensions waved it through and excused this 16x32.
    // Raised by Hicks; `sin(1px/1px)`, which is dimensionless and valid, is
    // pinned as excusing above.
    {
      code: '<div className="w-4 h-8 hover:w-[calc(32px*sin(1px*2))] hover:h-[calc(32px*sin(1px*2))] hover:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 hover:w-[calc(32px*sin(1px*2))] hover:h-[calc(32px*sin(1px*2))] hover:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Tailwind emits `group-nth-[1_of_.a.b.c]` as `:where(.group):nth-child(1 of
    // .a.b.c)`, four classes' worth, which outranks the three-class `[.x.y.z_&]`
    // and picks `w-4`. Matching the `nth` payload only when it stands alone left
    // the compound `group-`/`peer-` forms unread and excused this 16x32. Raised
    // by Hicks.
    {
      code: '<div className="w-8 h-8 group-nth-[1_of_.a.b.c]:w-4 [.x.y.z_&]:w-8 group-nth-[1_of_.a.b.c]:[.x.y.z_&]:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "group-nth-[1_of_.a.b.c]:[.x.y.z_&]:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-8 h-8 group-nth-[1_of_.a.b.c]:w-4 [.x.y.z_&]:w-8 group-nth-[1_of_.a.b.c]:[.x.y.z_&]:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Two identically spelled values are only proof of a square while the browser
    // keeps them. These three are all provably dropped -- juxtaposed lengths and
    // a `calc()` whose addends disagree in type -- so the `w-4 h-8` pair
    // underneath stands and the radius is a real 16x32 lozenge. Reading equality
    // from spelling alone excused all of them. Raised by Hicks; the line between
    // provably dropped and merely unreadable is Vasquez's. An unspaced `+` was a
    // fourth entry here until Hicks showed Tailwind normalises the spacing; it is
    // now pinned as valid.
    {
      code: '<div className="w-4 h-8 hover:w-[calc(1px_2px)] hover:h-[calc(1px_2px)] hover:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 hover:w-[calc(1px_2px)] hover:h-[calc(1px_2px)] hover:rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="w-4 h-8 hover:w-[calc(1px_+_1)] hover:h-[calc(1px_+_1)] hover:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 hover:w-[calc(1px_+_1)] hover:h-[calc(1px_+_1)] hover:rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="w-4 h-8 hover:w-[calc(32px*sin(1px_+_1))] hover:h-[calc(32px*sin(1px_+_1))] hover:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 hover:w-[calc(32px*sin(1px_+_1))] hover:h-[calc(32px*sin(1px_+_1))] hover:rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="w-4 h-8 hover:w-[calc(32px*sin(1deg*1px))] hover:h-[calc(32px*sin(1deg*1px))] hover:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 hover:w-[calc(32px*sin(1deg*1px))] hover:h-[calc(32px*sin(1deg*1px))] hover:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // A repeated variant is repeated in the emitted selector too -- `hover:hover:`
    // is `&:hover:hover`, two classes -- so it beats a single `focus:` and picks
    // `w-4`. Collapsing the segments through a `Set` before ranking dropped it to
    // one class and excused this 16x32. Raised by Hicks, confirmed by Vasquez.
    {
      code: '<div className="w-4 h-8 hover:hover:w-4 focus:w-8 hover:hover:focus:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:hover:focus:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 hover:hover:w-4 focus:w-8 hover:hover:focus:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Tailwind composes variant prefixes recursively, so `not-nth-[1_of_.a.b.c]`
    // is `:not(:nth-child(1 of .a.b.c))` and weighs its argument: four classes,
    // which beats the three-class `[.x.y.z_&]` and picks `w-4`. Matching a fixed
    // set of prefixes per branch left every composition unread and excused this
    // 16x32. Raised by Hicks, who called the prefix regexes whack-a-mole, and
    // seconded by Vasquez.
    {
      code: '<div className="w-4 h-8 not-nth-[1_of_.a.b.c]:w-4 [.x.y.z_&]:w-8 not-nth-[1_of_.a.b.c]:[.x.y.z_&]:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "not-nth-[1_of_.a.b.c]:[.x.y.z_&]:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 not-nth-[1_of_.a.b.c]:w-4 [.x.y.z_&]:w-8 not-nth-[1_of_.a.b.c]:[.x.y.z_&]:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // `width` takes no negative length, so `-2rem` really is dropped on both axes
    // and nothing underneath proves a square. Bishop's negative control, pinned so
    // that admitting a leading `+` cannot quietly grow into admitting any sign.
    {
      code: '<div className="w-[-2rem] h-[-2rem] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="w-[-2rem] h-[-2rem] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // `in-focus-within:` and `not-print:` both add nothing to specificity --
    // Tailwind emits `:where(*:focus-within) &` for one and `@media not print`
    // for the other -- so the plain `focus:` beside them wins outright and pins
    // the width at 16 against a height of 32. Both were read as a class: `in-`
    // was zeroed only in its bracket spelling, and `not-` recursed into its
    // remainder without asking whether that remainder was an at-rule. Neither bug
    // was visible on a two-variant case, because the wrong weight of 1 ties with
    // `focus:` and ties are unioned into an excuse that happens to match the
    // browser. These pins put the winner on the other side of the tie. Raised by
    // Hicks; the discriminating shape is mine.
    {
      code: '<div className="w-8 h-8 in-focus-within:w-8 focus:w-4 in-focus-within:focus:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "in-focus-within:focus:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-8 h-8 in-focus-within:w-8 focus:w-4 in-focus-within:focus:rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="w-8 h-8 not-print:w-8 focus:w-4 not-print:focus:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "not-print:focus:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-8 h-8 not-print:w-8 focus:w-4 not-print:focus:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // The media families the at-rule list had never covered. Each of these emits
    // `@media not (...)`, which carries no specificity, so the `focus:w-4` beside
    // it wins outright and the element really is 16x32 when the radius applies.
    // Weighing them as ordinary classes produced a tie instead, and `resolve`
    // unions an equal-rank tie disjunctively -- which silently excused the lot.
    // Raised by Hicks. The list was rebuilt by compiling every `not-*` form
    // against the app's own Tailwind rather than from memory.
    {
      code: '<div className="w-8 h-8 not-noscript:w-8 focus:w-4 not-noscript:focus:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "not-noscript:focus:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-8 h-8 not-noscript:w-8 focus:w-4 not-noscript:focus:rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="w-8 h-8 not-inverted-colors:w-8 focus:w-4 not-inverted-colors:focus:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "not-inverted-colors:focus:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-8 h-8 not-inverted-colors:w-8 focus:w-4 not-inverted-colors:focus:rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="w-8 h-8 not-pointer-coarse:w-8 focus:w-4 not-pointer-coarse:focus:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "not-pointer-coarse:focus:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-8 h-8 not-pointer-coarse:w-8 focus:w-4 not-pointer-coarse:focus:rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="w-8 h-8 not-any-pointer-fine:w-8 focus:w-4 not-any-pointer-fine:focus:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "not-any-pointer-fine:focus:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-8 h-8 not-any-pointer-fine:w-8 focus:w-4 not-any-pointer-fine:focus:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // The bracket spelling negates an at-rule too -- `@media not (pointer:fine)`
    // -- but arrives by a different branch, where it was being read as a
    // `:not()` selector and bought a class it never has.
    {
      code: '<div className="w-8 h-8 not-[@media(pointer:fine)]:w-8 focus:w-4 not-[@media(pointer:fine)]:focus:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "not-[@media(pointer:fine)]:focus:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-8 h-8 not-[@media(pointer:fine)]:w-8 focus:w-4 not-[@media(pointer:fine)]:focus:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Outside a math function Tailwind performs no repair: `w-[1px+]` is emitted
    // verbatim as `width: 1px+`, which the browser drops, so the element keeps
    // its 16x32 base size under focus. Extending the sign amnesty to every value
    // rather than to `calc()` bodies alone silenced this. Raised by Hicks, and
    // the counterweight to the `calc(2rem+4px)` pin above: the same sign means
    // opposite things depending on whether Tailwind rewrites the value.
    {
      code: '<div className="w-4 h-8 focus:w-[1px+] focus:h-[1px+] focus:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "focus:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 focus:w-[1px+] focus:h-[1px+] focus:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // A live at-rule negation is the contrast case for the inert ones pinned
    // valid above: these really do emit, so the radius really is on the page.
    {
      code: '<div className="w-4 h-8 not-[@media(pointer:fine)]:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "not-[@media(pointer:fine)]:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 not-[@media(pointer:fine)]:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Dropping an inert class has to cost it its *evidence* as well as its
    // radius. Left in, a dead `not-starting:w-8 not-starting:h-8` pair excused a
    // box that is really 16x32 -- the same defect as reporting a dead radius,
    // only pointing the other way.
    {
      code: '<div className="w-4 h-8 not-starting:w-8 not-starting:h-8 rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 not-starting:w-8 not-starting:h-8 rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Tailwind's repair inserts whitespace; it cannot supply a missing operand.
    // `calc(1px_+_)` emits `calc(1px + )`, which Chromium drops, so the amnesty
    // requires the sign to sit between two operands. Raised by Hicks.
    {
      code: '<div className="w-4 h-8 focus:w-[calc(1px_+_)] focus:h-[calc(1px_+_)] focus:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "focus:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 focus:w-[calc(1px_+_)] focus:h-[calc(1px_+_)] focus:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // The same missing operand outside `calc()`. Chromium drops `min(1px + , 2px)`
    // and `abs(1px + )` exactly as it drops the `calc()` form, so the
    // operand-flanking check has to reach every math function, not just `calc()`.
    // These two are what make that breadth observable: narrowing the pattern back
    // to `calc` alone silences both, and a dropped declaration then reads as a
    // square.
    // A `/*` inside a quoted arbitrary value is two characters of a CSS string,
    // not the start of a comment, so this element is perfectly readable and its
    // 16x32 lozenge has to be reported. Suppressing on the bare character pair
    // silenced it. Raised by Hicks.
    {
      code: "<div className=\"w-4 h-8 rounded-full before:content-['/*']\" />",
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                "<div className=\"w-4 h-8 rounded-lg before:content-['/*']\" />",
            },
          ],
        },
      ],
    },
    // The glued spellings Tailwind does not repair. Chromium drops all three, so
    // neither axis is pinned and the radius cannot be excused.
    {
      code: '<div className="size-[abs(1px+2px)] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="size-[abs(1px+2px)] rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="size-[abs(1px_-2px)] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="size-[abs(1px_-2px)] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Tailwind's repair inserts spaces but cannot make a lone `.` into a number,
    // so `calc(1px+.)` is spaced into `calc(1px + .)` and dropped. Raised by
    // Hicks.
    {
      code: '<div className="size-[calc(1px+.)] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="size-[calc(1px+.)] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // A comment stripped out of an arbitrary value leaves a space behind, and CSS
    // reads `[/**\/-2px]` exactly as it reads `[-2px]` -- both are invalid on an
    // axis and both drop. The negative-drop gate was anchored hard at `^`, so the
    // commented spelling slipped past it and was read as a live length, and the
    // rule then contradicted itself about one declaration. Raised by Bishop, who
    // proposed trimming both ends instead; that would have been wrong, because
    // Hicks's `h-[/**\/length:1rem]` pin above proves the leading space is doing
    // real work. The gate learned to skip leading whitespace instead, which is
    // what CSS itself does, and both cases now hold at once.
    {
      code: '<div className="size-[/**/-2px] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="size-[/**/-2px] rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="w-[/**/-2px] h-[/**/-2px] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-[/**/-2px] h-[/**/-2px] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // The escaped quote opens no string, so Vasquez is right that the `/*`
    // behind it is a real comment -- but only when nothing intervenes. Inside a
    // repairable function Tailwind spaces the operators first, emitting
    // `calc(1px\" / *)`, and a `/` followed by a space starts no comment. The
    // sibling pin in the valid list keeps the bare `[\\"/*]` spelling suppressed;
    // this one proves the suppression is not blanket.
    {
      code: "<div className='w-4 h-8 rounded-full w-[calc(1px\\\"/*)]' />",
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                "<div className='w-4 h-8 rounded-lg w-[calc(1px\\\"/*)]' />",
            },
          ],
        },
      ],
    },
    // The mirror of the `abs(calc(...))` pin in the valid list: here the glued
    // operator sits under `abs`, which Tailwind emits verbatim, so Chromium drops
    // the value, both axes fall back to `w-4 h-8` and the lozenge is real.
    {
      code: '<div className="w-4 h-8 rounded-full size-[calc(abs(1px+2px))]" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 rounded-lg size-[calc(abs(1px+2px))]" />',
            },
          ],
        },
      ],
    },
    // The mirror of `calc((16px_+16px))` in the valid list: the same bare group
    // under `abs()` is emitted verbatim and dropped, so inheritance has to work
    // downwards as well as upwards.
    {
      code: '<div className="w-4 h-8 rounded-full size-[abs((1px+2px))]" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 rounded-lg size-[abs((1px+2px))]" />',
            },
          ],
        },
      ],
    },
    // A sign between two bare identifiers is emitted glued: `calc((e+pi)*1px)`
    // becomes `calc((e+pi) * 1px)`, which Chromium rejects outright, so the element
    // has no size and its `rounded-full` is unproven. Spacing it invented a valid
    // 5.86px square and excused a zero-height element as a circle. This is what
    // forced the repair model to be transcribed from the emitter rather than
    // derived by hand a fourth time. Raised by Hicks.
    {
      code: '<div className="size-[calc((e+pi)*1px)] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="size-[calc((e+pi)*1px)] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // A semicolon has no place in a math function's grammar, so the browser drops
    // the declaration entirely and the element renders unsized -- measured at
    // 1264x0 with the radius still applied. The declarations around it survive:
    // `width: calc(1px+2px;3px); height: 1px` really is 1264x1, so this is one
    // dropped value and not a truncation that poisons what follows. Nothing else
    // in the model catches it -- no juxtaposition, no dangling sign, and a call is
    // present, so the value walked out innocent. Raised by Hicks.
    {
      code: '<div className="size-[calc(1px+2px;3px)] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="size-[calc(1px+2px;3px)] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Two `!important`s in one value is not important twice, it is an invalid
    // declaration: `width: 16px!important!important` is dropped and the element
    // measures 1264x32. This pair is what makes reading from the last `!` and
    // reading from the first equivalent: whichever one is chosen, an unescaped
    // `!` is left behind in the other half and the declaration is invalid either
    // way. The exception is an *escaped* `!`, which is a literal character and
    // is skipped -- see the `var(--x\!y,32px)!important` pin below.
    {
      code: '<div className="size-[16px!important!important] h-[32px] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="size-[16px!important!important] h-[32px] rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="size-[16px!important_!important] h-[32px] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="size-[16px!important_!important] h-[32px] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Two `!important`s in one value is not important twice, it is an invalid
    // declaration: it is dropped, `w-`/`h-` win uncontested, and the element
    // measures 32x64 -- a lozenge, correctly reported. The regex this pin was
    // originally written against is gone: the importance check now reads the
    // value rather than pattern-matching it, so there is no head to be greedy or
    // lazy about, and the argument that used to be recorded here was answered by
    // measurement in a later round.
    {
      code: '<div className="size-[16px!important!important] w-[32px] h-[64px] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="size-[16px!important!important] w-[32px] h-[64px] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // A comment that never closes does not make the radius unreadable. The value
    // text lies entirely left of the `/*`, so Chromium applies it: these measure
    // 64x32 with r=9999px and r=16px respectively -- lozenges, drawn, and until
    // now silently excused because the escaping-comment guard skipped the whole
    // element. Found while checking Vasquez's round-38 claim that the
    // unterminated spelling was safe; it is safe from false reports, which is
    // what he checked, but it was hiding real ones.
    {
      code: '<div className="rounded-[9999px/*c] w-[64px] h-[32px]" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-[9999px/*c]" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="rounded-lg w-[64px] h-[32px]" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="rounded-[16px/*c] w-[64px] h-[32px]" />',
      output: null,
      errors: [
        {
          messageId: "oversized",
          data: { token: "rounded-[16px/*c]", px: "16", max: "8" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="rounded-lg w-[64px] h-[32px]" />',
            },
          ],
        },
      ],
    },
    // The reverse order, where the escaper is the oversized one: its own value is
    // left of the comment and survives, the following `rounded-[8px]` is eaten,
    // and the element really is a 64x32 lozenge with a 9999px radius.
    {
      code: '<div className="rounded-[9999px/*c] rounded-[8px] w-[64px] h-[32px]" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-[9999px/*c]" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="rounded-lg rounded-[8px] w-[64px] h-[32px]" />',
            },
          ],
        },
      ],
    },
    // A six-digit hex escape above U+10FFFF is legal CSS and illegal input to
    // `String.fromCodePoint`, so this threw a `RangeError` mid-lint until the
    // decoder replaced out-of-range points with U+FFFD as CSS Syntax 4.3.7 says.
    // The pin is the report, but the regression it guards is the crash. The
    // element measures 1264x32 -- the width declaration is invalid and dropped,
    // so it really is a lozenge. Raised by Hicks, with the stack trace.
    {
      code: '<div className="size-[16px!\\110000] h-[32px] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="size-[16px!\\110000] h-[32px] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // The `h-` twin of the interior-comment case: measured 16x32, a lozenge, and
    // excused until the padded value was re-trimmed.
    {
      code: '<div className="size-[16px] h-[32px/*c*/!important] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="size-[16px] h-[32px/*c*/!important] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // An escaped `!` is a literal character, not a delimiter, and it survives
    // comment stripping -- which is what falsified the first-vs-last equivalence
    // claimed in round 38. Measured 16x32. Reading from the first `!` finds the
    // escaped one and lets this pass. Raised by Hicks.
    {
      code: '<div className="h-[var(--x\\!y,32px)!important] w-[16px] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="h-[var(--x\\!y,32px)!important] w-[16px] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Four spellings CSS accepts and this rule read as unresolvable, so all four
    // were silently excused: units are case-insensitive, `+` is a valid sign,
    // scientific notation is a valid `<number>`, and `_` is Tailwind's space.
    // Measured 16px, 10px, 16px and 16px. Raised by Hicks.
    {
      code: '<div className="rounded-[16PX]" />',
      output: null,
      errors: [
        {
          messageId: "oversized",
          data: { token: "rounded-[16PX]", px: "16", max: "8" },
          suggestions: [
            { messageId: "replaceWithLg", output: '<div className="rounded-lg" />' },
          ],
        },
      ],
    },
    {
      code: '<div className="rounded-[1e1px]" />',
      output: null,
      errors: [
        {
          messageId: "oversized",
          data: { token: "rounded-[1e1px]", px: "10", max: "8" },
          suggestions: [
            { messageId: "replaceWithLg", output: '<div className="rounded-lg" />' },
          ],
        },
      ],
    },
    {
      code: '<div className="rounded-[+16px]" />',
      output: null,
      errors: [
        {
          messageId: "oversized",
          data: { token: "rounded-[+16px]", px: "16", max: "8" },
          suggestions: [
            { messageId: "replaceWithLg", output: '<div className="rounded-lg" />' },
          ],
        },
      ],
    },
    {
      code: '<div className="rounded-[_16px_]" />',
      output: null,
      errors: [
        {
          messageId: "oversized",
          data: { token: "rounded-[_16px_]", px: "16", max: "8" },
          suggestions: [
            { messageId: "replaceWithLg", output: '<div className="rounded-lg" />' },
          ],
        },
      ],
    },
    // A comment *after* the data-type hint leaves the hint where Tailwind looks
    // for it, so this is a real 16px radius. Its mirror image, with the comment
    // first, computes to 0 and is pinned valid.
    {
      code: '<div className="rounded-[length:/**/16px]" />',
      output: null,
      errors: [
        {
          messageId: "oversized",
          data: { token: "rounded-[length:/**/16px]", px: "16", max: "8" },
          suggestions: [
            { messageId: "replaceWithLg", output: '<div className="rounded-lg" />' },
          ],
        },
      ],
    },
    // A comment in a radius value is a comment, not an unreadable value. Every
    // other value reader in this file already stripped them; `arbitraryToPx` did
    // not, so it returned `null` and waved the site through. All four of these
    // apply a real radius in Chromium -- 16px, 16px, 9999px and 16px -- and all
    // four were silently excused. Found by Vasquez through the artifact a
    // comment-bearing `!important` leaves behind, though the plain spelling on
    // the first line shows the gap had nothing to do with importance.
    {
      code: '<div className="rounded-[16px/*c*/]" />',
      output: null,
      errors: [
        {
          messageId: "oversized",
          data: { token: "rounded-[16px/*c*/]", px: "16", max: "8" },
          suggestions: [
            { messageId: "replaceWithLg", output: '<div className="rounded-lg" />' },
          ],
        },
      ],
    },
    {
      code: '<div className="rounded-[/*c*/16px]" />',
      output: null,
      errors: [
        {
          messageId: "oversized",
          data: { token: "rounded-[/*c*/16px]", px: "16", max: "8" },
          suggestions: [
            { messageId: "replaceWithLg", output: '<div className="rounded-lg" />' },
          ],
        },
      ],
    },
    {
      code: '<div className="rounded-[length:16px/*c*/]" />',
      output: null,
      errors: [
        {
          messageId: "oversized",
          data: { token: "rounded-[length:16px/*c*/]", px: "16", max: "8" },
          suggestions: [
            { messageId: "replaceWithLg", output: '<div className="rounded-lg" />' },
          ],
        },
      ],
    },
    {
      code: '<div className="rounded-[16px/*!important*/!important]" />',
      output: null,
      errors: [
        {
          messageId: "oversized",
          data: {
            token: "rounded-[16px/*!important*/!important]",
            px: "16",
            max: "8",
          },
          suggestions: [
            { messageId: "replaceWithLg", output: '<div className="rounded-lg" />' },
          ],
        },
      ],
    },
    // Neither an escape nor a separator rescues a keyword that is not the
    // keyword. `!\importantx` decodes to `importantx` and `!imp_ortant` is
    // `imp ortant` -- two different identifiers, two invalid declarations, and
    // both measure 1264x32. The second is the discriminator against trimming
    // separators from anywhere in the keyword rather than only from its ends.
    {
      code: '<div className="size-[16px!\\importantx] h-[32px] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="size-[16px!\\importantx] h-[32px] rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="size-[16px!imp_ortant] h-[32px] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="size-[16px!imp_ortant] h-[32px] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // The mirror images of the two escape/comment circles in the valid list: an
    // important *height* against a plain `size-` width is a 16x32 lozenge, both
    // measured. These are what make the decoding load-bearing in the reporting
    // direction too, rather than only as a way of excusing things.
    {
      code: '<div className="size-[16px] h-[32px!important/*!important/**/] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="size-[16px] h-[32px!important/*!important/**/] rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="size-[16px] h-[32px!\\69mportant] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="size-[16px] h-[32px!\\69mportant] rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="size-[16px] h-[32px!\\69_mportant] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="size-[16px] h-[32px!\\69_mportant] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // `!importantx` is not `!important`: the declaration is invalid outright, so
    // `size-` supplies no width at all and the element measures 1264x32. This is
    // the discriminator against matching the keyword as a prefix, which would read
    // an important 16px here and excuse a real lozenge.
    {
      code: '<div className="size-[16px!importantx] h-[32px] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="size-[16px!importantx] h-[32px] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // `if()` is a function this rule cannot read, so it cannot prove a square --
    // the same documented limitation as `var()`, and nothing to do with the
    // semicolon it happens to contain. The pair below is the evidence for that
    // claim: the semicolon-free spelling reports identically, so the semicolon
    // condemnation being scoped to math calls is currently shadowed by this and
    // cannot be observed through the rule's verdicts. Both spellings measure
    // 16x16 in Chromium, so both of these reports are the rule failing toward
    // caution on a value it does not understand. Raised by Hicks; tracked as a
    // limitation rather than fixed here, because teaching the rule `if()` is a
    // new capability and not a repair.
    {
      code: '<div className="size-[if(style(--x:yes):16px;else:16px)] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="size-[if(style(--x:yes):16px;else:16px)] rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="size-[if(style(--x:yes):16px)] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="size-[if(style(--x:yes):16px)] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Importance read from inside the value cuts both ways. Here it is the height
    // that is important, so it beats the `size-` width-and-height pair on its own
    // axis only and the element measures 16x32 -- a lozenge that went unreported
    // while the marker was invisible. Raised by Hicks.
    {
      code: '<div className="size-[16px] h-[32px!important] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="size-[16px] h-[32px!important] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // The name scan stops at a hyphen, so `foo-calc(` is a `calc(` call, its
    // interior is repaired, and the `/*` never becomes a comment. Reading the name
    // as `foo-calc` withheld the repair, left an escaping comment in place, and
    // silently switched the whole check off for a live 16x32 lozenge. Raised by
    // Hicks.
    {
      code: '<div className="w-4 h-8 rounded-full before:w-[foo-calc(1px/*)]" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 rounded-lg before:w-[foo-calc(1px/*)]" />',
            },
          ],
        },
      ],
    },
    // Repair is case-sensitive: Tailwind emits `CALC(1px+2px)` exactly as written,
    // and Chromium rejects the glued sign, so the element has no size at all and
    // its `rounded-full` is unproven. Matching case-insensitively invented a valid
    // 3px square and excused it. The pin has to be unconditional -- an earlier
    // `focus:` spelling proved nothing, because the base state was already a
    // lozenge and reported whatever the variant did. Raised by Hicks.
    {
      code: '<div className="size-[CALC(1px+2px)] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="size-[CALC(1px+2px)] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // A quoted paren does not make the value unbalanced, so the element is judged
    // rather than skipped, and it is a 16x32 lozenge. Raised by Hicks.
    {
      code: "<div className=\"before:content-['('] w-4 h-8 rounded-full\" />",
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                "<div className=\"before:content-['('] w-4 h-8 rounded-lg\" />",
            },
          ],
        },
      ],
    },
    // The other side of the exclusion set: an operand precedes this `/`, so it is
    // spaced to `calc(1px / *2)`, no comment survives, and the value drops on the
    // trailing `*` with nothing to multiply. The element is judged, and it is a
    // 4x8 lozenge.
    {
      code: '<div className="w-4 h-8 rounded-full size-[calc(1px/*2)]" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 rounded-lg size-[calc(1px/*2)]" />',
            },
          ],
        },
      ],
    },
    // Repair has to run *before* comments are stripped, because repair is what
    // decides whether a comment is there at all. `calc(1px/**\/+2px)` is emitted
    // as `calc(1px / **\/+2px)` -- no comment, and dropped, because the `/` has
    // no operand after it. Stripping first invented the valid sum `calc(1px + 2px)`
    // and excused this lozenge. Raised by Hicks.
    {
      code: '<div className="w-4 h-8 rounded-full size-[calc(1px/**/+2px)]" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 rounded-lg size-[calc(1px/**/+2px)]" />',
            },
          ],
        },
      ],
    },
    // Inertness has to recurse through a negation as well as into one:
    // `not-group-print:` emits nothing, so the width it carries never lands and
    // the element stays a 16x32 lozenge. Stopping the recursion at the `not-`
    // head honoured the width and silenced a real report.
    {
      code: '<div className="w-4 h-8 rounded-full not-group-print:w-8" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 rounded-lg not-group-print:w-8" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="size-[min(1px_+_,2px)] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="size-[min(1px_+_,2px)] rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="size-[abs(1px_+_)] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="size-[abs(1px_+_)] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // A comment between the sign and the digits splits them into two tokens, so
    // `+/**\/1px` is not a length and Chromium drops it. Spacing the comment
    // rather than deleting it is what keeps the two apart. Raised by Hicks.
    {
      code: '<div className="w-4 h-8 focus:w-[+/**/1px] focus:h-[+/**/1px] focus:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "focus:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 focus:w-[+/**/1px] focus:h-[+/**/1px] focus:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // A comment separates tokens rather than vanishing. The simple spelling
    // `1/**\/px` never reaches the comment strip -- `isPlausibleLength` reads the
    // raw text, sees a unitless non-zero `1`, and drops the declaration, which is
    // what Chromium does too. The spelling that *does* reach it is one where both
    // halves are already plausible: `calc(1/**\/0px)` is `calc(1 0px)`, which
    // Chromium drops, but deleting the comment instead of spacing it spliced it
    // into a valid `calc(10px)` and falsely proved a square -- hiding an oversized
    // radius. Raised by Vasquez; the reachable spelling was found by differential
    // probe after his own example turned out to be caught earlier. Every spelling
    // here was checked against Chromium, not against the tokenizer grammar.
    {
      code: '<div className="size-[calc(1/**/0px)] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="size-[calc(1/**/0px)] rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="size-[calc(1e/**/2px)] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="size-[calc(1e/**/2px)] rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="size-[1px/**/2px] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="size-[1px/**/2px] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Tailwind reads its data-type hints off the candidate before CSS sees it, so
    // `[/**\/length:1rem]` is not a hint -- it emits the literal
    // `width: /**\/length:1rem`, which Chromium drops. What keeps it dropped is
    // that the comment strip in `provablyInvalidValue` leaves a space behind and
    // does not trim, so the `^length:` anchor never matches and the bare `length`
    // ident is refused. Pinned so that a future tidy-up of that whitespace cannot
    // quietly turn a dropped declaration into a proof of squareness.
    {
      code: '<div className="size-[/**/length:1rem] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="size-[/**/length:1rem] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // The contrast case for the mixed-spelling valid pins above: stripping the
    // comment at the read must not also stop the rule seeing that 16 and 32 are
    // different numbers.
    {
      code: '<div className="w-[16px/**/] h-[32px] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="w-[16px/**/] h-[32px] rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="size-[1/**/px] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="size-[1/**/px] rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="w-[1/**/px] h-[1/**/px] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="w-[1/**/px] h-[1/**/px] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Stripping the comment must not blind the comparison that follows: these
    // two are 1px and 2px and the element is a genuine 1x2.
    {
      code: '<div className="w-[1px/**/] h-[2px] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="w-[1px/**/] h-[2px] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Adding a length to a bare number is invalid, so both declarations drop and
    // the element has no width or height at all -- nothing proves a square. The
    // twins are identically spelled, which is the one path that reads a value
    // without typing it, so the invalidity has to be caught before equality is
    // consulted.
    {
      code: '<div className="w-[calc(1px_+_1)] h-[calc(1px_+_1)] rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-[calc(1px_+_1)] h-[calc(1px_+_1)] rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Tailwind spells a literal underscore `\_` and a descendant combinator `_`,
    // so `[.a\_b_&]` is the single class `.a_b` and carries no type at all.
    // Reading the escaped one as a combinator invented a type, which lifted the
    // payload a whole column above the `md:hover:` that beats it on segment
    // count, and excused this 16x32 lozenge.
    {
      code: '<div className="w-8 h-8 md:hover:w-4 [.a\\_b_&]:w-8 md:hover:[.a\\_b_&]:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "md:hover:[.a\\_b_&]:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-8 h-8 md:hover:w-4 [.a\\_b_&]:w-8 md:hover:[.a\\_b_&]:rounded-lg" />',
            },
          ],
        },
      ],
    },

    // classes and `group-[.a_section]:` is one class plus one type, so the class
    // column settles it before the type column is ever consulted and `w-4` wins:
    // a real 16x32 lozenge. Declaring every type-bearing payload unrankable
    // withdrew from that and excused it. Raised by Hicks.
    {
      code: '<div className="w-8 h-8 hover:focus:w-4 group-[.a_section]:w-8 group-[.a_section]:hover:focus:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "group-[.a_section]:hover:focus:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-8 h-8 hover:focus:w-4 group-[.a_section]:w-8 group-[.a_section]:hover:focus:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Tailwind's built-in `nth-[..]` emits `:nth-child(1 of .a.b.c)`, which is
    // four classes and beats a three-class arbitrary variant. Counting it as a
    // single class lost that comparison and excused a real 16x32 lozenge. Raised
    // by Hicks.
    {
      code: '<div className="w-8 h-8 nth-[1_of_.a.b.c]:w-4 [.x.y.z_&]:w-8 nth-[1_of_.a.b.c]:[.x.y.z_&]:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "nth-[1_of_.a.b.c]:[.x.y.z_&]:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-8 h-8 nth-[1_of_.a.b.c]:w-4 [.x.y.z_&]:w-8 nth-[1_of_.a.b.c]:[.x.y.z_&]:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // `dark:` is an at-rule here and carries no specificity, so the bare
    // `hover:w-4` outranks `dark:w-8` and the element is a 16x32 lozenge in dark
    // mode. Counting `dark` as a class inverted that. Raised by Hicks.
    {
      code: '<div className="w-8 h-8 hover:w-4 dark:w-8 dark:hover:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "dark:hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-8 h-8 hover:w-4 dark:w-8 dark:hover:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Raised by Hicks. `w-4 h-8` really is a 16x32 lozenge on hover: the pair is
    // definite, so the ratio never reaches used-value computation, and comparing
    // the two declarations' specificity was arbitrating a contest CSS does not
    // hold. Its companion sits in `valid` above.
    {
      code: '<div className="w-4 h-8 hover:aspect-square hover:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="w-4 h-8 hover:aspect-square hover:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Raised by Hicks: lexical equality is not physical equality. `w-full` and
    // `h-full` are 100% of two *different* containing-block axes, `screen` is
    // `100vw` on one and `100vh` on the other, fractions are likewise
    // axis-relative, and `fit`/`min`/`max` size to content. Each was proving
    // circularity from a coincidence of spelling.
    ...[
      "w-full h-full",
      "w-fit h-fit",
      "w-1/2 h-1/2",
      "w-screen h-screen",
      "w-[50%] h-[50%]",
      // Comparability survives an exact string match only when the value cannot
      // change meaning with the property it lands on. `cqw` and `cqh` differ by
      // spelling so they never match; `%` is axis-relative even when spelled the
      // same; `var()` can itself hold a percentage; a bare `calc(100% - 4px)`
      // inherits that; an unrecognised function is simply unknowable; and a
      // non-zero unitless number is not a length at all.
      "w-[10cqw] h-[10cqh]",
      // Each of these isolates one branch of the arbitrary-value walk, so no
      // branch can pass falsification by being shadowed by another: an
      // axis-dependent unit (`%`), a unit that is not a length at all (`deg`),
      // an unrecognised function over otherwise-valid operands (`atan2`, whose
      // only other tokens are comparable `rem` lengths, so the function branch is
      // the only thing that can refuse it), and a bare keyword (`fit-content`,
      // which is content-sized and so axis-relative). `env()` looks like a
      // candidate for the function branch and is not: it is indefinite, so it is
      // refused before the walk is reached and the pin would prove nothing.
      // Raised by Hicks, against an earlier draft of this pin. `sign()` used to
      // serve here and no longer can: round twenty-one collapses the numeric
      // functions to a scalar, so it is now refused as a unitless number instead.
      // `atan2()` returns an *angle* rather than a `<number>`, so it is
      // deliberately not collapsed and still reaches the function branch.
      "w-[10deg] h-[10deg]",
      "w-[atan2(2rem,1rem)] h-[atan2(2rem,1rem)]",
      // The collapse is confined to the functions that really do return a
      // `<number>`, and collapsing one does not turn the value into a length:
      // `sin(90deg)` is a bare `1`, and a percentage operand is still refused.
      "w-[sin(90deg)] h-[sin(90deg)]",
      "w-[sign(2rem)] h-[sign(2rem)]",
      "w-[calc(50%*sin(90deg))] h-[calc(50%*sin(90deg))]",
      // `atan2()` returns an angle, not a `<number>`, so multiplying a length by
      // it is invalid and CSS drops the declaration -- leaving no evidence, which
      // is not the same as evidence of a circle. Collapsing the angle-returning
      // functions along with the numeric ones would excuse this wrongly.
      "w-[calc(1px*atan2(2,1))] h-[calc(1px*atan2(2,1))]",
      // Over- and under-supplying a numeric function makes the whole declaration
      // invalid, so CSS drops it and there is no evidence either way. Collapsing
      // it regardless would manufacture a length from a value that never applied.
      "w-[calc(32px*sin(90deg,0deg))] h-[calc(32px*sin(90deg,0deg))]",
      "w-[calc(32px*pow(2))] h-[calc(32px*pow(2))]",
      // The rounding-strategy allowance is confined to arguments of a function
      // and to the four spellings CSS defines: a bare keyword at the top level
      // is still no length, and an invented strategy is still not one either.
      "w-[up] h-[up]",
      // A constant is only meaningful inside a math function, so a bare one is
      // no more a length than any other keyword. `env()` and `attr()` stay out
      // for the reason `var()` does: their contents are unknown and may be a
      // percentage, as `env(x,50%)` shows plainly.
      "w-[pi] h-[pi]",
      "w-[env(safe-area-inset-top)] h-[env(safe-area-inset-top)]",
      "w-[round(sideways,32px,1px)] h-[round(sideways,32px,1px)]",
      "w-[fit-content] h-[fit-content]",
      "w-[calc(100%-4px)] h-[calc(100%-4px)]",
      "w-[var(--x)] h-[var(--x)]",
      "w-[calc(var(--x)*2)] h-[calc(var(--x)*2)]",
      "w-[anchor-size(width)] h-[anchor-size(width)]",
      // An invalid ratio is dropped by CSS, so it leaves no evidence at all —
      // which is not the same as leaving evidence of a circle.
      "aspect-[-1/-1]",
      "aspect-[-2/1]",
      // Raised by Hicks: CSS's `<number>` grammar is narrower than JavaScript's.
      "aspect-[0x2/0x2]",
      "aspect-[banana]",
      "aspect-banana",
      // Raised by Hicks: `1.` is JavaScript's number grammar, not CSS's.
      "aspect-[1./1.]",
      // Raised by Hicks: `auto` is a standalone term in `auto || <ratio>` and may
      // appear at most once, so this is invalid and leaves no evidence.
      "aspect-[auto_auto]",
      "aspect-[auto]",
      // Raised by Hicks: a CSS-wide keyword is valid but says nothing about shape.
      "aspect-[initial]",
      // An important dimension on one axis does not manufacture squareness.
      "w-8! h-4",
      // The important reading wins the tie outright, and here it proves a lozenge.
      "aspect-video! aspect-square",
      "w-[2] h-[2]",
      // A unitless number in scientific notation is still not a length.
      "w-[1e2] h-[1e2]",
      "size-full",
      "size-fit",
      "size-min",
      "size-max",
      "size-auto",
    ].map((dims) => ({
      code: `<div className="${dims} rounded-full" />`,
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: `<div className="${dims} rounded-lg" />`,
            },
          ],
        },
      ],
    })),

    // An animation still loses to a pair *proved* to differ — a 4x8 spinner is a
    // lozenge — which is the line between this and the `animate-ping` valid case.
    {
      code: '<div className="animate-spin w-4 h-8 rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="animate-spin w-4 h-8 rounded-lg" />',
            },
          ],
        },
      ],
    },

    // `aspect-square`, by contrast, loses to any *definite* pair even when the
    // two axes are incomparable, because the cascade resolves width and height
    // before the ratio is consulted. This is the asymmetric half of Bishop's
    // percentage finding, and the reason his prescription was not taken whole: a
    // percentage *height* is conditionally indefinite, but a percentage *width*
    // has no such rule, so this stays a 100%-by-32px lozenge and stays reported.
    // Treating both axes alike would have excused it silently.
    {
      code: '<div className="aspect-square w-full h-8 rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="aspect-square w-full h-8 rounded-lg" />',
            },
          ],
        },
      ],
    },

    // Raised by Hicks: a later `aspect-*` replaces the ratio outright, so
    // `aspect-square hover:aspect-[2/1]` is a 2:1 box on hover and the pill is
    // real. `aspect-auto` counts as a replacement too — it removes the ratio.
    ...[
      "hover:aspect-[2/1]",
      "hover:aspect-video",
      "hover:aspect-auto",
      "hover:aspect-[4/2]",
      "hover:aspect-[0/1]",
      // Raised by Hicks: a CSS-wide keyword is valid, and `initial` computes to
      // `auto`, so it replaces the ratio exactly as `aspect-auto` does.
      "hover:aspect-[initial]",
    ].map((override) => ({
      code: `<div className="w-8 aspect-square ${override} hover:rounded-full" />`,
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: `<div className="w-8 aspect-square ${override} hover:rounded-lg" />`,
            },
          ],
        },
      ],
    })),

    // The important marker settles the tie inside a variant too.
    {
      code: '<div className="hover:aspect-video! hover:aspect-square hover:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="hover:aspect-video! hover:aspect-square hover:rounded-lg" />',
            },
          ],
        },
      ],
    },

    // Raised by Hicks: a non-square ratio proves non-squareness on its own, so it
    // contradicts an animation as well as a `ratio`. A 2:1 spinner is a lozenge
    // whatever its size, and stays one when only a single axis is pinned.
    ...[
      "aspect-[2/1] animate-spin",
      "w-[2rem] aspect-[2/1] animate-spin",
      "aspect-video pf-animate-spin",
    ].map((dims) => ({
      code: `<div className="${dims} rounded-full" />`,
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: `<div className="${dims} rounded-lg" />`,
            },
          ],
        },
      ],
    })),
    {
      code: '<div className="animate-spin hover:aspect-[2/1] hover:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="animate-spin hover:aspect-[2/1] hover:rounded-lg" />',
            },
          ],
        },
      ],
    },

    // Skipping `auto` must leave the axis *unproven*, not excused: an 8-wide box
    // of unknown height is not evidence of a circle, so this still reports.
    {
      code: '<div className="w-8 h-auto rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="w-8 h-auto rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="h-8 w-auto rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="h-8 w-auto rounded-lg" />',
            },
          ],
        },
      ],
    },

    // `size-*` is `w-* h-*`, not an unconditional "this is a circle" flag. When
    // it short-circuited, a more specific axis override could not contradict it
    // and `size-8 hover:w-4` was excused on hover, where it is a 4x8 lozenge.
    {
      code: '<div className="size-8 hover:w-4 hover:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="size-8 hover:w-4 hover:rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="size-8 md:w-4 md:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "md:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="size-8 md:w-4 md:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // `aspect-square` names no dimensions, so it cannot be contradicted axis by
    // axis -- but it must still lose to a strictly more specific pair that
    // disagrees, or the same hole reopens through a different door.
    {
      code: '<div className="aspect-square hover:w-4 hover:h-8 hover:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="aspect-square hover:w-4 hover:h-8 hover:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // CSS resolves definite width/height before aspect-ratio has anything to
    // say, so `aspect-square w-4 h-8` is a 4x8 box and the ratio is ignored.
    // Contradiction therefore has to bite at equal specificity, not only finer.
    {
      code: '<div className="aspect-square w-4 h-8 rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="aspect-square w-4 h-8 rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Guards the fresh-Set fix in `resolve()`: the tie-union branch used to
    // mutate the map's stored evidence, so `focus:w-8` leaked into the `hover`
    // entry and the `hover:` radius was excused by a width it never had.
    // Aliasing this again reports nothing.
    {
      code: '<div className="h-8 hover:w-4 focus:w-8 hover:focus:rounded-full hover:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="h-8 hover:w-4 focus:w-8 hover:focus:rounded-full hover:rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Arbitrary-property syntax sets border-radius without ever spelling
    // "rounded". Until the rule read it, any value at all passed unseen -- and
    // the first thing it caught once taught was a 12px radius in this project's
    // own Badge test.
    {
      code: '<div className="[border-radius:12px]" />',
      output: null,
      errors: [{ messageId: "oversized" }],
    },
    {
      code: '<div className="md:[border-radius:1.5rem]" />',
      output: null,
      errors: [{ messageId: "oversized" }],
    },
    {
      code: '<div className="[border-radius:9999px]" />',
      output: null,
      errors: [{ messageId: "fullRound" }],
    },

    // Tailwind's data-type hint emits the same CSS as `rounded-[12px]`; treating
    // it as unresolvable let any oversized arbitrary radius through unseen.
    {
      code: '<div className="rounded-[length:12px]" />',
      output: null,
      errors: [
        {
          messageId: "oversized",
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="rounded-lg" />',
            },
          ],
        },
      ],
    },

    // The tokenizer blind spot that made #1022's "zero reports" partly illusory:
    // a regex variant-strip that stops at the first `]` never resolves these to a
    // radius utility, so they were silently skipped rather than judged. `w-8 h-4`
    // is a 2:1 lozenge, so each of these is a real violation.
    {
      code: '<div className="data-[state=open]:rounded-full w-8 h-4" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "data-[state=open]:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="data-[state=open]:rounded-lg w-8 h-4" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="[&[data-state=open]]:rounded-full w-8 h-4" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "[&[data-state=open]]:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="[&[data-state=open]]:rounded-lg w-8 h-4" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="supports-[display:grid]:rounded-xl" />',
      output: '<div className="supports-[display:grid]:rounded-lg" />',
      errors: [
        {
          messageId: "oversized",
          data: { token: "supports-[display:grid]:rounded-xl", px: 12 },
        },
      ],
    },
    {
      code: '<div className="group-hover/item:rounded-xl" />',
      output: '<div className="group-hover/item:rounded-lg" />',
      errors: [
        {
          messageId: "oversized",
          data: { token: "group-hover/item:rounded-xl", px: 12 },
        },
      ],
    },
    // The important marker must not hide the utility either, in either position.
    {
      code: '<div className="rounded-xl!" />',
      output: '<div className="rounded-lg!" />',
      errors: [
        { messageId: "oversized", data: { token: "rounded-xl!", px: 12 } },
      ],
    },
    {
      code: '<div className="!rounded-xl" />',
      output: '<div className="!rounded-lg" />',
      errors: [
        { messageId: "oversized", data: { token: "!rounded-xl", px: 12 } },
      ],
    },

    // Subset semantics must not become "any evidence anywhere excuses anything".
    // The element is not square below `md`, so it is not excused at no breakpoint.
    {
      code: '<div className="md:aspect-square rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="md:aspect-square rounded-lg" />',
            },
          ],
        },
      ],
    },
    // The most specific applicable condition wins: on hover this is 4x8.
    {
      code: '<div className="w-8 h-8 hover:w-4 hover:rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "hover:rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="w-8 h-8 hover:w-4 hover:rounded-lg" />',
            },
          ],
        },
      ],
    },

    // A bare `data-pf-radius` does not say which radius was intended, so it is not
    // a signature and must not waive. `data-pf-progress-track` still waives bare,
    // covered in `valid` above -- that one really is a boolean marker.
    {
      code: '<div data-pf-radius className="rounded-full w-8 h-4" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div data-pf-radius className="rounded-lg w-8 h-4" />',
            },
          ],
        },
      ],
    },
    // rounded-xl is 12px in Tailwind's stock scale, which the project does not
    // remap -- so it is over the 8px ceiling too.
    {
      code: '<div className="rounded-xl border" />',
      output: '<div className="rounded-lg border" />',
      errors: [
        { messageId: "oversized", data: { token: "rounded-xl", px: 12 } },
      ],
    },
    // ...but it is legal under a raised ceiling, which is what `maxPx` is for.
    {
      code: '<div className="rounded-2xl" />',
      options: [{ maxPx: 12 }],
      output: '<div className="rounded-lg" />',
      errors: [
        { messageId: "oversized", data: { token: "rounded-2xl", px: 16 } },
      ],
    },
    {
      code: '<div className="rounded-2xl border p-4" />',
      output: '<div className="rounded-lg border p-4" />',
      errors: [
        { messageId: "oversized", data: { token: "rounded-2xl", px: 16 } },
      ],
    },
    // Regression: the fix range must come from the source text, not from a
    // quasi's cooked/raw value. Both normalise CRLF to LF, so on a Windows
    // checkout every preceding line shifted the range one char left and the
    // fixer glued the tail of one class onto the head of the next -- a wrong
    // class, not a syntax error, so the build stayed green. (The artefact is
    // deliberately not spelled out here: AdminThemeSafety greps source text
    // for colour utilities and would read it as a real, dead one.)
    // Note the \r\n: with \n these cases pass even against the broken code.
    {
      code: [
        "<div className={`",
        "        w-full text-left border border-pf-border rounded-xl p-4",
        "        hover:shadow-md",
        "      `} />",
      ].join("\r\n"),
      output: [
        "<div className={`",
        "        w-full text-left border border-pf-border rounded-lg p-4",
        "        hover:shadow-md",
        "      `} />",
      ].join("\r\n"),
      errors: [
        { messageId: "oversized", data: { token: "rounded-xl", px: 12 } },
      ],
    },
    // Same, but the violation is in the quasi *after* an interpolation, so the
    // start index has to survive the `${...}` too.
    {
      code: [
        "<div className={`p-2 ${x}",
        "   border rounded-xl gap-2`} />",
      ].join("\r\n"),
      output: [
        "<div className={`p-2 ${x}",
        "   border rounded-lg gap-2`} />",
      ].join("\r\n"),
      errors: [
        { messageId: "oversized", data: { token: "rounded-xl", px: 12 } },
      ],
    },
    {
      code: '<div className="rounded-3xl" />',
      output: '<div className="rounded-lg" />',
      errors: [
        { messageId: "oversized", data: { token: "rounded-3xl", px: 24 } },
      ],
    },
    // A side segment plus an oversized size: both parts survive the rewrite.
    {
      code: '<div className="rounded-tl-3xl" />',
      output: '<div className="rounded-tl-lg" />',
      errors: [{ messageId: "oversized" }],
    },
    // Side-scoped and variant-prefixed tokens are matched on their base.
    {
      code: '<div className="rounded-t-2xl" />',
      output: '<div className="rounded-t-lg" />',
      errors: [{ messageId: "oversized" }],
    },
    {
      code: '<div className="md:rounded-2xl" />',
      output: '<div className="md:rounded-lg" />',
      errors: [{ messageId: "oversized" }],
    },
    // Arbitrary values above the ceiling: suggestion, not silent rewrite,
    // because rounded-md may be the better answer and only a human knows.
    {
      code: '<div className="rounded-[1.75rem]" />',
      output: null,
      errors: [
        {
          messageId: "oversized",
          data: { token: "rounded-[1.75rem]", px: 28 },
          suggestions: [
            {
              messageId: "replaceWithLg",
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
          messageId: "oversized",
          data: { token: "rounded-[20px]", px: 20 },
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="rounded-lg" />',
            },
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
          messageId: "fullRound",
          data: { token: "rounded-full" },
          suggestions: [
            {
              messageId: "replaceWithLg",
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
          messageId: "fullRound",
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div className="px-2 py-0.5 text-xs rounded-lg bg-pf-bg-2" />',
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
          messageId: "fullRound",
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="w-full h-2 rounded-lg" />',
            },
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
          messageId: "fullRound",
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="px-3 rounded-lg" />',
            },
          ],
        },
      ],
    },
    // Inside clsx(), reported at the offending fragment.
    {
      code: '<div className={clsx("flex gap-3 rounded-2xl border", isActive && "bg-pf-bg-1")} />',
      output:
        '<div className={clsx("flex gap-3 rounded-lg border", isActive && "bg-pf-bg-1")} />',
      errors: [{ messageId: "oversized" }],
    },
    // Inside a template literal.
    {
      code: "<div className={`rounded-2xl ${extra}`} />",
      output: "<div className={`rounded-lg ${extra}`} />",
      errors: [{ messageId: "oversized" }],
    },
    // Two violations in one attribute are both reported.
    {
      code: '<div className="rounded-2xl md:rounded-3xl" />',
      output: '<div className="rounded-lg md:rounded-lg" />',
      errors: [{ messageId: "oversized" }, { messageId: "oversized" }],
    },
    // An unrelated data attribute is not a waiver.
    {
      code: '<div data-testid="chip" className="px-3 py-1 rounded-full" />',
      output: null,
      errors: [
        {
          messageId: "fullRound",
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div data-testid="chip" className="px-3 py-1 rounded-lg" />',
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
          messageId: "fullRound",
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div data-pf-radius="sm" className="px-3 py-1 rounded-lg" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div data-pf-radius={false} className="px-3 py-1 rounded-full" />',
      errors: [
        {
          messageId: "fullRound",
          suggestions: [
            {
              messageId: "replaceWithLg",
              output:
                '<div data-pf-radius={false} className="px-3 py-1 rounded-lg" />',
            },
          ],
        },
      ],
    },

    // Circularity has to be unconditional. `md:aspect-square` is a rectangle
    // below the breakpoint, so `rounded-full` is wrong there.
    {
      code: '<div className="md:aspect-square rounded-full px-4" />',
      errors: [
        {
          messageId: "fullRound",
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="md:aspect-square rounded-lg px-4" />',
            },
          ],
        },
      ],
    },
    {
      code: '<div className="w-6 md:h-6 rounded-full" />',
      errors: [
        {
          messageId: "fullRound",
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: '<div className="w-6 md:h-6 rounded-lg" />',
            },
          ],
        },
      ],
    },

    // Raised by Hicks, and all five are false *negatives* -- the direction this
    // rule is least likely to notice on its own, since nothing complains.
    //
    // Carrying a length token is not being a length: `calc(1px/1px)` is a number
    // and CSS drops it, so both axes fall back to auto and the opaque-pair
    // shortcut was proving a square from two declarations that never applied.
    // `calc(1px*1px)` is an area and drops the same way.
    ...[
      "w-[calc(1px/1px)] h-[calc(1px/1px)] rounded-full",
      "w-[calc(1px*1px)] h-[calc(1px*1px)] rounded-full",
      // The opaque pair is real here but stale: `hover:w-8` is more specific, so
      // on hover the box is 32x2 and the standing pair no longer describes it.
      "w-[calc(1px*2)] h-[calc(1px*2)] hover:w-8 hover:rounded-full",
      // A named variant takes its bracket as an *argument*: `has-[&>img]` emits
      // `&:has(*>img)` and `supports-[selector(::before)]` emits an `@supports`
      // block, so in both the radius stays on the host -- where `w-4 h-8`
      // contradicts `aspect-square`. Reading the `&>` inside the bracket as a
      // combinator moved the target off the host and lost the contradiction.
      "w-4 h-8 aspect-square has-[&>img]:rounded-full",
      "w-4 h-8 aspect-square supports-[selector(::before)]:rounded-full",
      // Same mistake one level in: `[&:has(>img)]` emits `&:has(>img)`, where the
      // `>` belongs to the `:has()` argument and not to the selector's own shape.
      // The element carrying the radius is still the host.
      "w-4 h-8 aspect-square [&:has(>img)]:rounded-full",
      // ...and the converse: attached to a compound, `:is()` qualifies it rather
      // than replacing the subject, so these are still the host and the
      // contradiction still applies.
      "w-4 h-8 aspect-square [&:is(.a_.b)]:rounded-full",
      "w-4 h-8 aspect-square [:is(&:hover)]:rounded-full",
      // `calc()` subtraction on axes that genuinely differ still reports.
      "w-[calc(32px_-_1px)] h-[calc(32px_-_2px)] rounded-full",
      // Media variants add no specificity, so `hover:` outranks `md:` however many
      // breakpoints are stacked. Both of these are honoured by CSS at the pseudo
      // class and are genuinely non-square there. Raised by Bishop and Hicks.
      "md:w-[calc(1px*2)] md:h-[calc(1px*2)] hover:w-4 hover:h-8 md:hover:rounded-full",
      "md:aspect-square hover:aspect-video md:hover:rounded-full",
      // A computed negative length is not dropped: `calc(1px - 2px)` parses,
      // applies and clamps to zero, so the height at `:hover` is 0 and the base
      // 32x32 circle is gone. Raised by Hicks, with browser evidence, against a
      // reading that CSS drops it. The bare form is dropped and sits in `valid`.
      "w-8 h-8 hover:h-[calc(1px_-_2px)] hover:rounded-full",
      // `clamp()` with three arguments is valid, so it applies and withdraws the
      // height the base pinned, and the 32x32 circle can no longer be proved.
      "w-8 h-8 hover:h-[clamp(1px,2px,3px)] hover:rounded-full",
      // Addends of a sum must agree in degree. `calc(1px + 1)` is invalid CSS, so
      // it cannot withdraw the base width and excuse this lozenge. Raised by Hicks.
      "w-4 h-2 aspect-square hover:w-[calc(1px_+_1)] hover:rounded-full",
    ].map((classes) => ({
      code: `<div className="${classes}" />`,
      errors: [
        {
          messageId: "fullRound",
          suggestions: [
            {
              messageId: "replaceWithLg",
              output: `<div className="${classes.replace("rounded-full", "rounded-lg")}" />`,
            },
          ],
        },
      ],
    })),
  ],
});
