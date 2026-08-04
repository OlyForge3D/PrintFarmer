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
 *   - matching explicit width and height in *comparable* units (`w-8 h-8`,
 *     `h-2.5 w-2.5`, `w-[12px] h-[12px]`) -- but not `w-full h-full`, which is
 *     100% of two different containing-block axes and equal only by coincidence
 *   - `size-*` on the spacing scale (Tailwind sets both axes)
 *   - `aspect-square`
 *   - `animate-spin` / `animate-ping` / `pf-animate-spin` (spinners and ripples)
 *
 * That evidence is read per variant prefix, so `[&::-webkit-slider-thumb]:w-4`
 * and `:h-4` excuse `[&::-webkit-slider-thumb]:rounded-full` -- all three scope
 * to the same pseudo-element -- while `md:aspect-square` still does not excuse
 * an unprefixed `rounded-full`, because that element is a rectangle below `md`.
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

const MAX_RADIUS_PX = 8; // --pf-radius-lg

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
  "": 4, // bare `rounded`
  md: 6,
  lg: 8,
  xl: 12,
  "2xl": 16,
  "3xl": 24,
  "4xl": 32,
};

const SIDES = new Set([
  "t",
  "r",
  "b",
  "l",
  "s",
  "e",
  "tl",
  "tr",
  "br",
  "bl",
  "ss",
  "se",
  "ee",
  "es",
]);

/**
 * Split a `rounded*` utility into its size, or return null if it is not one.
 * Handles the optional side segment explicitly rather than leaning on regex
 * backtracking, so `rounded-l` (left side, default radius) and `rounded-lg`
 * (large radius, all sides) cannot be confused.
 */
function parseRoundedSize(base) {
  // Arbitrary-property syntax sets border-radius without ever spelling
  // "rounded", so it has to be recognised separately or any value at all slips
  // through: `[border-radius:12px]` is valid Tailwind and emits real CSS.
  // Normalised to the `[value]` form so it flows into the same size handling.
  const property = /^\[border-radius:([^\]]+)\]$/.exec(base);
  if (property) return `[${property[1]}]`;

  if (base !== "rounded" && !base.startsWith("rounded-")) return null;
  if (base === "rounded") return "";

  let size = base.slice("rounded-".length);
  const dash = size.indexOf("-");
  if (dash !== -1 && SIDES.has(size.slice(0, dash))) {
    size = size.slice(dash + 1);
  } else if (SIDES.has(size)) {
    return ""; // e.g. `rounded-l`: a side with the default radius
  }
  return size;
}

const WAIVER_ATTRIBUTES = new Set([
  "data-pf-radius",
  "data-pf-progress-track",
  "data-pf-progress-fill",
]);

/**
 * Split a Tailwind candidate into its variant segments and the bare utility.
 *
 * Variants are separated by `:`, but a `:` inside brackets, parens, braces or
 * quotes belongs to the variant, not to the separator — so this scans with a
 * depth counter rather than matching a regex per variant spelling. That is the
 * difference between seeing `data-[state=open]:rounded-full` and silently
 * skipping it: the old regexes matched `[...]:` and `[\w-]+:` and neither
 * accepts a bracket *inside* a named variant, so the token never resolved to a
 * radius utility and was never reported at all.
 *
 * Deliberately no allow-list of variant names. Anything before an unescaped
 * top-level `:` is opaque, which makes this correct for `supports-[...]`,
 * `peer-[...]`, `group-data-[...]`, `has-[...]`, `@max-md`, `group-hover/item`,
 * `*`, `**`, arbitrary `[&[data-state=open]]`, and whatever Tailwind adds next.
 * Leading/trailing `!` (both the legacy and v4 important markers) is stripped
 * from the utility so `rounded-xl!` and `!rounded-xl` still resolve.
 */
/**
 * True when a variant compiles to no rule whatsoever, so the class it decorates
 * never reaches the stylesheet at all.
 *
 * Three families, each settled by compiling the form rather than by reasoning
 * about what ought to work: `not-starting:`, because `@starting-style` has no
 * negated form; `group-`/`peer-` wrapping any at-rule variant, because those
 * need a selector to hang their marker on; and `not-[@…]` naming an at-rule
 * outside the negatable whitelist.
 *
 * Such a class was first made *unranked*, which only let it excuse in a
 * comparison. Hicks rejected that as a half-measure and he was right: unranked
 * is not absent, so `w-[16px] h-[32px] not-starting:rounded-full` still reported
 * a radius that never ships. An inert class is now dropped outright -- it
 * contributes no weight, no shape evidence and no reportable radius -- which is
 * the only reading consistent with an empty stylesheet.
 *
 * A fourth family, also Hicks: Tailwind has no double negation at all, so
 * `not-not-hover:` and `not-not-starting:` emit nothing whatever the inner
 * variant is. Recognising `not-` only once read the second one as an ordinary
 * variant name and reported a radius that never ships.
 */
/**
 * Is one variant segment dead -- that is, does Tailwind emit no rule at all for it?
 *
 * Inertness is recursive, because every wrapper here composes: `not-` negates
 * whatever follows, and `group-`/`peer-`/`has-` hang a marker off it. Wrapping a
 * dead variant leaves it dead, so `not-group-print:`, `group-not-not-hover:` and
 * `has-not-starting:` all emit nothing exactly as their inner forms do. Reading
 * only one level treated the outer wrapper as an ordinary variant name and
 * reported a radius that never reaches the page. Raised by Hicks, who supplied
 * all three spellings.
 *
 * This is the single source of truth for inertness: `weighSegment` consults it
 * rather than re-deriving the same split, which is how the one-level version
 * drifted in the first place. Both Vasquez and Bishop asked for the consolidation.
 */
function isInertSegment(segment) {
  const bare = segment.replace(/^\[|\]$/g, "");
  const namedAtRule = /^not-\[\s*@([a-z-]+)/i.exec(segment);
  if (namedAtRule) return !NEGATABLE_AT_RULE.test(namedAtRule[1]);
  const prefix = /^(?:group|peer|has|not)-(?=.)/i.exec(bare);
  if (!prefix) return false;
  const rest = bare.slice(prefix[0].length);
  const head = prefix[0].toLowerCase();
  if (head === "not-") {
    // Tailwind has no double negation whatsoever, at any depth.
    if (/^not-/i.test(rest)) return true;
    if (AT_RULE_VARIANT.test(rest)) return NON_NEGATABLE_AT_RULE.test(rest);
    return isInertSegment(rest);
  }
  return (
    AT_RULE_VARIANT.test(rest) ||
    (/^not-/i.test(rest) && AT_RULE_VARIANT.test(rest.slice(4))) ||
    isInertSegment(rest)
  );
}

function isInertVariant(segments) {
  return segments.some(isInertSegment);
}

function splitCandidate(token) {
  const segments = [];
  let depth = 0;
  let quote = null;
  let start = 0;

  for (let i = 0; i < token.length; i += 1) {
    const ch = token[i];
    if (ch === "\\") {
      i += 1;
      continue;
    }
    if (quote) {
      if (ch === quote) quote = null;
      continue;
    }
    if (ch === '"' || ch === "'") {
      quote = ch;
      continue;
    }
    if (ch === "[" || ch === "(" || ch === "{") {
      depth += 1;
      continue;
    }
    if (ch === "]" || ch === ")" || ch === "}") {
      if (depth > 0) depth -= 1;
      continue;
    }
    if (ch === ":" && depth === 0) {
      segments.push(token.slice(start, i));
      start = i + 1;
    }
  }

  const marked = token.slice(start);
  const stripped = marked.replace(/^!+/, "").replace(/!+$/, "");
  // Importance is reported rather than merely stripped, because it decides the
  // cascade outright: an important declaration beats every non-important one in
  // the same origin, whatever their selectors. Without it `aspect-square!
  // aspect-video` reads as an unresolvable conflict when CSS resolves it
  // definitively in favour of the square.
  //
  // It can also be declared *inside* an arbitrary value, which is a second
  // spelling of the same thing and not a second mechanism: Tailwind copies the
  // bracket contents into the declaration, so `size-[16px!important]` emits
  // `width: 16px!important`. Reading only the class-level marker left that
  // importance invisible, and `size-[16px!important] h-[32px]` — a genuine
  // 16x16 circle, measured — was reported as a lozenge. Raised by Hicks.
  const inner = arbitraryImportance(stripped);
  return {
    segments,
    utility: inner.utility,
    important: marked !== stripped || inner.important,
  };
}

/**
 * Strip a CSS `!important` declared inside a trailing arbitrary value, and say
 * whether one was there.
 *
 * The grammar is CSS's, not Tailwind's, and it is looser than it looks. Three
 * separate reviewers broke three separate versions of this, and every version
 * failed the same way: it pattern-matched the *text* instead of reading the
 * value the way a CSS parser does. So this one does the two things a parser
 * does first -- discard comments, resolve escapes -- and only then asks whether
 * what is left of the last `!` spells the keyword.
 *
 * Comments are removed before the `!` is looked for at all, which is the only
 * arrangement that gets all three of these right:
 *
 *   size-[16px!important/*!important/**\/]   16x16, important (Hicks)
 *   h-[32px!important/*!important/**\/]      16x32, important (Hicks)
 *   size-[16px/*!important/**\/]             16x32, NOT important -- a marker
 *                                            written entirely inside a comment
 *                                            is not a marker
 *
 * Searching for the last `!` in the raw text finds the one inside the comment
 * and mangles the value; searching for the first finds the real one in the
 * first case and the wrong one elsewhere. Neither quantifier is right, which is
 * why the choice between them was the wrong question -- I argued it at length
 * in round 37 and was answered with measurements. The reader below takes the
 * last *unescaped* `!` of the comment-stripped text, which is a third thing
 * again: see the note at the search itself for why the qualifier matters.
 *
 * Escapes are resolved because CSS identifiers may carry them and Tailwind
 * passes them through verbatim. `16px!\important`, `16px!imp\ortant`,
 * `16px!IMPOR\TANT` (Vasquez) and `16px!\69mportant` (Hicks) are all important,
 * all measured, and all read as a plain `16px` by everything downstream -- so
 * missing them charged a genuine 16x16 circle with being a lozenge.
 *
 * Tailwind writes a space as `_`, so `_` terminates a numeric escape exactly as
 * a space does, and separators are allowed only at the ends of the keyword: a
 * separator *inside* it, as in `imp_ortant`, is a different identifier and not
 * important at all.
 */
const IMPORTANT_TAIL = /^[\s_]*(?:\\(?:[0-9a-fA-F]{1,6}[\s_]?|[^])|[^\\\s_])+[\s_]*$/;
const CSS_ESCAPE = /\\(?:([0-9a-fA-F]{1,6})[\s_]?|([^]))/g;

/**
 * Resolve CSS identifier escapes: `\69mportant` and `\important` are both `important`.
 *
 * Out-of-range, surrogate and null code points are replaced rather than thrown
 * on, which is what CSS Syntax 4.3.7 requires and, more to the point, what stops
 * `size-[16px!\110000]` from crashing the linter with a `RangeError` in the
 * middle of a run. `String.fromCodePoint` accepts a six-digit hex escape only up
 * to U+10FFFF, and CSS allows six digits unconditionally, so the two disagree on
 * a range an author can type. Raised by Hicks, with the stack trace.
 */
function decodeCssEscapes(text) {
  return text.replace(CSS_ESCAPE, (_whole, hex, literal) => {
    if (hex === undefined) return literal;
    const point = Number.parseInt(hex, 16);
    if (point === 0 || point > 0x10ffff || (point >= 0xd800 && point <= 0xdfff)) {
      return "\uFFFD";
    }
    return String.fromCodePoint(point);
  });
}

function arbitraryImportance(utility) {
  if (!utility.endsWith("]")) return { utility, important: false };
  const clean = stripCssComments(utility);
  if (!clean.endsWith("]")) return { utility, important: false };
  // The keyword is read from the last *unescaped* `!`, and the qualifier is the
  // whole point. Round 38 argued that first-vs-last was a provable equivalence,
  // on the grounds that comments are gone by this line so any remaining `!` must
  // be real, and two real ones make the declaration invalid either way. The
  // premise was false: `\!` survives comment stripping and is a literal
  // character, not a delimiter. `h-[var(--x\!y,32px)!important] w-[16px]` is a
  // measured 16x32 lozenge, and reading from the first `!` finds the escaped one,
  // mangles the value and lets it pass. Hicks built that case after I claimed the
  // equivalence -- the second time in two rounds that a claim of mine about this
  // function was stronger than the evidence for it.
  //
  // With escaped `!` excluded the original argument does hold, and is now pinned
  // rather than asserted: two unescaped `!` leave one inside the tail under this
  // reading and one inside the value under the other, both are invalid CSS, and
  // both readings condemn.
  //
  // The escape-skip itself is *not* pinned, and I am saying so rather than
  // claiming it cannot be. The argument for its being unobservable is that a
  // literal escaped `!` is by definition preceded by `\`, so landing on one
  // leaves a head ending in a backslash, which no reader in this file accepts --
  // the value is dropped either way. That argument is exactly the kind I have now
  // got wrong twice on this function, so it is written down to be attacked rather
  // than trusted. The loop stays because it is what a CSS tokenizer does.
  let bang = -1;
  for (let i = 0; i < clean.length; i += 1) {
    if (clean[i] === "\\") {
      i += 1;
      continue;
    }
    if (clean[i] === "!") bang = i;
  }
  if (bang < 0) return { utility, important: false };
  const tail = clean.slice(bang + 1, -1);
  if (!IMPORTANT_TAIL.test(tail)) return { utility, important: false };
  if (decodeCssEscapes(tail).trim().replace(/^_+|_+$/g, "").toLowerCase() !== "important") {
    return { utility, important: false };
  }
  // `stripCssComments` substitutes a space, so removing an interior comment
  // leaves the value padded: `size-[16px/*c*/!important]` becomes
  // `size-[16px ]`, which no downstream reader parses. That cut both ways --
  // a measured 16x16 circle was reported, and its `h-` twin, a measured 16x32
  // lozenge, was excused. The value is re-trimmed inside its brackets so the
  // text handed on is the text CSS sees. Raised by Hicks.
  const open = clean.indexOf("[");
  const head = clean.slice(open + 1, bang).trim();
  return { utility: `${clean.slice(0, open + 1)}${head}]`, important: true };
}

/**
 * Resolve an arbitrary radius like `[1.75rem]` to pixels.
 * Returns `Infinity` for values that mean "fully round", and `null` when the
 * value cannot be resolved (`var()`, `calc()`), in which case nothing is
 * reported -- an unprovable violation is not a violation.
 */
function arbitraryToPx(value) {
  // Tailwind allows an explicit data-type hint: `rounded-[length:12px]` emits
  // the same CSS as `rounded-[12px]`. Strip it, or the value reads as
  // unresolvable and the site is silently skipped.
  //
  // The hint is removed *before* comments, because Tailwind only honours it at
  // the literal start of the value. `rounded-[length:/**/16px]` is a real 16px
  // radius and `rounded-[/**/length:16px]` computes to 0, and stripping comments
  // first collapses the two: the first was excused and the second reported, one
  // error in each direction from a single ordering. Raised by Hicks.
  //
  // Comments are stripped for the same reason as everywhere else in this file,
  // and it was not a hypothetical: `rounded-[16px/*c*/]` emits
  // `border-radius: 16px/*c*/`, which really is a 16px radius, and this function
  // used to return `null` for it and wave the site through. Found by Vasquez.
  //
  // `_` is Tailwind's space, so it is resolved before trimming; `rounded-[_16px_]`
  // is a 16px radius, measured, and read as unresolvable until it was.
  const hinted = value.slice(1, -1).replace(/^[a-z-]+:(?!:)/i, "");
  const inner = stripCssComments(hinted).replace(/_/g, " ").trim();
  if (/^(?:9999(?:px|rem)|100%|50%|100vmax)$/i.test(inner)) return Infinity;
  // Units are case-insensitive, `+` is a valid sign, and scientific notation is a
  // valid `<number>`: `16PX`, `+16px` and `1e1px` measure 16px, 16px and 10px and
  // were all read as unresolvable. A leading `-` is deliberately *not* accepted --
  // a negative radius is invalid, so CSS drops the declaration and the element
  // measures 0, which is why `rounded-[-16px]` must stay excused. Raised by Hicks.
  const match = /^\+?(\d*\.?\d+(?:e[+-]?\d+)?)(px|rem|em)$/i.exec(inner);
  if (!match) return null;
  const scalar = Number.parseFloat(match[1]);
  return match[2].toLowerCase() === "px" ? scalar : scalar * 16;
}

/**
 * Index an element's shape evidence by variant condition.
 *
 * The variant matters, in both directions. `md:aspect-square` is a rectangle
 * below `md`, so it cannot excuse an unconditional `rounded-full` — exempting
 * it there is exactly the bug this rule exists to catch. But
 * `[&::-webkit-slider-thumb]:w-4` and `[&::-webkit-slider-thumb]:h-4` do prove
 * that `[&::-webkit-slider-thumb]:rounded-full` lands on a circle, because all
 * three scope to the same pseudo-element, and no honest waiver exists for that
 * case: `data-pf-radius="full"` on the host `<input>` would assert that the
 * *input* is a pill, which it is not.
 *
 * Evidence therefore counts for a radius token when the evidence's conditions
 * are a subset of the radius token's. Unprefixed evidence (the empty set) is a
 * subset of everything, so it applies in every state; `md:size-8` applies to
 * `md:hover:rounded-full` because `{md} ⊆ {md, hover}`; and `md:aspect-square`
 * still cannot excuse a bare `rounded-full` because `{md} ⊄ {}`. Width and
 * height resolve from the most specific applicable condition, which is how the
 * cascade behaves: given `w-8 h-4 hover:w-4`, the hover state really is 4×4.
 *
 * `size-N` is decomposed into `w-N h-N` rather than treated as a standalone
 * "this is a circle" flag, so it takes part in that same resolution and a more
 * specific override can contradict it: `size-8 hover:w-4 hover:rounded-full` is
 * a 4×8 lozenge on hover, and is reported. `aspect-square` and the spin
 * animations name no dimensions, so they cannot be contradicted axis by axis;
 * they still short-circuit, but only when no applicable width/height pair at
 * equal or finer specificity disagrees. Equal counts because CSS resolves
 * definite width/height before `aspect-ratio`, so `aspect-square w-4 h-8` is a
 * 4×8 box and the ratio is simply ignored. `auto` is excluded from that evidence
 * entirely: it is the one indefinite dimension keyword, and `aspect-ratio` is
 * only overruled when *both* axes are definite, so `w-full h-auto aspect-square`
 * really is a circle.
 *
 * Subset containment on its own only ever *widens* what counts as evidence, but
 * this is not a strictly report-reducing change overall: the rule now also reads
 * variant-prefixed `w-`/`h-` evidence that the old exact-match version ignored
 * entirely, and most-specific-wins lets that prefixed evidence *override* the
 * bare evidence. So `w-8 h-8 hover:w-4 hover:rounded-full` went from excused to
 * reported. That is the correct answer — on hover the element really is a 4×8
 * lozenge — but it means tightening this heuristic can surface new work, and it
 * did: do not assume a green lint run before the change implies one after.
 *
 * Known limitation, deliberately not modelled: Tailwind's breakpoints are
 * cumulative and ordered, but set containment treats them as unordered. So
 * `w-8 h-8 md:w-4 lg:rounded-full` is excused even though `md:w-4` is still in
 * force at `lg`, making it 4×8. Sorting also collapses `[&_img]:hover:` and
 * `hover:[&_img]:` onto one key, and unprefixed host evidence is accepted as an
 * *excuse* for a radius scoped to a descendant (`w-8 h-8 [&_img]:rounded-full`)
 * even though the descendant need not share the host's dimensions. It is no
 * longer accepted as a *contradiction*: host and descendant dimensions were being
 * paired into a lozenge that existed on neither element, which is a false report
 * rather than a lenient one, so contradiction evidence must now come from the
 * same selector scope as the radius. Raised by Hicks — three times, and each
 * correction narrowed what counts as a scope change. The first fix classified
 * scopes by whether the variant mentions `&`, which got the two commonest cases
 * backwards: `[&:hover]` is this element in a state, so its own dimensions do
 * bear on it, while `*:` names the children and mentions no `&` at all. The
 * second required the combinator to sit immediately after the `&`, which missed
 * `[&:hover_img]` (emitted as `&:hover img`). What moves the target is a
 * combinator anywhere after the `&` at the top level of a *bare* `[…]` selector,
 * a child variant, or a pseudo-element — and nothing inside a payload, since
 * `has-[&>img]` emits `&:has(*>img)` and keeps the radius on the host.
 * Catching the rest means
 * encoding breakpoint order and selector scope — a real cascade implementation,
 * well beyond a "is this plausibly square?" heuristic. All three fail toward
 * excusing rather than toward a false report, which is the direction that cannot
 * break a build or provoke a dishonest waiver. A fourth, same direction: ties
 * between conditions of equal variant count are unioned rather than resolved, so
 * given `hover:w-8 focus:w-4 hover:focus:rounded-full` the rule sees both widths
 * and accepts the one that matches, and reads `md:aspect-square hover:aspect-video`
 * as possibly square even though a media variant carries no specificity while
 * `hover:` does. Repeats of the *same* condition are unioned for a different
 * reason: which wins depends on emission order, and Tailwind sorts each utility
 * group, so the class attribute does not determine it. `!important` is the one tie
 * CSS settles and it is honoured. A fifth: a value holding a
 * `var()` is indefinite, so `w-full h-[var(--h)] aspect-square` is read as a
 * possible circle even though a length in `--h` would pin both axes. A sixth:
 * `auto || <ratio>` proves squareness but never a lozenge, because on a replaced
 * element `auto` selects the natural ratio and the specified one applies only in
 * its absence — so a 2:1 `<img>` marked `aspect-[auto_1/1]` is excused.
 *
 * A seventh, and the widest of them: the rule does not type-check CSS math. Any
 * arbitrary dimension it cannot evaluate — `w-[calc(1px*2)]`, `w-[var(--x)]`,
 * `w-[-1px]` — is read as *opaque*, which withdraws no proof, contradicts no
 * ratio and pins no axis. It keeps one power only: the same opaque value on both
 * axes still proves a square, since CSS resolves it identically twice. So
 * `w-8 h-8 hover:h-[calc(1px/1px)] hover:rounded-full` is excused whether or not
 * CSS keeps that height. The alternative — Hicks's "opaque winner", which
 * replaces whatever came before — reports a genuine 32×32 circle whenever the
 * declaration is in fact dropped, and that is the one direction the rule refuses.
 *
 * An eighth, of the same family: `min-content`, `fit-content` and `stretch` are
 * valid and CSS keeps them, but none is a length, so they cannot overrule an
 * `aspect-ratio`. `stretch` in particular can genuinely un-square a box — 32px
 * wide inside a 100px-tall parent — and that goes unreported. Raised by Hicks.
 *
 * A ninth: a percentage *height* is definite only when the parent's own height
 * does not depend on its content (CSS 2.1 §10.5), so `h-full` is treated as
 * indefinite and `w-full h-full aspect-square` reads as a square. There is no
 * such rule for widths, so the asymmetry is encoded on one axis only — treating
 * both alike would have silently excused `aspect-square w-full h-8`, which is a
 * real 100%-by-32px lozenge. Raised by Bishop; the defect was his, the cure
 * is narrower than the one he prescribed.
 *
 * A tenth, on the two fences now around that opaque-pair proof. Unit degree is
 * computed where the arithmetic can be followed, so `calc(1px/1px)` and
 * `calc(1px*1px)` are recognised as a number and an area and therefore dropped,
 * but anything that resists evaluation stays opaque rather than being guessed
 * at — over-claiming a degree would report a genuine circle. And the surviving
 * proof requires the opaque pair to be *winning*, compared by condition-key
 * length alone: `!important` is honoured everywhere else, but an important
 * definite losing to a more specific opaque is not modelled here, so
 * `w-8! h-8! hover:w-[calc(1px*2)] hover:h-[calc(1px*2)] hover:rounded-full`
 * is excused on a box that is in fact 32×32. Same direction as the rest.
 *
 * An eleventh, the last consequence of the same scoping. The square proof reads
 * the whole cascade first and only falls back to the radius element's own scope,
 * so a host pair that outranks a descendant's own pair excuses it:
 * `hover:focus:w-8 hover:focus:h-8 [&_img]:w-4 [&_img]:h-8 [&_img]:hover:focus:rounded-full`
 * says nothing about an `<img>` that really is a 16x32 lozenge. Scoping the
 * proof outright would catch it, and would also be a stricter rule than this
 * codebase agreed to — host evidence is allowed to excuse a descendant, and is
 * only forbidden to condemn one. Pinned in the spec so the asymmetry cannot be
 * removed by accident.
 *
 * A twelfth, and the only one of them that points toward a false report rather
 * than a false excuse: CSS `if()` is not in `COMPARABLE_FUNCTION`, so a value
 * built out of one is indefinite exactly as `var()` is, whatever its branches
 * say. `size-[if(style(--x:yes):16px;else:16px)]` measures 16x16 and is reported
 * anyway, and so is the semicolon-free spelling -- which is what proves this is
 * opacity and not the semicolon check above. Raised by Hicks; `if()` is
 * Chrome-137 syntax and appears nowhere in this codebase. Tracked in #1078.
 *
 * Tracked in #1064, to close before #1046's larger churn leans on this rule.
 */
function shapeEvidence(classText) {
  const aspects = new Map();
  const animated = new Map();
  const widths = new Map();
  const heights = new Map();
  // Opaque readings are kept out of the cascade above and only ever compared
  // with each other, so they live apart from the definite ones. See `OPAQUE`.
  const widthsOpaque = new Map();
  const heightsOpaque = new Map();

  const add = (map, key, value, important) => {
    let entries = map.get(key);
    if (!entries) map.set(key, (entries = new Map()));
    entries.set(value, (entries.get(value) ?? false) || important);
  };

  /**
   * File one dimension reading. A `DROPPED` value is recorded nowhere, so the
   * declaration it failed to override still stands; an `OPAQUE` one is filed
   * apart from the cascade, where it can only ever meet its own twin -- and is
   * *also* filed in the cascade as `INDEFINITE`, because CSS applied it and threw
   * the declaration it overrode away. Omitting that resurrected the discarded
   * base: in `aspect-square w-[4px] h-[32px] hover:w-[calc(32px*1)]` the rule read
   * the width at `:hover` as 4px, contradicted `aspect-square` and reported a
   * genuine 32x32 circle. Raised by Bishop. `DROPPED` is the opposite case and
   * keeps its silence, because there CSS really does discard the new value.
   */
  const record = (definite, opaque, key, reading, value, important) => {
    if (reading === DROPPED) return;
    if (reading === OPAQUE || reading === OPAQUE_LIVE) {
      add(opaque, key, value, important);
      if (reading === OPAQUE_LIVE) add(definite, key, INDEFINITE, important);
    } else add(definite, key, reading, important);
  };

  for (const raw of classText.split(/\s+/)) {
    if (!raw) continue;
    const { segments, utility, important } = splitCandidate(raw);
    // A class Tailwind compiles to nothing supplies no evidence either. Leaving
    // it in let a dead `not-starting:w-8` tie with a live reading and excuse a
    // real lozenge, which is the same defect as reporting a dead radius, only
    // pointing the other way.
    if (isInertVariant(segments)) continue;
    const key = conditionKey(segments);

    const aspect = /^aspect-(\S+)$/.exec(utility);
    if (aspect) {
      // Only one `aspect-ratio` declaration reaches the box in any given state:
      // the most specific one. Recording every reading in a single map and
      // resolving the winner later — rather than keeping separate "proves square"
      // and "replaces" maps and cancelling one against the other — is what lets a
      // finer replacement erase a coarser proof. On hover, `animate-spin
      // aspect-[2/1] hover:aspect-auto` is not a proven lozenge, because `auto` is
      // the declaration the cascade applies there.
      //
      // Among *equals* the winner is the later declaration in the stylesheet, and
      // that is deliberately not modelled, because the class attribute does not
      // determine it. Tailwind emits each utility group in its own sorted order,
      // so `aspect-video aspect-square` and `aspect-square aspect-video` compile
      // to byte-identical CSS. Reading either as "last wins" would invent an
      // asymmetry CSS does not have. Equals are therefore unioned, and where they
      // disagree the rule keeps the reading that fails toward excusing (#1064).
      // `!important` is the one tie CSS does settle, and `resolve` honours it.
      //
      // An invalid ratio is dropped by CSS, so it is not recorded at all. An
      // *unreadable* one — `aspect-[calc(1)]`, `aspect-[var(--r)]` — is applied
      // but cannot be evaluated here, and is likewise not recorded, so a coarser
      // proof survives it rather than being withdrawn. Tailwind normalises
      // `aspect-[calc(1)]` to `aspect-ratio: calc(1)`, which is 1: withdrawing on
      // it reported a genuine circle. Under total ignorance of the finer value
      // the rule must guess the way that cannot provoke a dishonest waiver, and
      // `data-pf-radius="full"` is documented as the ledger of deliberate
      // *pills*, so forcing it onto a circle would corrupt that ledger. Settled
      // by Bishop, Hicks and Vasquez against my own initial reading.
      const kind = classifyAspect(aspect[1]);
      if (kind !== "invalid" && kind !== "unreadable")
        add(aspects, key, kind, important);
      continue;
    }
    // `size-N` is `w-N h-N`. Feeding it through the axis maps rather than
    // treating it as a standalone "it's a circle" flag is what lets a more
    // specific override contradict it: given `size-8 hover:w-4`, the hover state
    // really is a 4x8 lozenge, and short-circuiting here would excuse it. It
    // goes through the same `auto` exclusion, so `size-auto` proves nothing.
    const size = /^size-(\S+)$/.exec(utility);
    if (size) {
      const asWidth = dimensionReading(size[1], "width");
      const asHeight = dimensionReading(size[1], "height");
      record(widths, widthsOpaque, key, asWidth, size[1], important);
      record(heights, heightsOpaque, key, asHeight, size[1], important);
      continue;
    }
    if (
      utility === "animate-spin" ||
      utility === "animate-ping" ||
      utility === "pf-animate-spin"
    ) {
      add(animated, key, true, important);
      continue;
    }

    // `auto` is the one *indefinite* dimension keyword in everyday use, and CSS
    // only ignores `aspect-ratio` when both axes are definite. So `w-full h-auto
    // aspect-square` really is a circle.
    //
    // An indefinite reading is *recorded* rather than skipped, as `INDEFINITE`.
    // Skipping it dropped it out of the cascade entirely, so a coarser definite
    // value won by default: `w-4 h-8 hover:w-auto hover:aspect-square` was read as
    // 16x32 on hover when `width:auto` had in fact replaced `w-4`, leaving a 32x32
    // circle. Raised by Hicks. Recording it lets it win its own contest and then
    // report the axis as unpinned, which is the honest state — it neither excuses
    // a radius on its own nor contradicts a ratio.
    //
    // A value CSS *drops* is the opposite case and is not recorded at all, so the
    // declaration it failed to override still stands. See `DROPPED`.
    const width = /^w-(\S+)$/.exec(utility);
    if (width) {
      record(
        widths,
        widthsOpaque,
        key,
        dimensionReading(width[1], "width"),
        width[1],
        important,
      );
    }
    const height = /^h-(\S+)$/.exec(utility);
    if (height) {
      record(
        heights,
        heightsOpaque,
        key,
        dimensionReading(height[1], "height"),
        height[1],
        important,
      );
    }
  }

  /**
   * Values from the winning condition in state `target`, together with that
   * condition's specificity, so callers can tell which of two competing pieces of
   * evidence the cascade would actually honour.
   *
   * `!important` outranks specificity entirely: within one origin an important
   * declaration beats every non-important one, however finely selected. So the
   * contest runs among the important declarations if there are any, and among all
   * of them otherwise — which is why the first important reading resets the
   * standings rather than merely competing on size.
   */
  /**
   * How strongly a condition key competes in the cascade.
   *
   * Counting variants was the original proxy, and it is wrong in the one way a
   * proxy must not be: it can *condemn*. A media variant emits an at-rule around
   * an ordinary class, so `md:lg:w-8` is still one class -- specificity (0,1,0) --
   * while `hover:w-4` is a class plus a pseudo-class, (0,2,0). CSS honours the
   * hover width; counting segments made `md:lg:` (two) outrank `hover:` (one) and
   * reported a real circle. Raised by Bishop, and the same root cause Hicks found
   * from the excusing side, where `md:` and `hover:` tied and a lozenge escaped.
   *
   * So selector-bearing variants are counted first, and the raw segment count
   * breaks ties -- which is what CSS does too, since equal-specificity rules fall
   * to source order and Tailwind emits more heavily qualified utilities later.
   * Unknown variants are assumed to bear a selector, so a variant this list has
   * not heard of keeps the old, more cautious ranking.
   *
   * Two refinements on top, both raised against the first version of this
   * function. An arbitrary variant carrying an id outranks any number of classes
   * and pseudo-classes, because CSS weighs ids in a separate column -- raised by
   * Vasquez, whose `[#id]:` construction beat `hover:focus:` in CSS and lost
   * here, reporting a real circle. And `:where()` contributes nothing at all, so
   * a variant that is only a `:where()` wrapper is weighed like a media query --
   * raised by Hicks. Three tiers is still an approximation of a three-column
   * comparison, but it is one that no longer inverts on the cases either of them
   * could construct.
   */
  const conditionRank = (key) => {
    if (key === "") return 0;
    const segments = key.split("\u0000");
    let ids = 0;
    let selectors = 0;
    let types = 0;
    for (const segment of segments) {
      if (AT_RULE_VARIANT.test(segment)) continue;
      // A variant that is only a `:where()` takes the utility's own class inside
      // the wrapper, so the whole selector weighs zero and loses to the bare
      // utility rather than merely tying with it. Combined with any other variant
      // it becomes order-dependent -- whether the later variant lands inside the
      // wrapper or outside it decides whether anything survives -- and the sorted
      // condition key has already discarded that order, so the weight is unknown
      // rather than zero. Raised by Hicks.
      if (ZERO_SPECIFICITY_VARIANT.test(segment))
        return segments.length === 1 ? -1 : null;
      // Pseudo-class names are ASCII case-insensitive, so the test has to be too:
      // spelled `:WHERE(`, the guard missed and the flat counter tallied the
      // wrapper's contents, reporting a genuine square. Raised by Hicks.
      if (/:where\(/i.test(segment)) return null;
      // A `#` inside a quoted attribute value is not an id, so quoted strings go
      // before anything is counted. `[&[href="#top"]]` was being weighed in the id
      // column and so outranked every pseudo-class -- raised by Vasquez.
      const unquoted = segment.replace(/(['"])(?:\\.|(?!\1)[^\\])*\1/g, "");
      // An arbitrary variant is a whole selector rather than a single class, so
      // counting it as one inverted against stacked pseudo-classes: `[.a.b.c_&]`
      // weighs three classes yet ranked below `hover:focus:` -- raised by Hicks.
      // The `&` stands for the utility's own class, which every condition carries
      // and which therefore cancels.
      // A bare *type* selector sits in CSS's third column: below every class and
      // id, but above the at-rule-and-source-order tie-break that the segment
      // count stands in for. `group-[section]:` really does beat `md:print:` and
      // was losing to it, and `group-[.a_section]:` carries a class *and* a type,
      // so the class columns can tie and the type still decides -- both raised by
      // Hicks. Declaring every type-bearing payload unrankable stood in for the
      // column for two rounds, but it defers even when a *higher* column has
      // already settled the question: `hover:focus:` is two classes and beats
      // `group-[.a_section]:`'s one whatever the type column says, and
      // withdrawing there excused a real 16x32 lozenge -- also raised by Hicks.
      // So the column is carried honestly, and only a payload that weighs nothing
      // yet is not empty -- syntax this model does not recognise at all -- stays
      // unrankable, which unions it with whatever wins and so can only excuse.
      const unweighable = (payload, weight) =>
        weight.ids === 0 &&
        weight.selectors === 0 &&
        weight.types === 0 &&
        payload.replace(/[&*\s>+~,]/g, "") !== "";
      // Tailwind composes variants by prefixing, and the prefixes nest: `not-`
      // and `has-` wrap what follows in `:not()`/`:has()`, each of which weighs
      // its argument, while `group-`/`peer-` prepend a marker Tailwind puts
      // inside `:where()`, which weighs nothing. Matching a fixed prefix set with
      // a regex per branch left every composition unread -- `not-nth-[1_of_.a.b.c]`
      // fell through to the one-class default and excused a real 16x32 lozenge,
      // and there are more spellings than branches. So a segment is parsed
      // recursively instead: strip one prefix, weigh the remainder, add what the
      // prefix itself contributes. Raised by Hicks, seconded by Vasquez, both of
      // whom called the earlier prefix-tolerant regexes whack-a-mole.
      const weighPayload = (payload, synthesised) => {
        const weight = selectorWeight(synthesised);
        return unweighable(payload, weight) ? null : weight;
      };
      const weighSegment = (text) => {
        // A `/name` modifier names a group or peer and is no part of the selector.
        const bare = text.replace(/\/[^/[\]]*$/, "");
        // Inertness is deliberately *not* re-tested here. `isInertVariant` is the
        // sole gate and it runs strictly earlier, at both entry points, dropping
        // the whole candidate before anything is weighed -- verified by making this
        // branch throw, which no pin and no file in the repository could reach.
        // It used to be re-derived one level deep in three branches below, which is
        // exactly how it came to miss `not-not-`, `not-group-print` and friends;
        // deleting it leaves one source of truth rather than two that can drift.
        // Consolidation asked for by Vasquez and Bishop.
        // Tailwind's `in-*` variant puts the whole ancestor selector inside
        // `:where()` -- `in-[.a]` emits `:where(*:is(.a)) &` -- so it weighs
        // nothing at all. Falling through to the single-class default let it
        // outrank conditions that beat it in CSS. Raised by Hicks, who also caught
        // the first spelling of this test swallowing `in-range:`, a genuine
        // pseudo-class that weighs a full class -- hence the bracket.
        if (/^in-\[/i.test(bare)) return { ids: 0, selectors: 0, types: 0 };
        // The same is true of the *named* composition -- `in-focus-within:` emits
        // `:where(*:focus-within) &` -- so the `in-` prefix zeroes whatever
        // follows it, bracket or not. Matching only the bracket left the named
        // spelling on the one-class default, where it tied with a `focus:` that
        // beats it outright in CSS and the tie was unioned into an excuse.
        // `in-range` is the one token that must survive this: it is a registered
        // pseudo-class variant in its own right, emitting `&:in-range`, and CSS
        // has no other `:in-*` pseudo-class for the exception to have to grow for.
        // Raised by Hicks, twice -- once for swallowing `in-range:`, once for not
        // swallowing enough.
        if (/^in-/i.test(bare) && !/^in-range$/i.test(bare)) {
          return { ids: 0, selectors: 0, types: 0 };
        }
        // Tailwind emits a bare payload as written, and a selector *list* is
        // matched one entry at a time, so the weight is its most specific entry
        // rather than their sum -- `[.a,.b,.c_&]` was outranking `[.x.y_&]`,
        // raised by Hicks. `:is()` has exactly that rule, so it is weighed
        // through it. The `&` stands for the utility's own class, which every
        // condition carries and which therefore cancels.
        const arbitrary = /^\[([^]*)\]$/.exec(bare);
        if (arbitrary)
          return weighPayload(arbitrary[1], `:is(${arbitrary[1]})`);
        const named = /^([a-z-]+)-\[([^]*)\]$/i.exec(bare);
        if (named) {
          const head = named[1].toLowerCase();
          // Only the `:has()` and `:not()` families take a selector; every other
          // arbitrary payload -- `aria-[...]`, `data-[...]` -- is a single
          // attribute selector and is already right as one class.
          if (head === "has" || head === "not") {
            // `not-[@media(pointer:fine)]` negates an at-rule, not a selector:
            // it emits `@media not (pointer:fine)` and carries no specificity.
            // Reading it as `:not(@media...)` bought it a class it never has.
            // Only `media`, `supports` and `container` have a negated form --
            // `not-[@starting-style]`, `not-[@layer]`, `not-[@scope]`,
            // `not-[@page]`, `not-[@property]` and `not-[@keyframes]` all emit
            // nothing whatsoever, so they are inert and go unranked. Raised by
            // Hicks; the split was settled by compiling each form, and the three
            // negatable ones are whitelisted rather than the dead ones
            // blacklisted, so an at-rule nobody here has heard of fails toward
            // excusing rather than toward inventing a weight.
            const atRule = /^\s*@([a-z-]+)/i.exec(named[2]);
            if (head === "not" && atRule) {
              return NEGATABLE_AT_RULE.test(atRule[1])
                ? { ids: 0, selectors: 0, types: 0 }
                : null;
            }
            return weighPayload(named[2], `:${head}(${named[2]})`);
          }
          // `nth-[..]`/`nth-last-[..]` take the same `An+B [of S]` microsyntax as
          // the pseudo-class they emit, so they weigh one class plus their most
          // specific `of` argument.
          if (head === "nth" || head === "nth-last") {
            return weighPayload(named[2], `:${head}-child(${named[2]})`);
          }
          // Tailwind wraps the `.group`/`.peer` marker in `:where()`, so the
          // marker weighs nothing and the payload is the whole weight, and wraps
          // the payload in `:is()`, so a list takes its most specific entry.
          if (head === "group" || head === "peer")
            return weighPayload(named[2], `:is(${named[2]})`);
        }
        const prefix = /^(?:group|peer|has|not)-(?=.)/i.exec(bare);
        if (prefix) {
          const rest = bare.slice(prefix[0].length);
          const head = prefix[0].toLowerCase();
          // `not-` negates whatever kind of variant follows, and an at-rule
          // variant negated is still an at-rule: `not-print:` emits
          // `@media not print`, which adds no specificity at all. Recursing
          // blindly read it as `:not(print)` and bought it a class, which then
          // tied with a `focus:` that outranks it and unioned the tie into an
          // excuse. `not-hover:` really is `&:not(:hover)` and keeps its class.
          // Raised by Hicks.
          if (head === "not-" && AT_RULE_VARIANT.test(rest)) {
            // A negated at-rule carries no selector of its own, so it weighs
            // nothing and cannot break a tie. Whether it emits a rule at all is
            // `isInertVariant`'s question, and it has already been asked: a throw
            // placed here was reached by no pin and by no file in the repository.
            return { ids: 0, selectors: 0, types: 0 };
          }
          return weighSegment(rest);
        }
        return /#[\w-]/.test(bare)
          ? { ids: 1, selectors: 0, types: 0 }
          : { ids: 0, selectors: 1, types: 0 };
      };
      const weight = weighSegment(unquoted);
      if (weight === null) return null;
      ids += weight.ids;
      selectors += weight.selectors;
      types += weight.types;
    }
    return (
      ids * 1000000000 + selectors * 1000000 + types * 1000 + segments.length
    );
  };

  const resolve = (map, target, scopedTo = null) => {
    let best = null;
    let bestSize = -1;
    let importantSeen = false;
    // Conditions whose specificity cannot be weighed honestly are unioned with
    // whatever wins rather than allowed to win or lose, so an unreadable variant
    // widens the set of values considered and can only excuse.
    let unranked = null;
    for (const [key, entries] of map) {
      if (!conditionApplies(key, target)) continue;
      if (scopedTo !== null && selectorScope(key) !== scopedTo) continue;
      const size = conditionRank(key);
      for (const [value, important] of entries) {
        if (importantSeen && !important) continue;
        if (important && !importantSeen) {
          importantSeen = true;
          best = null;
          bestSize = -1;
          unranked = null;
        }
        if (size === null) {
          if (!unranked) unranked = new Set();
          unranked.add(value);
          continue;
        }
        if (size > bestSize) {
          best = new Set([value]);
          bestSize = size;
        } else if (size === bestSize) {
          if (!best) best = new Set();
          best.add(value);
        }
      }
    }
    if (unranked) {
      if (!best) best = new Set();
      for (const value of unranked) best.add(value);
    }
    return { values: best, size: bestSize };
  };

  /** True when the element renders as a circle in the state `segments` selects. */
  return function isCircularAt(segments) {
    const target = conditionKey(segments);
    const scope = selectorScope(target);
    const w = resolve(widths, target);
    const h = resolve(heights, target);
    const wScoped = resolve(widths, target, scope);
    const hScoped = resolve(heights, target, scope);

    // Squareness needs *comparable* lengths on both axes, not merely equal
    // spelling: `w-full h-full` is 100% of two different containing-block axes.
    // Every non-`auto` value still counts as definite for the contradiction test
    // below, because that only asks whether the axis is pinned at all.
    const provesSquare = (a, b) => {
      if (!a.values || !b.values) return false;
      for (const value of a.values) {
        if (value === INDEFINITE) continue;
        if (
          [...b.values].some(
            (other) => other !== INDEFINITE && sameLength(value, other),
          )
        ) {
          return true;
        }
      }
      return false;
    };

    // The proof is tried twice: against the whole cascade, and against the radius
    // element's own scope. Raised by Bishop, and it is the mirror of Hicks's
    // finding below. That one stopped host evidence *condemning* a descendant,
    // but the square proof was left reading the whole cascade, so host evidence
    // could still condemn indirectly — by outranking the descendant's own pair
    // and displacing it: a two-variant host pair beat an unvariant descendant
    // pair, and an image that was 32x32 in every state was reported. Trying the
    // scoped reading as a fallback only ever *adds* an excuse, so it cannot
    // manufacture a false report.
    if (provesSquare(w, h) || provesSquare(wScoped, hScoped)) return true;

    // An opaque value proves nothing on its own, but the *same* opaque value on
    // both axes still proves a square: whatever CSS makes of `calc(1px*2)`, it
    // makes the same of it twice. `isComparableArbitrary` keeps this honest by
    // refusing axis-relative values, so `w-[calc(100%/3)] h-[calc(100%/3)]` is
    // still two different lengths and is still reported.
    //
    // The pair only speaks where it actually wins. Raised by Hicks: in
    // `w-[calc(1px*2)] h-[calc(1px*2)] hover:w-8 hover:rounded-full` the hover
    // width is a more specific declaration, so on hover the box is 32x2 and the
    // standing opaque pair is stale. Comparing specificity against the definite
    // reading on each axis is what keeps the shortcut from outliving its state,
    // while still letting `hover:w-[calc(1px*2)] hover:h-[calc(1px*2)]` overrule
    // a base `w-8 h-8`.
    // The same scoped fallback as the definite proof, for the same reason: a
    // host pair must not displace the radius element's own.
    const opaqueProves = (side, wRef, hRef) => {
      const a = resolve(widthsOpaque, target, side);
      const b = resolve(heightsOpaque, target, side);
      if (!a.values || !b.values) return false;
      if (!(a.size >= wRef.size && b.size >= hRef.size)) return false;
      for (const value of a.values) {
        if ([...b.values].some((o) => sameLength(value, o))) return true;
      }
      return false;
    };
    if (
      opaqueProves(undefined, w, h) ||
      opaqueProves(scope, wScoped, hScoped)
    ) {
      return true;
    }

    // `aspect-square` and the spin animations both assert circularity without
    // naming dimensions, but they are overruled by different evidence, and
    // conflating them produced false reports on live pulse indicators.
    //
    // `aspect-square` loses to any *definite* pair, because CSS resolves
    // `width`/`height` before `aspect-ratio` has anything to say: given
    // `aspect-square w-4 h-8` the box is 4x8 and the ratio is simply ignored.
    // That precedence is a fact about the cascade, so it holds even when the two
    // axes are not comparable — `aspect-square w-full h-8` is not a circle.
    //
    // An animation is a *heuristic*, not a declaration, so nothing in the cascade
    // overrules it. It loses only to a pair proved to differ, which needs both
    // axes comparable: `animate-spin w-4 h-8` is a lozenge and is reported, but
    // `animate-ping w-full h-full` inside a square parent is exactly what a pulse
    // indicator is, and the rule cannot see the parent to say otherwise.
    // An axis counts as pinned only if the reading that wins its cascade is a
    // real length. A tie that includes an `INDEFINITE` reading is unresolvable,
    // so it fails toward excusing like every other tie: the axis is not definite.
    const pinned = (side) =>
      Boolean(side.values) && ![...side.values].includes(INDEFINITE);
    // Contradiction evidence must come from the *same selector scope* as the
    // radius. A radius scoped to a descendant or a pseudo-element is judged on
    // that element's own dimensions, and the host's say nothing about it: given
    // `size-4 [&_img]:h-8 [&_img]:aspect-square [&_img]:rounded-full` the host is
    // 16px wide and the `<img>` is 32x32, so pairing the host's width with the
    // image's height invented a lozenge that exists on neither. Raised by Hicks.
    // Host evidence may still *excuse* — that is the documented limitation above,
    // and it fails toward excusing — but it may no longer condemn.
    // `scope`, `wScoped` and `hScoped` are resolved at the top of this function,
    // because the square proof above needs the same readings.
    const definite = pinned(wScoped) && pinned(hScoped);
    const unequal =
      definite &&
      [...wScoped.values].every(isComparableDimension) &&
      [...hScoped.values].every(isComparableDimension);
    const pairSize = Math.min(wScoped.size, hScoped.size);

    // The winning `aspect-*` for this state, read once. Conditions of equal
    // specificity are unioned rather than resolved — including two spellings of
    // the *same* condition, whose stylesheet order the class attribute does not
    // determine (#1064) — and where they disagree the rule takes the reading that
    // fails toward excusing: `square` still counts as evidence, and an `unknown`
    // alongside a `nonsquare` withdraws the proof.
    const aspect = resolve(aspects, target);
    const kinds = aspect.values ?? new Set();
    // A *proven* non-square ratio contradicts an animation as well as standing in
    // for dimensions — `animate-spin aspect-[2/1]` is a 2:1 box whatever its size.
    // A mere replacement does not: `animate-spin aspect-auto` says nothing about
    // shape. Either reading lapses once a definite pair at equal or finer
    // specificity overrides the ratio, at which point the box is whatever those
    // dimensions say.
    // A definite pair on both axes neutralises `aspect-ratio` outright, and that
    // is not a specificity contest: specificity is resolved per property, so the
    // winning `width`/`height` and the winning `aspect-ratio` are decided in
    // separate cascades and only then combined. `width` and `height` win that
    // combination whenever both are definite, however coarsely they were
    // selected. Raised by Hicks: comparing the pair's variant count against the
    // ratio's excused `w-4 h-8 hover:aspect-square hover:rounded-full`, which is a
    // 16x32 lozenge on hover.
    const ratioApplies = !definite;
    const squareHolds = kinds.has("square") && ratioApplies;
    // No `!kinds.has('square')` guard: this is only ever consulted once
    // `squareHolds` has come out false, which given the shared `ratioApplies`
    // already implies the winner was not square.
    const nonSquareHolds =
      kinds.has("nonsquare") && !kinds.has("removed") && ratioApplies;

    /**
     * True when `map`'s assertion of circularity survives every contradiction:
     * an entry is neutralised only by a contradiction at equal or finer
     * specificity, since a base-state animation still holds in states no variant
     * override reaches.
     */
    const overruled = (map, contradictions) => {
      for (const [key] of map) {
        if (!conditionApplies(key, target)) continue;
        const size = conditionRank(key);
        // An unweighable rank on either side counts as reaching, for the same
        // reason it ties in `resolve`: the comparison is not knowable, and the
        // honest direction is the excusing one.
        if (
          contradictions.some(
            (c) => c.ok && (c.size === null || size === null || c.size >= size),
          )
        )
          continue;
        return true;
      }
      return false;
    };
    return (
      squareHolds ||
      overruled(animated, [
        { ok: unequal, size: pairSize },
        { ok: nonSquareHolds, size: aspect.size },
      ])
    );
  };
}

/**
 * How an `aspect-*` argument bears on squareness:
 *
 * - `square`    — provably 1:1, so it is evidence of a circle.
 * - `nonsquare` — provably not 1:1, so it proves the box is a lozenge.
 * - `unknown`   — syntactically fine but unreadable here, so it replaces an
 *   earlier ratio without proving anything: `auto`, a CSS-wide keyword such as
 *   `initial`, a value function, or a degenerate ratio, which CSS treats as
 *   `auto`. A `var()` lands here as a function call, which is why it needs no
 *   branch of its own.
 * - `invalid`   — not a valid `aspect-ratio` value at all, so CSS drops the
 *   declaration and an earlier ratio stands. A negative component lands here, as
 *   does anything that is not a CSS number: `[0x2]` is JavaScript's idea of a
 *   number, not CSS's, `[1.]` is nobody's, and `[banana]` is not a number at all.
 *   An unrecognised *function* is invalid too — `banana()` parses as a function
 *   but no such function exists, so the declaration is dropped rather than
 *   merely unreadable.
 *
 * Ratios are read by value rather than by spelling, so `[2/2]` is square and
 * `[4/2]` is not. The grammar is `auto || <ratio>`, so a bare `auto` may sit
 * beside a ratio and the ratio still governs a non-replaced element: `[auto_2/1]`
 * is a 2:1 box. Tailwind writes spaces as `_` and also accepts a bare fraction
 * (`aspect-3/2`). The sign test is on the components rather than the quotient,
 * because `[-1/-1]` divides to 1.
 */
function classifyAspect(value) {
  if (value === "square") return "square";
  // Spelling-based, and deliberately so: Tailwind's `aspect-video` is 16/9 today,
  // but this will not track a theme that redefines the ratio. It is a named
  // utility rather than a value, so there is nothing here to read.
  if (value === "video") return "nonsquare";
  const bracketed = /^\[(.+)\]$/.exec(value);
  const body = (bracketed ? bracketed[1] : value).replace(/_/g, " ").trim();
  // A CSS-wide keyword is valid but says nothing about shape. They divide,
  // though, and Hicks drew the line in the right place: `initial` and `unset`
  // both compute to `auto`, which is a *readable removal* of the ratio, whereas
  // `inherit`, `revert` and `revert-layer` expose a parent or lower-cascade
  // value the rule cannot see. Under the scoped reading agreed with Bishop and
  // Vasquez, the unknowable ones must not withdraw a coarser proof.
  if (/^(?:initial|unset)$/i.test(body)) return "removed";
  if (/^(?:inherit|revert|revert-layer)$/i.test(body)) return "unreadable";
  // `auto` is a standalone term in the grammar, not part of the ratio, so it is
  // lifted out before the remainder is read as one. It may appear at most once.
  const terms = body.split(/\s+/);
  const autos = terms.filter((term) => /^auto$/i.test(term));
  if (autos.length > 1) return "invalid";
  const rest = terms.filter((term) => !/^auto$/i.test(term));
  if (rest.length === 0) return autos.length ? "removed" : "invalid";
  // Splitting on *top-level* `/` only. A naive split rejects `calc((1/2)/(3/4))`,
  // which is a single valid number, by counting its internal divisions as ratio
  // separators. Raised by Hicks.
  const parts = splitTopLevel(rest.join(" "), "/");
  if (parts.length > 2) return "invalid";
  // A *known* value function is valid CSS but not readable here. An unrecognised
  // name is not a function at all, and neither is a known one called with no
  // arguments, so the whole declaration is invalid. Both outcomes now leave an
  // earlier proof standing, for different reasons: CSS drops the invalid one, and
  // the rule declines to guess at the unreadable one. See `'unreadable'`.
  const functions = parts.flatMap((part) => [
    ...part.matchAll(/([a-z][a-z0-9-]*)\s*\(\s*(\)?)/gi),
  ]);
  if (functions.length > 0) {
    const sound = functions.every(
      ([, name, empty]) => VALUE_FUNCTION.test(name) && empty !== ")",
    );
    return sound ? "unreadable" : "invalid";
  }
  if (parts.some((part) => !CSS_NUMBER.test(part))) return "invalid";
  const numbers = parts.map(Number);
  if (numbers.some((n) => n < 0)) return "invalid";
  const [antecedent, consequent = 1] = numbers;
  if (antecedent === 0 || consequent === 0) return "removed";
  const square = antecedent / consequent === 1;
  // With `auto` present the specified ratio governs only an element that has no
  // natural one, and the rule cannot see whether it is looking at a `<div>` or a
  // 2:1 `<img>`. Raised by Hicks. So `auto` may still prove squareness, which
  // errs toward excusing, but it never proves a lozenge — `auto 2/1` does not
  // contradict a spinner, because on a replaced element it may not be 2:1 at all.
  if (autos.length && !square) return "removed";
  return square ? "square" : "nonsquare";
}

/**
 * Split on a top-level separator, ignoring any that sits inside brackets,
 * parentheses or braces.
 */
/**
 * The index of the `)` that closes the `(` at `open`, or `-1` if the text runs
 * out first.
 */
function closingParen(text, open) {
  let depth = 0;
  for (let i = open; i < text.length; i += 1) {
    if (text[i] === "(") depth += 1;
    else if (text[i] === ")") {
      depth -= 1;
      if (depth === 0) return i;
    }
  }
  return -1;
}

/**
 * `sin()`, `cos()` and friends return a `<number>`, not a length, and angle units
 * are legal only as their arguments. So `calc(100px*sin(90deg))` is a perfectly
 * ordinary length that the token walk was refusing on sight of `deg` -- raised by
 * Vasquez, whose case reported a real circle. Collapsing each such call to a bare
 * `1` before the walk models exactly that: the call is a scalar, and whatever
 * units sit inside it are the function's business rather than the value's.
 *
 * Widening the unit and function whitelists instead, as first proposed, would
 * have excused `w-[10deg]`, `w-[sin(90deg)]` and `w-[sign(2rem)]`, all of which
 * are `<number>`s or angles rather than lengths and are correctly refused.
 *
 * `asin()`, `acos()`, `atan()` and `atan2()` return *angles*, so they are
 * deliberately absent and stay incomparable.
 *
 * A call is only collapsed when its argument count is one CSS accepts. An
 * over-supplied `sin(90deg,0deg)` is invalid, so CSS drops the declaration and
 * there is no evidence either way -- collapsing it regardless manufactured a
 * length out of a value that never applied. Raised by Hicks.
 *
 * Argument *type* is a precondition on the same footing, and so is an argument
 * being present at all. CSS Values 4 requires `sin()`/`cos()`/`tan()` to resolve
 * to a number or an angle and `pow()`/`sqrt()`/`log()`/`exp()` to resolve to
 * numbers, so `sin(10px)`, `sin(50%)` and `pow(2px,2)` are type errors, and
 * `sin()` is a parse error; each drops the declaration and leaves the base width
 * standing. Collapsing them regardless read the width as a definite `1px`, which
 * reported a genuine circle in the first case and excused a genuine lozenge in
 * the second. `sign()` is the documented exception: it takes a calculation of
 * any type. Raised by Vasquez and by Hicks.
 *
 * The collapse is a statement about *type*, not magnitude: `sin()` contributes a
 * dimensionless factor, and `calc(32px*sin(45deg))` is still never equated with
 * `32px`, because two arbitrary values are only ever compared as written. That
 * is also why a substituted argument needs no special case -- `sin(var(--a))`
 * collapses to the same scalar on both sides of a comparison, so an element
 * sized `w-[calc(32px*sin(var(--a)))] h-[calc(32px*sin(var(--a)))]` is square
 * whatever `--a` turns out to be, while the same width against a plain `h-8`
 * stays unprovable and is still reported. Guarding on `var()` here only broke
 * the first of those.
 *
 * Nested calls need no special handling either: a skipped outer call leaves the
 * cursor inside itself, so the inner call is collapsed on the next pass and the
 * outer is re-examined against the collapsed argument. `sin(sign(2rem))` is
 * therefore correctly collapsed, while `sin(10px)` stays refused.
 */
const ANGLE_UNIT = /^(?:deg|grad|rad|turn)$/i;
const BARE_DIMENSION = /^(?:\d+\.?\d*|\.\d+)(?:e[+-]?\d+)?([a-z]+|%)$/i;
function argumentTypeAccepted(name, list) {
  const angular = name === "sin" || name === "cos" || name === "tan";
  for (const argument of splitTopLevel(list, ",")) {
    const trimmed = argument.trim().replace(/^[_\s]+|[_\s]+$/g, "");
    if (trimmed === "") return false;
    if (name === "sign") continue;
    // Only a *bare* dimension is typed here. An argument carrying arithmetic may
    // have its units cancel -- `sin(1px/1px)` is a dimensionless number and is
    // perfectly legal -- and rejecting it left a genuine
    // `w-[calc(1px*sin(1px/1px))] h-[calc(...)]` square unprovable and reported.
    // Raised by Vasquez. Deciding those honestly means evaluating the argument's
    // type, which is what `lengthDegree` does one layer up; doing it here would
    // be mutually recursive for no gain, because the type errors seen in practice
    // are written plainly.
    const dimension = BARE_DIMENSION.exec(trimmed);
    if (dimension !== null) {
      if (!angular || !ANGLE_UNIT.test(dimension[1])) return false;
      continue;
    }
    // A compound argument is asked what it resolves to rather than waved past:
    // `sin(1px*2)` is a length and CSS drops the declaration, and accepting it
    // excused a lozenge the base rule reports. Degree 0 is a plain `<number>`,
    // which every one of these functions takes. `NaN` is a disagreement of types
    // -- `sin(1px_+_1)` -- and unreadable-with-no-call is a grammar this model
    // fully understands and still cannot parse, as in `sin(1deg*1px)`; both are
    // provably dropped. Anything genuinely unknowable is left alone. Raised by
    // Hicks. This does recurse -- `lengthDegree` collapses numeric calls of its
    // own -- but strictly inward, one nesting level per step, so `sin(sign(2rem))`
    // resolves and terminates.
    const degree = lengthDegree(trimmed);
    if (degree !== null && degree !== 0) return false;
    if (degree === null && provablyInvalidValue(trimmed)) return false;
  }
  return true;
}
const NUMERIC_FUNCTION_ARITY = new Map([
  ["sin", [1]],
  ["cos", [1]],
  ["tan", [1]],
  ["sqrt", [1]],
  ["exp", [1]],
  ["sign", [1]],
  ["log", [1, 2]],
  ["pow", [2]],
]);
const NUMERIC_FUNCTION = /(?:^|[^\w-])(sin|cos|tan|pow|sqrt|log|exp|sign)\(/i;
function collapseNumericFunctions(text) {
  let out = text;
  let from = 0;
  // Bounded rather than `while (true)`: every pass either collapses a call or
  // steps past it, so the bound is only a backstop against a malformed value.
  for (let pass = 0; pass < 64; pass += 1) {
    const match = NUMERIC_FUNCTION.exec(out.slice(from));
    if (match === null) break;
    const at = from + match.index;
    const open = at + match[0].length - 1;
    const nameStart = open - match[1].length;
    const close = closingParen(out, open);
    if (close === -1) break;
    const arity = NUMERIC_FUNCTION_ARITY.get(match[1].toLowerCase());
    const list = out.slice(open + 1, close);
    const given = splitTopLevel(list, ",").length;
    if (
      !arity.includes(given) ||
      !argumentTypeAccepted(match[1].toLowerCase(), list)
    ) {
      from = open + 1;
      continue;
    }
    out = `${out.slice(0, nameStart)}1${out.slice(close + 1)}`;
    from = 0;
  }
  return out;
}

/**
 * A selector fragment's weight in the id and class columns.
 *
 * `:is()`, `:not()` and `:has()` take the weight of their *most specific*
 * argument and contribute nothing themselves, so a flat tally over the text
 * inflates them: `:is(.a,.b)` is one class, not the three that counting `:is`,
 * `.a` and `.b` separately produces. That inflation let a two-class condition
 * beat a three-class one and turned a genuine circle into a report -- raised by
 * Vasquez. The arguments are weighed recursively so a nesting is weighed the
 * same way.
 *
 * `:nth-child()` and `:nth-last-child()` are the same shape with a different
 * rule: the pseudo-class counts for itself, and an `of S` clause adds the most
 * specific selector in `S` on top. Tallying that list flatly made
 * `:nth-child(2 of .a,.b,.c)` four classes where CSS says two -- raised by
 * Hicks.
 *
 * `:where()` never arrives here: `conditionRank` refuses to rank any segment
 * containing one, because a sorted condition key has already lost the order that
 * decides what the wrapper encloses.
 *
 * `&` stands for the utility's own class, which every condition carries and
 * which therefore cancels on both sides of any comparison.
 */
function selectorWeight(selector) {
  let ids = 0;
  let selectors = 0;
  let types = 0;
  let rest = selector.replace(/&/g, "");
  for (;;) {
    const match =
      /:(is|matches|any|not|has|nth-child|nth-last-child|host-context|host)\(/i.exec(
        rest,
      );
    if (!match) break;
    const open = match.index + match[0].length - 1;
    const close = closingParen(rest, open);
    // An unclosed argument list cannot be weighed, and the loop must not spin on
    // it; what is left is counted flatly instead.
    if (close < 0) break;
    const name = match[1].toLowerCase();
    let list = rest.slice(open + 1, close);
    if (name.startsWith("nth-")) {
      // The pseudo-class weighs for itself; only an `of S` clause adds more.
      // Tailwind writes the separating spaces as underscores.
      selectors += 1;
      const of = /[\s_]of[\s_]/i.exec(list);
      list = of ? list.slice(of.index + of[0].length) : "";
    } else if (name === "host" || name === "host-context") {
      // Same shape: `:host` and `:host-context` weigh a pseudo-class for
      // themselves and add the *most specific* entry of the argument, which is a
      // selector list like any other. Tallying that list flatly made
      // `:host(.a,.b,.c)` four classes where CSS says two, which outranked a
      // genuine `.x.y.z` ancestor and picked a lozenge. Raised by Vasquez, in the
      // reachable descendant spelling `[:host(.a,.b,.c)_&]:` Hicks supplied --
      // `[&:host(...)]` cannot match a shadow host at all.
      selectors += 1;
    }
    let bestIds = 0;
    let bestSelectors = 0;
    let bestTypes = 0;
    if (list !== "") {
      for (const argument of splitTopLevel(list, ",")) {
        const weight = selectorWeight(argument);
        if (
          weight.ids > bestIds ||
          (weight.ids === bestIds &&
            (weight.selectors > bestSelectors ||
              (weight.selectors === bestSelectors && weight.types > bestTypes)))
        ) {
          bestIds = weight.ids;
          bestSelectors = weight.selectors;
          bestTypes = weight.types;
        }
      }
    }
    ids += bestIds;
    selectors += bestSelectors;
    types += bestTypes;
    rest = rest.slice(0, match.index) + rest.slice(close + 1);
  }
  ids += (rest.match(/#[\w-]/g) || []).length;
  selectors += (rest.match(/\.[\w-]|:{1,2}[a-z-]|\[[\w-]/gi) || []).length;
  // A type selector may carry a namespace prefix -- `*|section`, `svg|a` -- which
  // is still a single type. Neither `*` nor `|` opened a compound here, so the
  // whole thing went uncounted and handed the type column to nobody. Raised by
  // Hicks. An identifier only starts a compound after a combinator, and Tailwind
  // writes the descendant combinator as an underscore, so `_` is a boundary too;
  // an *escaped* underscore is a literal character inside a class name and is
  // deliberately not, because with a real type column a miscount now misranks
  // rather than merely unranking.
  types += (
    rest.match(
      /(?:^|[\s>+~,]|(?<!\\)_)(?:(?:[a-z][\w-]*|\*)\|)?[a-z][\w-]*/gi,
    ) || []
  ).length;
  return { ids, selectors, types };
}

function splitTopLevel(text, separator) {
  const parts = [];
  let depth = 0;
  let start = 0;
  for (let i = 0; i < text.length; i += 1) {
    const ch = text[i];
    if (ch === "(" || ch === "[" || ch === "{") depth += 1;
    else if (ch === ")" || ch === "]" || ch === "}") {
      // Defensive, and deliberately unfalsifiable here: an unbalanced closer
      // always leaves a stray `)` inside some part, which `CSS_NUMBER` rejects,
      // so every input that could drive the depth negative is already invalid
      // whichever way it splits. Kept so the helper's contract stands alone.
      if (depth > 0) depth -= 1;
    } else if (ch === separator && depth === 0) {
      parts.push(text.slice(start, i));
      start = i + 1;
    }
  }
  parts.push(text.slice(start));
  return parts.map((part) => part.trim());
}

/**
 * Functions that may legally appear where a `<number>` is expected. Anything
 * else spelled with parentheses is an unknown function, which makes the
 * declaration invalid rather than merely unreadable.
 */
const VALUE_FUNCTION =
  /^(?:var|env|attr|calc|min|max|clamp|round|mod|rem|abs|sign|sin|cos|tan|asin|acos|atan|atan2|pow|sqrt|hypot|log|exp)$/i;

/**
 * CSS's `<number>` grammar, which is neither a superset nor a subset of
 * JavaScript's. CSS accepts a leading `+` where `Number.parseFloat` is happy but
 * the old pattern was not; CSS rejects a trailing bare `.`, hex, and `Infinity`,
 * all of which JavaScript reads. A bare `.5` is fine in both.
 */
const CSS_NUMBER = /^[+-]?(?:\d+(?:\.\d+)?|\.\d+)(?:e[+-]?\d+)?$/i;

/**
 * True when a dimension value leaves its axis *indefinite*, so it neither proves
 * squareness nor contradicts an `aspect-ratio`. `auto` is the everyday case; the
 * CSS-wide keywords land here because the initial value of `width`/`height` is
 * `auto`, and `inherit` because the inherited computed value is not knowable at
 * lint time. Keyword matching is case-insensitive, as CSS is.
 *
 * Anything containing `var()` is indefinite too, wherever the `var()` sits. An
 * earlier draft exempted `var()` nested inside `calc()`, reasoning that `calc()`
 * cannot produce a keyword. That was wrong, and Hicks's counter-example is worth
 * keeping: substitution happens *before* the value is parsed, so if the custom
 * property holds `auto` then `calc(auto + 0px)` is invalid at computed-value
 * time, and an invalid non-inherited declaration falls back to the initial
 * value -- which for `width`/`height` is `auto`. So the value really can be
 * indefinite, and the only honest reading of any `var()` is "unknown".
 */
const INDEFINITE = Symbol("indefinite");

/**
 * A declaration CSS throws away, which is a different thing from one that sets an
 * indefinite value. `height:banana` is discarded at parse time, so an earlier
 * `h-8` still applies and the element is still 32px tall; `height:auto` is kept
 * and really does replace it. Recording the first as `INDEFINITE` conflated the
 * two and unpinned an axis CSS had left pinned, withdrawing a real proof:
 * `w-8 h-8 hover:h-[banana] hover:rounded-full` was reported as a lozenge when it
 * is a 32x32 circle. Raised by Hicks. A dropped reading is therefore not recorded
 * at all, exactly as an invalid `aspect-ratio` already was.
 */
const DROPPED = Symbol("dropped");

/**
 * A value that carries a real dimension but that the rule cannot evaluate:
 * `w-[calc(1px*2)]`, `w-[var(--x)]`, `w-[-1px]`.
 *
 * This is the third outcome Hicks argued the classifier needed, though not the
 * one he prescribed. He asked for an *opaque winner* that replaces whatever came
 * before; that is the false-positive direction, and the probe that settles it is
 * `w-8 h-8 hover:h-[calc(1px/1px)] hover:rounded-full`. CSS drops that height
 * (dividing a length by a length yields a number), so the element stays a 32x32
 * circle — but a replacing winner unpins the axis and reports it. The rule
 * cannot type-check CSS math well enough to know which happened, and where it
 * cannot know it must not withdraw a proof. That is the same reading Bishop,
 * Hicks and Vasquez converged on for unreadable `aspect-ratio` values, applied
 * to the axis that had not caught up with it.
 *
 * So an opaque value is not recorded in the cascade at all. It keeps exactly one
 * power: when the *same* opaque value appears on both axes it still proves
 * squareness, because whatever CSS decides it decides identically for both.
 */
const OPAQUE = Symbol("opaque");

/**
 * An opaque value the rule is confident CSS *applies*: the arithmetic was
 * followed and came out as a length, or it is a `var()`, which is always applied
 * even though its value cannot be read here. It withdraws the axis as well as
 * joining the opaque map, because the declaration it overrode is gone.
 *
 * Plain `OPAQUE` is the residue -- arithmetic that could not be followed at all,
 * which is disproportionately CSS that is simply malformed (`calc(1px 2px)`,
 * `min(1px,2)`). CSS drops those, so the base must survive, and withdrawing the
 * axis for them turned four proven circles into reports.
 */
const OPAQUE_LIVE = Symbol("opaque-live");

/**
 * The arbitrary forms the rule reads as a definite length: a non-negative
 * `<number><unit>`, or a bare zero. Anything richer -- a math function, a sign,
 * a `var()` -- is `OPAQUE`.
 */
const SIMPLE_LENGTH = /^\+?(?:\d+\.?\d*|\.\d+)(?:e[+-]?\d+)?(%|[a-z]+)?$/i;

/**
 * A CSS comment is whitespace, and Tailwind copies one straight through into
 * the stylesheet: `w-[1px/**\/]` really does emit `width: 1px/**\/` and really
 * does compute to 1px. Reading the comment as part of the value made an
 * ordinary length unrecognisable, and an unrecognisable length was then
 * condemned as invalid and the declaration treated as dropped -- so a genuine
 * 1x1 circle was reported. Raised by Hicks. The unterminated form is stripped
 * too: CSS closes a comment at end-of-input rather than discarding the
 * declaration.
 *
 * A comment separates tokens, though -- it is whitespace, not nothing -- so it
 * has to be replaced by a space rather than deleted. The simple spelling
 * `1/**\/px` never gets this far: `isPlausibleLength` reads the raw text, sees a
 * unitless non-zero `1`, and drops the declaration, exactly as Chromium does.
 * The spelling that does get here is one whose halves are each already
 * plausible: `calc(1/**\/0px)` is `calc(1 0px)`, which Chromium drops, and
 * deleting the comment spliced it into a valid `calc(10px)` and falsely proved a
 * square. The outer edges are deliberately *not* trimmed, though nothing in the
 * suite currently distinguishes the two: a leading comment is trailing
 * whitespace to CSS, but Tailwind reads its data-type hints off the candidate
 * before CSS ever sees it, and `w-[/**\/length:1rem]` is not a hint -- it emits
 * the literal `width: /**\/length:1rem`, which Chromium drops. Trimming would
 * slide such a value back under the `^length:` anchor and read a dropped
 * declaration as a valid 1rem. Today `isPlausibleLength` refuses it first, on
 * the bare `length` ident, so both spellings report and the mutation survives;
 * the untrimmed form is kept because it is the one that stays correct if that
 * earlier gate ever moves. Checked against Tailwind's emitted CSS and Chromium's
 * parser rather than against the tokenizer grammar. Raised by Vasquez.
 */
const stripCssComments = (text) => text.replace(/\/\*[^]*?(?:\*\/|$)/g, " ");

/**
 * Whether a single candidate opens a CSS comment that does not also close
 * inside it.
 *
 * Quoting is honoured for the *opening* only, which is exactly how CSS reads it:
 * a `/*` inside a string is two characters of that string, so
 * `before:content-['/**\/']` starts nothing. Once a comment is open the
 * tokenizer stops recognising strings altogether, so a later `'*\/'` in some
 * other candidate really does close it -- and everything between, radius
 * included, is gone. Both directions raised by Hicks.
 */
/**
 * Tailwind rewrites an arbitrary value before emitting it, inserting spaces around
 * operators -- but only inside the math functions it knows, and only for the
 * *nearest* enclosing one. `calc(1px+2px)` is repaired to `calc(1px + 2px)`;
 * `abs(1px+2px)` is emitted verbatim; and the two nest, so `abs(calc(1px+2px))`
 * is repaired on the inside and computes, while `calc(abs(1px+2px))` is not and
 * drops. Classifying by the outermost function got both of those backwards.
 * Raised by Hicks.
 *
 * Modelling the rewrite once, here, also settles what is a comment: Tailwind
 * separates the `/` and the `*` of `calc(1px/*)` into `calc(1px / *)`, so no
 * comment is opened, while `abs(1px/*)` and a bare `1px/*` really do open one.
 * Scanning the class as written could not tell those apart.
 *
 * The membership of this set is measured, not inferred from the shape of the
 * names: `abs()` and `sign()` take a single argument rather than a list and are
 * emitted exactly as written, so `abs(1px+2px)` drops where `abs(1px_+_2px)`
 * becomes `abs(1px + 2px)` and computes to 3px. Granting them the amnesty
 * excused values the browser had thrown away. Raised by Hicks; Vasquez noted the
 * same asymmetry from the other side.
 */
/**
 * Which math functions Tailwind rewrites the interior of, and how.
 *
 * Every CSS math function except `abs()` and `sign()`, measured one at a time
 * against emission rather than taken from the grammar: `pow(1+1,2)` is emitted as
 * `pow(1 + 1, 2)` and computes to 4, while `abs(1+1)` and `sign(1+1)` are emitted
 * verbatim and dropped. An earlier list held only the six that had come up in
 * review, so `pow`, `sqrt`, `log`, `exp` and the trigonometric family were read as
 * unrepairable and their values condemned though the browser computes them.
 * Raised by Hicks.
 */
const REPAIRABLE_MATH = new Set([
  "calc",
  "min",
  "max",
  "clamp",
  "mod",
  "rem",
  "sin",
  "cos",
  "tan",
  "asin",
  "acos",
  "atan",
  "atan2",
  "pow",
  "sqrt",
  "hypot",
  "log",
  "exp",
  "round",
]);

const DIGIT = /[0-9]/;
/**
 * What Tailwind will accept as the name in front of `(`: digits and *lowercase*
 * letters, stopping at anything else. Both halves are load-bearing and both were
 * got wrong by a regex that looked more natural. `foo-calc(1px/*)` is repaired,
 * because the scan stops at the hyphen and finds `calc` -- reading the name as
 * `foo-calc` withheld repair, left the comment intact, and silently switched the
 * check off for a live lozenge. `2calc(1px/*)` is *not* repaired, because the
 * scan takes the leading digit and finds `2calc` -- reading it as `calc` invented
 * a repair, dissolved a comment that really does swallow the rule that follows,
 * and reported an element whose radius the browser had already discarded. And
 * the scan is lowercase-only, which is what makes `CALC(1px+2px)` a non-call.
 * All three raised by Hicks.
 */
const NAME_CHAR = /[0-9a-z]/;
const DIMENSION_CHAR = /[%a-zA-Z]/;

/**
 * The characters Tailwind's value parser treats as separators: `:` `,` `=` `>`
 * `<` newline space tab. `+`, `-` and `*` are deliberately absent, which is why
 * `calc(1px+var(--a_b))` parses as a function *named* `1px+var` rather than as
 * `var`, and so loses `var`'s underscore exemption. That is measurable: the
 * same `var()` written as `calc(var(--a_b)+1px)` keeps its underscore.
 */
const VALUE_SEPARATORS = new Set([58, 44, 61, 62, 60, 10, 32, 9]);

/**
 * Tailwind's underscore decode for one token. `\_` is a literal underscore; a
 * bare `_` becomes a space unless the caller is exempting this token.
 */
function decodeToken(text, exempt) {
  let out = "";
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (ch === "\\" && text[i + 1] === "_") {
      out += "_";
      i += 1;
    } else if (ch === "_" && !exempt) {
      out += " ";
    } else {
      out += ch;
    }
  }
  return out;
}

/** Parse a value the way Tailwind does, into words, separators and calls. */
function parseValueNodes(input) {
  const text = input.replaceAll("\r\n", "\n");
  const root = [];
  const stack = [];
  let buffer = "";
  const parent = () =>
    stack.length > 0 ? stack[stack.length - 1].nodes : root;
  const flush = () => {
    if (buffer.length > 0) {
      parent().push({ kind: "word", value: buffer });
      buffer = "";
    }
  };
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    if (code === 92) {
      // An escape carries its next character with it, so `\_` reaches the
      // decoder intact rather than being read as a space escape.
      buffer += text[i] + text[i + 1];
      i += 1;
    } else if (code === 47) {
      flush();
      parent().push({ kind: "separator", value: text[i] });
    } else if (VALUE_SEPARATORS.has(code)) {
      flush();
      let end = i + 1;
      while (end < text.length && VALUE_SEPARATORS.has(text.charCodeAt(end)))
        end += 1;
      parent().push({ kind: "separator", value: text.slice(i, end) });
      i = end - 1;
    } else if (code === 39 || code === 34) {
      const start = i;
      for (let scan = i + 1; scan < text.length; scan++) {
        const inner = text.charCodeAt(scan);
        if (inner === 92) {
          scan += 1;
          continue;
        }
        if (inner === code) {
          i = scan;
          break;
        }
      }
      buffer += text.slice(start, i + 1);
    } else if (code === 40) {
      const node = { kind: "function", value: buffer, nodes: [] };
      buffer = "";
      parent().push(node);
      stack.push(node);
    } else if (code === 41) {
      const closing = stack.pop();
      if (buffer.length > 0) {
        if (closing) closing.nodes.push({ kind: "word", value: buffer });
        buffer = "";
      }
    } else {
      buffer += text[i];
    }
  }
  if (buffer.length > 0) root.push({ kind: "word", value: buffer });
  return root;
}

/**
 * Apply Tailwind's underscore rules to a parsed value, in place.
 *
 * Two call names are exempt, and they are exempt differently. A `url()` keeps
 * every underscore in its whole subtree, because a URL is opaque. A `var()` or
 * `theme()` keeps them only in its *first* node, and only if that node is a
 * word -- the custom property name -- while any fallback is decoded normally.
 * Both exemptions also match a suffix, so `my_url(` and `my_var(` inherit them.
 */
function decodeNodes(nodes) {
  for (const node of nodes) {
    if (node.kind !== "function") {
      node.value = decodeToken(node.value, false);
      continue;
    }
    if (node.value === "url" || node.value.endsWith("_url")) {
      node.value = decodeToken(node.value, false);
      continue;
    }
    if (
      node.value === "var" ||
      node.value.endsWith("_var") ||
      node.value === "theme" ||
      node.value.endsWith("_theme")
    ) {
      node.value = decodeToken(node.value, false);
      for (let i = 0; i < node.nodes.length; i++) {
        if (i === 0 && node.nodes[i].kind === "word") {
          node.nodes[i].value = decodeToken(node.nodes[i].value, true);
          continue;
        }
        decodeNodes([node.nodes[i]]);
      }
      continue;
    }
    node.value = decodeToken(node.value, false);
    decodeNodes(node.nodes);
  }
}

function stringifyValueNodes(nodes) {
  let out = "";
  for (const node of nodes) {
    out +=
      node.kind === "function"
        ? `${node.value}(${stringifyValueNodes(node.nodes)})`
        : node.value;
  }
  return out;
}

/**
 * Expand Tailwind's `_` space escapes.
 *
 * This is not a text substitution, which is what it was until Hicks measured
 * `calc(var(--foo_bar)+1px)` and `url(foo_bar)` and found both wrong: which
 * underscores survive depends on the *parse*, so the parse is reproduced. A
 * value with no call at all skips the repair entirely, matching the emitter's
 * own early return.
 */
function decodeUnderscores(value) {
  if (!value.includes("(")) return decodeToken(value, false);
  const nodes = parseValueNodes(value);
  decodeNodes(nodes);
  return stringifyValueNodes(nodes);
}

/**
 * Reproduce the value Tailwind emits for an arbitrary class.
 *
 * This is a transcription of the emitter's behaviour, arrived at by measurement
 * and then checked against the shipped implementation, because three successive
 * rounds of deriving it by hand produced a model that agreed with every case
 * anyone had thought to measure and was still wrong in general. The last of those
 * -- "an operator is spaced unless an operator, `(` or `,` precedes it" -- got
 * `calc((e+pi)*1px)` wrong: Tailwind spaces the `*` and leaves the `+` glued, so
 * the browser rejects the declaration, and the rule was excusing a 0-height
 * element as a circle. Raised by Hicks.
 *
 * The distinction the hand-written model kept missing is that spacing depends on
 * *dimensions*, not on operands: `1px` and `50%` are numeric tokens, `e` and `pi`
 * are bare identifiers, and a sign between two identifiers is left alone.
 */
function repairedValue(value) {
  // `_` is Tailwind's space escape and is expanded before any repair happens, so
  // the repair sees a real space. Which underscores survive is a property of the
  // parsed value, not of the text -- see `decodeUnderscores`.
  const source = decodeUnderscores(value);
  let sawFunction = false;
  for (const name of REPAIRABLE_MATH) {
    if (source.includes(name)) {
      sawFunction = true;
      break;
    }
  }
  if (!sawFunction) return source;

  const repairable = [];
  let out = "";
  // Where the numeric token being read ends, and where the last completed one
  // ended. `1px` counts, `e` does not, and that difference is the whole of why
  // `calc(1px+2px)` is spaced and `calc((e+pi)*1px)` is not.
  let dimensionEnd = null;
  let lastDimensionEnd = null;

  for (let i = 0; i < source.length; i += 1) {
    const ch = source[i];
    if (DIGIT.test(ch) || (dimensionEnd !== null && DIMENSION_CHAR.test(ch))) {
      dimensionEnd = i;
    } else {
      lastDimensionEnd = dimensionEnd;
      dimensionEnd = null;
    }

    if (ch === "(") {
      out += ch;
      let start = i;
      for (let p = i - 1; p >= 0; p -= 1) {
        if (!NAME_CHAR.test(source[p])) break;
        start = p;
      }
      const name = source.slice(start, i);
      // A bare grouping paren is transparent, but only downwards: it inherits
      // repairability from whatever encloses it rather than having any of its own.
      // `calc((16px+16px))` is repaired and computes to 32px, while `abs((1px+2px))`
      // is emitted verbatim and dropped. Raised independently by Vasquez and Bishop.
      repairable.push(
        REPAIRABLE_MATH.has(name) ||
          (name === "" && repairable[repairable.length - 1] === true),
      );
      continue;
    }
    if (ch === ")") {
      repairable.pop();
      out += ch;
      continue;
    }

    const inside = repairable[repairable.length - 1] === true;
    if (ch === "," && inside) {
      // Every comma inside a repairable call is followed by a space: `min(1px,+2px)`
      // is emitted as `min(1px, +2px)`. No verdict turns on this, since comma
      // spacing cannot change whether a value parses, but this function is
      // documented as what the browser receives and is read as such.
      out += ", ";
      continue;
    }
    if (ch === " " && inside && out.endsWith(" ")) continue;

    if ("+-*/".includes(ch) && inside) {
      const trimmed = out.trimEnd();
      const prev = trimmed[trimmed.length - 1] ?? "";
      const beforePrev = trimmed[trimmed.length - 2] ?? "";
      const next = source[i + 1] ?? "";
      // A sign inside scientific notation belongs to the exponent, not to the
      // expression: `calc(1e+2px+1px)` is emitted as `calc(1e+2px + 1px)` and
      // computes to 101px. The digit before the `e` is required, because CSS also
      // has `e` as a bare constant and there the sign is a real operator.
      if ((prev === "e" || prev === "E") && DIGIT.test(beforePrev)) out += ch;
      // Only the first operator of a run is spaced, and a sign straight after `(`
      // or `,` is unary: `calc(1px++2px)` becomes `calc(1px + +2px)`, `calc(+1px)`
      // and `min(1px,+2px)` are left alone.
      else if ("+-*/".includes(prev) || prev === "(" || prev === ",") out += ch;
      // A space already written before the operator is not doubled.
      else if (source[i - 1] === " ") out += `${ch} `;
      else if (
        DIGIT.test(prev) ||
        DIGIT.test(next) ||
        prev === ")" ||
        next === "(" ||
        "+-*/".includes(next) ||
        (lastDimensionEnd !== null && lastDimensionEnd === i - 1)
      ) {
        out += ` ${ch} `;
      } else {
        // Two identifiers with a sign between them, as in `(e+pi)`: emitted glued,
        // and rejected by the browser.
        out += ch;
      }
      continue;
    }
    out += ch;
  }
  return out;
}

/**
 * Tailwind closes an unbalanced arbitrary value, but not predictably: measured,
 * `calc((1px+2px)` is balanced into a valid `calc((1px + 2px))` that computes to
 * 3px, while `calc(1px+2px` is emitted as `calc()1px+2px` and dropped. Those two
 * differ only in an inner paren and point opposite ways, so no reading of the
 * class can be trusted. The element is skipped rather than guessed at -- the same
 * fail-safe direction as an unterminated comment, and for the same reason: a
 * wrong guess here would report a real circle. Raised by Hicks.
 *
 * A paren inside a CSS string is a character, not structure, so quoting is
 * tracked: `before:content-['(']` is perfectly valid and must not silence the
 * lozenge beside it. A blind scan skipped those elements entirely. Also raised by
 * Hicks.
 */
function hasUnbalancedParens(value) {
  let depth = 0;
  let quote = null;
  let escaped = false;
  for (const ch of value) {
    if (escaped) {
      escaped = false;
      continue;
    }
    if (ch === "\\") {
      escaped = true;
      continue;
    }
    if (quote) {
      if (ch === quote) quote = null;
      continue;
    }
    if (ch === '"' || ch === "'") {
      quote = ch;
      continue;
    }
    if (ch === "(") depth += 1;
    else if (ch === ")") depth -= 1;
    if (depth < 0) return true;
  }
  return depth !== 0;
}

/**
 * Does this class open a CSS comment that escapes it?
 *
 * The candidate is repaired first, because Tailwind's operator spacing decides
 * whether a `/*` is a comment at all. What is scanned is therefore the text the
 * browser will actually see, not the text the author typed.
 *
 * No attempt is made to check that the class is a utility Tailwind recognises,
 * and that is deliberate. `unknown-[1px/*]` compiles to nothing, so its comment
 * cannot escape and the element could safely be judged; suppressing it anyway is
 * a bounded over-*excuse*, and this function can only ever suppress, never
 * accuse. Filtering by utility name would need Tailwind's whole namespace,
 * including anything a plugin contributes. That list is a second source of truth
 * and it drifts in the dangerous direction: the day it misses a real utility,
 * this rule ignores a comment that genuinely escapes, the radius is swallowed in
 * the browser but still visible to the linter, and a flat-cornered element is
 * reported as a bubble. Raised by Hicks, and left as-is on Vasquez's and Bishop's
 * concurrence.
 */
function hasEscapingComment(rawCandidate) {
  const candidate = repairedValue(rawCandidate);
  let quote = null;
  for (let i = 0; i < candidate.length; i += 1) {
    const ch = candidate[i];
    // A backslash escapes the next character wherever it appears, not only inside
    // a string. `\"` is a literal quote character and opens nothing, so a `/*`
    // after it starts a real comment: Chromium parses `width: \"/*;` and swallows
    // the following rule outright. Treating the escaped quote as a string opener
    // hid that comment and let the rule judge classes the browser never sees.
    // Raised by Vasquez, with the emitted CSS and a swallowed-rule measurement.
    if (ch === "\\") {
      i += 1;
      continue;
    }
    if (quote) {
      if (ch === quote) quote = null;
      continue;
    }
    if (ch === '"' || ch === "'") {
      quote = ch;
      continue;
    }
    if (ch === "/" && candidate[i + 1] === "*") {
      const close = candidate.indexOf("*/", i + 2);
      if (close === -1) return true;
      i = close + 1;
    }
  }
  return false;
}

function isSimpleLength(text) {
  const match = SIMPLE_LENGTH.exec(text);
  if (!match) return false;
  if (match[1] === undefined) return Number.parseFloat(text) === 0;
  return match[1] === "%" || COMPARABLE_UNIT.test(match[1]);
}

/**
 * Tailwind's arbitrary-value data-type hints, which prefix the value rather than
 * forming part of it: `w-[length:1rem]` is a 1rem width. Tailwind matches these
 * case-sensitively, so `w-[LENGTH:1rem]` is not a hint at all -- it emits
 * `width:LENGTH:1rem`, which CSS drops. Raised by Hicks.
 */
const DATA_TYPE_HINT = /^(?:length|number|percentage|size|integer):/;

/**
 * Valid but indefinite: CSS keeps the declaration, and it overrides anything
 * earlier, but it pins no length the rule can compare.
 */
/**
 * A variant that adds no specificity because it is only a `:where()` wrapper.
 * `:where()` is defined to contribute zero, so `[:where(&:hover)]:w-8` loses to a
 * bare `w-4` however deeply it nests. Raised by Hicks.
 */
const ZERO_SPECIFICITY_VARIANT = /^\[?\s*:where\([^]*\)\s*\]?$/i;

/**
 * Variants Tailwind emits as an at-rule rather than as part of the selector, and
 * which therefore add no specificity. Container queries and arbitrary
 * `min-[..]`/`max-[..]`/`supports-[..]` forms are included, and so is `dark`:
 * this project declares no `@custom-variant dark` and sets no `darkMode`, so
 * Tailwind's default applies and `dark:w-8` compiles to
 * `@media (prefers-color-scheme: dark)`. It was previously excluded on the
 * stated grounds that the variant is class-based here, which the compiled output
 * contradicts; counting it as a full class let it outrank a `hover:` that beats
 * it in CSS and excused a real 16x32 lozenge. Raised by Hicks.
 */
const AT_RULE_VARIANT =
  /^(?:sm|md|lg|xl|2xl|dark|print|portrait|landscape|motion-safe|motion-reduce|contrast-more|contrast-less|forced-colors|inverted-colors|noscript|(?:any-)?pointer-(?:none|coarse|fine)|starting|(?:min|max)-(?:sm|md|lg|xl|2xl)|(?:min|max|supports)-\[[^\]]*\]|@.*|\[\s*@[^]*\])$/i;

/**
 * At-rule variants Tailwind cannot negate. Every entry above emits `@media not
 * ...` or `@supports not ...` under `not-`, except `@starting-style`, which has
 * no negated form -- so `not-starting:` compiles to *nothing at all*. Weighing
 * it zero, as if it were `not-print:`, let a dead class win a comparison and
 * report a radius that never ships. Raised by Hicks, and settled by compiling
 * each `not-*` form against the app's own Tailwind rather than by reasoning
 * about what ought to be negatable.
 */
const NON_NEGATABLE_AT_RULE = /^starting$/i;

/**
 * The at-rules Tailwind can negate in the *arbitrary* spelling `not-[@…]`. Only
 * these three emit anything: `not-[@media(…)]`, `not-[@supports(…)]` and
 * `not-[@container(…)]` become `@media not (…)` and so on, while
 * `not-[@starting-style]`, `not-[@layer]`, `not-[@scope]`, `not-[@page]`,
 * `not-[@property]` and `not-[@keyframes]` compile to nothing at all. This is a
 * whitelist rather than a blacklist on purpose: an at-rule not named here is
 * treated as inert and can only ever excuse, whereas guessing that an unknown
 * at-rule negates cleanly would invent a weight and could report a radius that
 * never ships. Raised by Hicks; settled by compiling each form.
 */
const NEGATABLE_AT_RULE = /^(?:media|supports|container)$/i;

const INDEFINITE_KEYWORD =
  /^(?:auto|initial|inherit|unset|revert|revert-layer)$/i;

/**
 * Sized-to-content keywords CSS accepts on `width`/`height`. They are valid, so
 * they override, but none of them is a length -- `min-content` may or may not be
 * 32px, and treating it as definite let it overrule an `aspect-ratio` that in
 * fact still held. Note what is absent: `content` is a `flex-basis` value, `none`
 * belongs to `max-width`/`max-height`, and `available`/`fill-available` are not
 * Chromium spellings. Those are dropped rather than kept. Raised by Hicks.
 */
const SIZING_KEYWORD = /^(?:min-content|max-content|fit-content|stretch)$/i;

/**
 * The recorded reading for a `w-`/`h-`/`size-` value: the value itself when it
 * pins the axis, `INDEFINITE` when CSS keeps it but it pins nothing, `DROPPED`
 * when CSS discards it and whatever came before still stands.
 */
function dimensionReading(value, axis) {
  const arbitrary = /^\[(.+)\]$/.exec(value);
  // The comment strip has to happen here, at the read, and not only in the
  // comparison functions further down. `identicalTwinProves` strips, so
  // `w-[1px/**\/] h-[1px/**\/]` was excused and looked fixed; the *mixed*
  // spelling `w-[16px/**\/] h-[16px]` never reaches a twin comparison because
  // `isPlausibleLength` drops it first, so a real 16x16 circle was still
  // reported. Raised by Bishop, who also caught the rationale above overclaiming
  // a universal strip that did not exist.
  //
  // Leading whitespace left by a comment is deliberately kept, and only the tail
  // is trimmed. That distinction is load-bearing, and the case that proves it is
  // `w-[32px] h-[/**\/length:1rem] aspect-square rounded-full`: the malformed
  // height drops, the `aspect-square` beside it governs, and the element really
  // is a circle. Trimming the front too would read `length:1rem` as a valid
  // height, contradict the ratio, and report a circle as a lozenge. Built by
  // Hicks after Vasquez and I had each failed to find a discriminator and I had
  // written this comment off as unfalsifiable -- twice. Bishop found a second
  // discriminator pointing the other way, `size-[/**\/-2px]`, which looked like a
  // reason to trim both ends; it was really a missing `\s*` in the negative-drop
  // gate below, and fixing it there keeps both cases true at once.
  // Repair runs before the comment is stripped, because it is repair that decides
  // whether a comment is there at all -- see `provablyInvalidValue`.
  const text = stripCssComments(
    repairedValue((arbitrary ? arbitrary[1] : value).trim()),
  ).replace(/\s+$/, "");
  if (INDEFINITE_KEYWORD.test(text)) return INDEFINITE;
  if (axis === "height" && isPercentageHeight(value)) return INDEFINITE;
  if (!arbitrary) return value;
  if (SIZING_KEYWORD.test(text)) return INDEFINITE;
  // `var()` is opaque rather than dropped: CSS applies the declaration and
  // discards what it overrode, even though its value cannot be read here.
  if (/\bvar\(/i.test(text)) return OPAQUE_LIVE;
  // A bare negative length is invalid on `width`/`height`, so CSS drops the
  // declaration and the earlier one stands. A negative *computed* one does not
  // behave that way: `calc(1px - 2px)` parses, applies, and clamps to zero, so it
  // really does displace the base. Raised by Hicks, with browser evidence, and
  // against Vasquez's reading that CSS drops both -- `CSS.supports` accepts the
  // calc form, which settles it. Only the bare form is dropped here.
  //
  // The leading `\s*` is load-bearing and must not be removed: a comment stripped
  // out of `[/**\/-2px]` leaves a space in its place, and CSS treats the two
  // spellings identically (both drop). Anchoring hard at `^` let the commented
  // form slip past as a live length, so the rule disagreed with itself about the
  // same declaration. Raised by Bishop; pinned immediately below.
  //
  // Negative *zero* is not negative: CSS accepts `-0px` and computes it to the
  // same 0 as `0`, so the test is numeric rather than textual. Reading the bare
  // minus sign condemned a valid square. Raised by Hicks.
  const negative = /^\s*-\s*(\d*\.?\d+)/.exec(text);
  if (negative && parseFloat(negative[1]) !== 0) return DROPPED;
  if (!isPlausibleLength(text)) return DROPPED;
  const inner = text.replace(DATA_TYPE_HINT, "").trim();
  // A simple token has already been adjudicated, and must not reach the
  // arithmetic below: bare `0` is a valid length for `width`, while `calc(0)` is
  // a number CSS drops. Unitless zero means different things in and out of a
  // calculation, so only values that actually contain arithmetic are analysed.
  if (isSimpleLength(inner)) return value;
  // Carrying a length token is not the same as *being* a length. Raised by
  // Hicks: `calc(1px/1px)` is a number, so CSS drops it exactly as it drops
  // `calc(2)` — and an opaque pair that CSS never applies must not be allowed to
  // prove a square. Unit degree is only consulted when it can be computed, so an
  // expression the arithmetic cannot follow still falls through to `OPAQUE`.
  const degree = lengthDegree(inner);
  if (degree !== null && degree !== 1) return DROPPED;
  return degree === 1 ? OPAQUE_LIVE : OPAQUE;
}

/**
 * The unit degree of an arbitrary value: 1 for a length, 0 for a plain number,
 * `null` when the arithmetic cannot be followed.
 *
 * CSS multiplication adds degrees and division subtracts them, so `calc(1px*2)`
 * is a length and `calc(1px/1px)` is not. Only a confident answer is returned:
 * mistaking a real length for a number would cost a proof and report a genuine
 * circle, which is the one direction this rule refuses to move in.
 */
function lengthDegree(rawText) {
  const text = collapseNumericFunctions(stripCssComments(rawText));
  const term = (raw) => {
    const parts = [];
    const ops = [];
    let depth = 0;
    let current = "";
    for (const ch of raw) {
      if (ch === "(") depth += 1;
      else if (ch === ")") depth = Math.max(0, depth - 1);
      if (depth === 0 && (ch === "*" || ch === "/")) {
        parts.push(current);
        ops.push(ch);
        current = "";
        continue;
      }
      current += ch;
    }
    parts.push(current);
    let total = factor(parts[0]);
    if (total === null) return null;
    for (const [index, op] of ops.entries()) {
      const next = factor(parts[index + 1]);
      if (next === null) return null;
      total += op === "*" ? next : -next;
    }
    return total;
  };

  const factor = (raw) => {
    const trimmed = raw.trim().replace(/^[_\s]+|[_\s]+$/g, "");
    const wrapped =
      /^calc\(([^]*)\)$/i.exec(trimmed) ?? /^\(([^]*)\)$/.exec(trimmed);
    if (wrapped) return lengthDegree(wrapped[1]);
    // `min()`, `max()` and `clamp()` are ordinary maths, not opaque noise, and
    // treating them as unreadable let a valid `min(32px,64px)` leave a stale base
    // width standing. Raised by Hicks. Arity is part of validity: `clamp()` takes
    // exactly three arguments, and CSS drops it outright otherwise, so a wrong
    // count stays unreadable rather than becoming a length.
    const ranged = /^(min|max|clamp)\(([^]*)\)$/i.exec(trimmed);
    if (ranged) {
      const args = splitTopLevel(ranged[2], ",");
      const arity =
        ranged[1].toLowerCase() === "clamp"
          ? args.length === 3
          : args.length >= 1;
      if (!arity) return null;
      const degrees = args.map((arg) => lengthDegree(arg));
      if (degrees.some((d) => d === null)) return null;
      return degrees.every((d) => d === degrees[0]) ? degrees[0] : NaN;
    }
    // `abs()`, `round()`, `mod()`, `rem()` and `hypot()` preserve their operands'
    // dimension, so they are arithmetic the rule can follow exactly as it follows
    // `min()`. Leaving them unreadable was not merely conservative: an opaque
    // value whose arithmetic cannot be followed does not withdraw its axis, so a
    // stale base width survived under `hover:w-[abs(32px)]` and a real circle was
    // reported -- raised by Hicks. Arity is part of validity here too.
    const dimensional = /^(abs|round|mod|rem|hypot)\(([^]*)\)$/i.exec(trimmed);
    if (dimensional) {
      const args = splitTopLevel(dimensional[2], ",");
      const name = dimensional[1].toLowerCase();
      // `round()` takes an optional rounding strategy as its first argument. It
      // is a keyword, not an operand, so it is removed before the degrees are
      // compared and before the arity is counted -- raised by Hicks, whose
      // `round(up,32px,1px)` was read as a three-operand call with a
      // dimensionless `up` and refused, reporting a real circle.
      if (
        name === "round" &&
        args.length > 0 &&
        ROUNDING_STRATEGY.test(args[0].trim())
      ) {
        args.shift();
      }
      const arity =
        name === "abs"
          ? args.length === 1
          : // `round()`'s interval argument is optional and defaults to 1, so
            // `round(2)` is an ordinary rounded number rather than an unreadable
            // call. Raised by Vasquez.
            name === "round"
            ? args.length === 1 || args.length === 2
            : name === "hypot"
              ? args.length >= 1
              : args.length === 2;
      if (!arity) return null;
      const degrees = args.map((arg) => lengthDegree(arg));
      if (degrees.some((d) => d === null)) return null;
      return degrees.every((d) => d === degrees[0]) ? degrees[0] : NaN;
    }
    // `[+-]?`, not `-?`. A leading `+` is identity on a CSS number and the
    // browser keeps it, so `+2rem` is a real length. Bishop prescribed widening
    // this in round 26 and I declined, because with the sign guard in
    // `provablyInvalidValue` applying to *every* value no pin could tell the
    // widening from its absence -- he built the mutant himself and measured no
    // difference across fourteen probes. Narrowing that guard to `calc()`, which
    // is where Tailwind's repair actually happens, made this line load-bearing:
    // `size-[+2rem] rounded-full` has no twin for `scalarLength` to intercept,
    // so the value arrives here, and with `-?` it is condemned as invalid and a
    // genuine square is reported. Bishop was right; the case that proves it is
    // the one with only one dimension, which is why every twin probe missed it.
    const scalar = /^([+-]?(?:\d+\.?\d*|\.\d+)(?:e[+-]?\d+)?)([a-z%]*)$/i.exec(
      trimmed,
    );
    if (!scalar) {
      // A math constant is a `<number>` of fixed value, so it multiplies and
      // divides like any other scalar and contributes no unit degree.
      return MATH_CONSTANT.test(trimmed) ? 0 : null;
    }
    if (scalar[2] === "") return 0;
    if (scalar[2] === "%") return 1;
    return COMPARABLE_UNIT.test(scalar[2]) ? 1 : null;
  };

  const body = /^calc\(([^]*)\)$/i.exec(text.trim());
  const expression = body ? body[1] : text;
  // CSS requires whitespace around `+` and `-`, which Tailwind writes as `_`.
  // Every addend of a valid sum shares one degree -- and taking the first
  // readable one on trust meant `calc(1px + 1)`, which CSS rejects outright, was
  // read as a length and allowed to displace the declaration below it. Raised by
  // Hicks. A disagreement is reported as `NaN`, which the caller drops, and an
  // unreadable addend still yields `null` and the cautious reading.
  let agreed = null;
  for (const addend of expression.split(/(?:_|\s)[+-](?:_|\s)/)) {
    const degree = term(addend);
    if (degree === null) return null;
    if (Number.isNaN(degree)) return NaN;
    if (agreed === null) agreed = degree;
    else if (agreed !== degree) return NaN;
  }
  return agreed;
}

/**
 * Whether an arbitrary value is a length CSS keeps. Keywords are handled by the
 * caller, so anything reaching here has to carry a real dimension: a value built
 * only from scalars is not one, and `w-[calc(2)]` is dropped by CSS however
 * well-formed it looks. Raised by Hicks. Rejecting a length CSS does keep costs a
 * proof and errs toward excusing, so the check stays strict on units and
 * permissive on structure it cannot tokenise.
 */
function isPlausibleLength(inner) {
  const text = collapseNumericFunctions(
    inner.replace(DATA_TYPE_HINT, "").trim(),
  );
  let depth = 0;
  let sawLength = false;
  for (const token of text.matchAll(ARBITRARY_TOKEN)) {
    const [text2, number, unit, fn, bare] = token;
    // Track nesting so the rules below can tell a top-level dimension from a
    // scalar inside a function. `calc(2*1rem)` is 32px: the bare `2` is a
    // multiplier, not a malformed length. Raised by Hicks.
    depth += (text2.match(/\(/g) || []).length;
    depth -= (text.slice(token.index).match(/^\)+/) || [""])[0].length;
    if (fn !== undefined) {
      if (!VALUE_FUNCTION.test(fn)) return false;
      continue;
    }
    if (bare !== undefined) {
      // `round()`'s optional strategy is a keyword, not a value, and CSS accepts
      // it, so refusing it made a well-formed length look invalid -- raised by
      // Hicks, whose `round(up,32px,1px)` reported a real circle. It is only
      // ever an argument, so it is accepted inside a function and nowhere else;
      // `w-[up]` stays refused. Which function it belongs to, and whether the
      // remaining arity is legal, is settled in `lengthDegree`. A math constant
      // is admitted on the same terms and for the same reason.
      if (!(
        depth > 0 &&
        (ROUNDING_STRATEGY.test(bare) || MATH_CONSTANT.test(bare))
      ))
        return false;
      continue;
    }
    if (unit === "") {
      // Unitless is a length only at zero; inside a function it is a scalar.
      if (depth > 0) continue;
      if (Number.parseFloat(number) !== 0) return false;
      sawLength = true;
      continue;
    }
    if (unit !== "%" && !COMPARABLE_UNIT.test(unit)) return false;
    sawLength = true;
  }
  return sawLength;
}

/**
 * True when a `w-`/`h-`/`size-` value resolves to the same absolute length on
 * either axis, and so proves squareness when it appears on both.
 *
 * The distinction this draws is between *definite* and *comparable*, which are
 * not the same thing and were conflated until Hicks pointed it out. `w-full` and
 * `h-full` are both perfectly definite, but they are 100% of the containing
 * block's *width* and *height* respectively, so they are equal only when the
 * parent happens to be square — which the rule cannot see. Matching them by
 * lexeme was proving circularity from a coincidence of spelling. The same holds
 * for fractions (`w-1/2 h-1/2`), content sizing (`fit`, `min`, `max`), and
 * `screen`, which expands to `100vw` on one axis and `100vh` on the other.
 *
 * What survives: the spacing scale and `px`, which are absolute; viewport-unit
 * keywords, where the *same* keyword on both axes really is the same length
 * (`w-dvh h-dvh`); and arbitrary values built only from axis-independent units.
 *
 * That last case generalises further than it first looks, because the caller only
 * consults this after the two value *strings* have already matched exactly. So
 * the axis-dependent units cannot slip through by unit alone: `w-[10cqw]` and
 * `h-[10cqh]` are different strings and never match, while `w-[10cqw] h-[10cqw]`
 * is 10% of the container's width on both axes and genuinely is a circle. The
 * same holds for `lh`, and for `min()`/`max()`/`clamp()`/`calc()` over comparable
 * operands, along with `abs()`, `round()`, `mod()`, `rem()` and `hypot()`, which
 * preserve their operands' dimension and do not care which axis they land on.
 * What must still be rejected is anything whose length depends on
 * *which property it is applied to* even when spelled identically -- a bare `%`,
 * which resolves against the containing block's width for `width` and its height
 * for `height` -- along with unrecognised functions, non-length units and
 * non-zero unitless numbers, none of which are lengths at all.
 *
 * `var()` does not reach here: a value containing one is indefinite, so it is
 * never recorded on an axis in the first place.
 *
 * The `%` rejection is done by the single token walk below rather than by a
 * special-cased early return. An earlier draft had an explicit guard; every
 * mutation of it survived, because the walk already refused `%` as an
 * unrecognised unit. Defensive code that no test can distinguish from its
 * absence makes the guard suite look stronger than it is, so it is gone and the
 * walk is the only mechanism.
 */
const COMPARABLE_UNIT =
  /^(?:px|rem|em|ch|rch|ex|rex|cap|rcap|ic|ric|lh|rlh|pt|pc|in|cm|mm|q|vh|vw|vi|vb|vmin|vmax|svh|svw|svi|svb|svmin|svmax|lvh|lvw|lvi|lvb|lvmin|lvmax|dvh|dvw|dvi|dvb|dvmin|dvmax|cqw|cqh|cqi|cqb|cqmin|cqmax)$/i;
const COMPARABLE_FUNCTION = /^(?:min|max|clamp|calc|abs|round|mod|rem|hypot)$/i;

/**
 * `round()`'s optional first argument is a rounding strategy keyword rather than
 * an operand, so it is neither a length nor subject to the degree agreement the
 * real arguments are held to.
 */
const ROUNDING_STRATEGY = /^(?:nearest|up|down|to-zero)$/i;
/**
 * CSS's math constants. They are `<number>`s with fixed values, so they are
 * scalars wherever they appear and cannot make a value axis-dependent -- unlike
 * `var()`, `env()` and `attr()`, whose contents are unknown and may be a
 * percentage. Raised by Vasquez, whose `calc(1px*pi)` was refused for carrying a
 * bare identifier and reported a real circle. They are only meaningful inside a
 * math function, so they are accepted there and nowhere else.
 */
const MATH_CONSTANT = /^(?:-?infinity|pi|e|nan)$/i;
/**
 * Every numeric, identifier and function token in an arbitrary value. `%` is
 * captured as a unit rather than skipped so that it reaches the whitelist and is
 * refused there like any other axis-dependent unit.
 *
 * That capture is a modelling choice rather than a load-bearing guard: letting
 * `%` fall through as a unitless number rejects every percentage the other
 * branch rejects, via the zero check below, so mutating it survives
 * falsification. The two spellings part company only on percentages that are
 * numerically zero -- `[0%]`, or Hicks's `[0.000...1%]` with enough leading
 * zeros to underflow `parseFloat` -- and on those the answers are equally
 * defensible, since zero width and zero height really are equal. It is written
 * this way because `%` *is* a unit and the code should say so, not because a
 * test distinguishes it.
 *
 * The number branch accepts scientific notation because CSS does: `[1e2px]` is
 * a valid 100px length, and tokenising it as `1` followed by a bare `e2px` had
 * it rejected as an unrecognised unit.
 *
 * The bare-identifier branch requires a leading *letter* so that the subtraction
 * in `calc(32px - 1px)` is not read as an identifier named `-`. It was, and the
 * value was rejected as incomparable, so two identical 31px axes stopped proving
 * a square and a real circle was reported -- while the `+` spelling, which the
 * pattern never matched at all, worked. Raised by Hicks. Skipping a stray `-` is
 * the same treatment `+`, `*` and `/` already get.
 */
const ARBITRARY_TOKEN =
  /(-?\d*\.?\d+(?:e[+-]?\d+)?)([a-z%]*)|([a-z-]+)\s*\(|([a-z][a-z-]*)/gi;

function isComparableArbitrary(raw) {
  const inner = collapseNumericFunctions(raw);
  let sawLength = false;
  let depth = 0;
  for (const token of inner.matchAll(ARBITRARY_TOKEN)) {
    const [text, , numericUnit, fn, bare] = token;
    depth += (text.match(/\(/g) || []).length;
    depth -= (inner.slice(token.index).match(/^\)+/) || [""])[0].length;
    if (numericUnit !== undefined) {
      // A unitless number is only a valid length when it is zero — but inside a
      // function it is a scalar, and `calc(2*1rem)` is a perfectly comparable
      // 32px. Raised by Hicks.
      if (numericUnit === "") {
        if (depth > 0) continue;
        if (Number.parseFloat(token[1]) !== 0) return false;
        sawLength = true;
        continue;
      }
      if (!COMPARABLE_UNIT.test(numericUnit)) return false;
      sawLength = true;
      continue;
    }
    if (fn !== undefined) {
      if (!COMPARABLE_FUNCTION.test(fn)) return false;
      continue;
    }
    if (bare !== undefined) {
      // The same keyword allowance as in `isPlausibleLength`: a strategy does
      // not make two identically spelled lengths differ, and nor does a
      // constant of fixed value.
      if (!(
        depth > 0 &&
        (ROUNDING_STRATEGY.test(bare) || MATH_CONSTANT.test(bare))
      ))
        return false;
      continue;
    }
  }
  // A math function whose operands are all unitless resolves to a `<number>`,
  // which is invalid for `width`/`height`, so CSS drops the declaration: no
  // amount of well-formedness makes `calc(2*3)` a length. Requiring a
  // length-bearing token somewhere is what separates it from `calc(2*1rem)`.
  // Raised independently by Bishop and Hicks.
  //
  // This check and the one closing `isPlausibleLength` are *mutually redundant*,
  // and deliberately kept so. Mutation testing shows each survives alone and the
  // pair is killed together, which is not a gap in the spec: a value carrying no
  // length-bearing token is dropped before it can be recorded, so it can never
  // reach a comparison, and one asking whether an axis is *definite* is not the
  // same question as whether two axes are *equal*. Neither guard may be deleted
  // on the evidence that the tests still pass without it.
  return sawLength;
}

/**
 * A percentage resolves against the containing block, and on the block axis that
 * containing block is very often indefinite: CSS 2.1 §10.5 says a percentage
 * `height` computes to `auto` when the parent's height depends on its content.
 * So `h-full` is only *conditionally* definite, and the rule cannot see the
 * parent to tell which. `w-full h-full aspect-square rounded-full` was therefore
 * reported as a lozenge when it lays out as a real square whenever the parent's
 * height is indefinite — a false report, the forbidden direction. Raised by
 * Bishop.
 *
 * The asymmetry is deliberate and is a fact about CSS, not a hedge: there is no
 * equivalent rule for widths, so a percentage `width` stays definite. Treating
 * both axes as indefinite would have excused `aspect-square w-full h-8`, which is
 * a genuine 100%-by-32px lozenge.
 */
const PERCENTAGE_VALUE = /^(?:full|\d+\/\d+)$/;

function isPercentageHeight(value) {
  const arbitrary = /^\[(.+)\]$/.exec(value);
  if (arbitrary) return arbitrary[1].includes("%");
  return PERCENTAGE_VALUE.test(value);
}

function isComparableDimension(value) {
  if (/^\d*\.?\d+$/.test(value)) return true;
  if (value === "px") return true;
  // Viewport keywords only. `screen` is deliberately absent: it is `100vw` on one
  // axis and `100vh` on the other, so identical spelling is not identical length.
  if (/^(?:svh|svw|lvh|lvw|dvh|dvw)$/.test(value)) return true;
  const arbitrary = /^\[(.+)\]$/.exec(value);
  if (arbitrary) {
    // Repaired first, like every other reader: `calc(1px*pow(1+1,2))` reaches the
    // browser as `calc(1px * pow(1 + 1, 2))` and computes to 4px, but read as
    // written its `pow` call will not collapse and the value looked incomparable,
    // so two identical axes stopped proving a square and a real circle was
    // reported.
    return isComparableArbitrary(
      stripCssComments(repairedValue(arbitrary[1])).replace(/^length:/, ""),
    );
  }
  return false;
}

/**
 * A single length reduced to a number and a unit, so equality can be decided by
 * value rather than by spelling. `w-[1e2px] h-[100px]` is a 100x100 circle that
 * was reported because the two strings differ — raised by Bishop. Units are
 * *not* converted between each other: `1rem` equals `16px` only if the root font
 * size is 16px, which is a user preference, so those stay unequal.
 */
function scalarLength(value) {
  const arbitrary = /^\[(.+)\]$/.exec(value);
  if (!arbitrary) return null;
  const text = stripCssComments(repairedValue(arbitrary[1]))
    .replace(DATA_TYPE_HINT, "")
    .trim();
  // A leading `+` is identity on a CSS number and the browser keeps it, so
  // `w-[+1px] h-[1px]` is a genuine 1x1 that was reported for differing in
  // spelling alone. Every numeric pattern in this file allowed a leading `-` and
  // none allowed a `+`; this is the only one of the four that any pin can tell
  // the presence of, because it is the one on the value-comparison path. Hicks
  // and Bishop both reported the identical-twin symptom; the mixed-spelling case
  // that isolates *this* regex survived both of their fixes.
  const single = /^([+-]?\d*\.?\d+(?:e[+-]?\d+)?)([a-z]*)$/i.exec(text);
  if (!single) return null;
  const number = Number.parseFloat(single[1]);
  if (!Number.isFinite(number)) return null;
  const unit = single[2].toLowerCase();
  if (unit !== "" && !COMPARABLE_UNIT.test(unit)) return null;
  if (unit === "" && number !== 0) return null;
  return `${number}${unit}`;
}

/** Whether two `w-`/`h-` values denote the same length, by value not by spelling. */
function sameLength(a, b) {
  if (a === b) return identicalTwinProves(a);
  const left = scalarLength(a);
  return left !== null && left === scalarLength(b);
}

/**
 * Whether two *identically spelled* values may stand as proof of a square.
 *
 * Spelling alone is not proof. `w-[calc(1px_2px)] h-[calc(1px_2px)]` juxtaposes
 * two lengths, which is invalid; the browser drops both declarations and the
 * pair underneath stands, so reading the twins as equal excused a real 16x32
 * lozenge -- raised by Hicks.
 *
 * Deferral is *not* the escape hatch it looks like. `w-[var(--x)] h-[var(--x)]`
 * is not provably square, because `--x` may hold a percentage, which resolves
 * against a different axis on each property -- so identical spelling still
 * proves nothing. Excusing unknowable twins was tried here and reverted: it
 * contradicted the pins that already record that reading, and being generous
 * about unrecognised names let `banana(1px)`, a parse error, prove a square.
 * Vasquez's case, and his percentage argument.
 */
function identicalTwinProves(value) {
  const arbitrary = /^\[(.+)\]$/.exec(value);
  if (!arbitrary) return isComparableDimension(value);
  const text = arbitrary[1]
    .replace(DATA_TYPE_HINT, "")
    .replace(/^length:/, "")
    .trim();
  return !provablyInvalidValue(text) && isComparableDimension(value);
}

/**
 * True only where the browser can be *shown* to drop the value, never merely
 * where this model cannot read it. An unreadable expression is condemned only
 * when it holds no function call at all, since there the whole grammar is
 * understood and the value still would not parse; `clamp(1rem,2vw,3rem)` must
 * stay innocent of a fault this model cannot see.
 *
 * Type disagreement -- `calc(1px_+_1)`, `sin(1deg*1px)` -- needs no clause of
 * its own: `isComparableDimension` refuses those before equality is ever
 * consulted. A `NaN` branch was tried here and removed after no pin could tell
 * it from its absence, which is the only evidence that a line is load-bearing.
 *
 * A scoped semicolon test was tried here too, and removed for the same reason.
 * The scoping is real -- a semicolon is fatal inside `calc()` but ordinary
 * inside `if()`, which uses it to separate branches -- but it is unobservable
 * through this rule's verdicts, because every value that would distinguish the
 * scoped test from the blunt one contains an `if()`, and `if()` is opaque here
 * (below), so such a value is refused for opacity before its semicolons are ever
 * reached. A stack-based version that tracked the enclosing function was written
 * and measured: it agreed with `includes(';')` on all 483 pins, and neither
 * removing its math scoping nor removing its `if()` exemption moved a single
 * verdict. Ninety lines that no test could see are worse than the one line they
 * replaced, so the one line stands, and the day `if()` becomes readable is the
 * day the scoping earns a pin.
 */
function provablyInvalidValue(text) {
  // Repair runs *before* comments are stripped, because repair is what decides
  // whether a comment exists at all: `calc(1px/**\/+2px)` is emitted as
  // `calc(1px / **\/+2px)`, which holds no comment and is dropped, whereas
  // stripping first invents the perfectly valid sum `calc(1px + 2px)` and excused
  // a real lozenge. Raised by Hicks.
  const clean = stripCssComments(repairedValue(text));
  // A semicolon inside a math function is a parse error, and the browser drops
  // the whole declaration: `size-[calc(1px+2px;3px)]` emits
  // `width: calc(1px + 2px;3px)` and the element measures 1264x0 -- unsized --
  // with the radius still applied. Raised by Hicks.
  //
  // The condemnation is confined to those functions rather than applied to the
  // value at large, because a semicolon is not universally fatal. `if()` uses it
  // to separate branches, so `size-[if(style(--x:yes):16px;else:16px)]` is valid
  // CSS that measures 16x16, and a blanket rule reported a genuine circle --
  // Hicks again, against the first version of this check. What matters is the
  // nearest enclosing function, not the presence of a semicolon anywhere, so an
  // `if()` nested inside a `calc()` keeps its own grammar.
  //
  // Judged after comments are stripped, so a semicolon safely inside one is not
  // condemned: `abs(1px/*x;y*/)` really is 1px, also raised by Hicks. A
  // semicolon inside a quoted string terminates nothing either, but both callers
  // of this function are asking about a length and no length holds a string, so
  // a quote-aware version was written and then deleted: no pin could tell it
  // from its absence, which is the only evidence that a line is load-bearing.
  if (clean.includes(";")) return true;
  if (lengthDegree(clean) !== null) return false;
  // Everything below is judged on the text the browser receives, after Tailwind's
  // operator repair, rather than on the class as written. Classifying by the
  // outermost function instead got nesting backwards in both directions:
  // `abs(calc(1px+2px))` is repaired on the inside and computes to 3px, while
  // `calc(abs(1px+2px))` is not repaired and drops. Raised by Hicks.
  const repaired = clean.trim();
  // Two multiplicative operators in a row leave the second without an operand.
  // This is what a comment degenerates into once repair has run over it.
  if (/[*/][ \t_]*[*/]/.test(repaired)) return true;
  // An empty group is not an operand: `calc(1px + ())` is dropped.
  if (/\(\s*\)/.test(repaired)) return true;
  // CSS Values requires whitespace on *both* sides of a binary `+` or `-`, and
  // after the repair above any sign still missing it is one the browser will see.
  // `_` counts as whitespace, being how Tailwind spells a space in an arbitrary
  // value -- and it must be kept out of the operand class as well as included in
  // the separator class, because `_` is a word character and `\w` therefore read
  // `1px_+_2px` as glued and condemned a value that computes to 3px.
  if (/[a-zA-Z0-9%)][+-]/.test(repaired)) return true;
  if (/[a-zA-Z0-9%)][ \t_]+[+-](?![ \t_])/.test(repaired)) return true;
  // Two operands with no operator between them is not an expression:
  // `calc(1px 2px)` and `calc(1/**\/0px)`, whose comment leaves exactly that
  // behind, are both dropped. A comma does not count -- `min(1px, 2px)` is an
  // argument list, not a juxtaposition.
  if (/[a-zA-Z0-9%)][ \t_]+[.\d]/.test(repaired)) return true;
  // Repair inserts whitespace but cannot invent an operand, so a sign left
  // dangling before `)`, `,` or end of value is dropped however it is spelt.
  if (/[+-][ \t_]*(?:[),]|$)/.test(repaired)) return true;
  // ...nor can it turn a lone `.` into a number: `calc(1px+.)` is spaced into
  // `calc(1px + .)` and dropped, while `calc(1px+.5px)` computes.
  if (/[+-][ \t_]*\.(?!\d)/.test(repaired)) return true;
  // Outside any function there is no repair at all: `w-[1px+]` is emitted
  // verbatim as `width: 1px+`, which the browser drops.
  return !/\(/.test(clean);
}

/**
 * The selector-scoping part of a condition: the variants that move the radius
 * off the host element onto a descendant or pseudo-element. Two conditions are
 * in the same scope when these match, whatever state variants they also carry.
 */
/**
 * Variants that render into a *separate box* rather than the element itself.
 */
const PSEUDO_ELEMENT_VARIANT =
  /^(?:before|after|placeholder|file|marker|selection|first-line|first-letter|backdrop|details-content)$/;

/** Tailwind's direct-child (`*:`) and any-descendant (`**:`) variants. */
const CHILD_VARIANT = /^\*{1,2}$/;

/**
 * Drop every parenthesised and bracketed payload, keeping the top level only.
 *
 * A combinator inside `:has(...)`, `:not(...)` or an attribute selector belongs
 * to that argument, not to the selector's own shape: `&:has(>img)` still targets
 * the element itself. Flattening first is what lets one combinator test serve
 * both `[&:has(>img)]` (on-host) and `[&:not(.x)_img]` (off-host).
 *
 * `:is()` and `:where()` are the exception, and only in one position. They are
 * selector *lists*, not predicates, so when one stands alone as its own compound
 * its argument is the subject: `[:is(&_section)]` emits `:is(& section)` and
 * targets the `<section>`, exactly as `[&_section]` does. Stripping the payload
 * hid that, and the host's dimensions were used to condemn a descendant. Raised
 * by Hicks. Attached to a compound they merely qualify it and the subject is
 * unchanged, so `[&:is(.a_.b)]` is still the host -- which is why transparency
 * requires the `:is` to be preceded by nothing, whitespace or a combinator.
 */
const TRANSPARENT_GROUP = /(?:^|[\s>+~,(])\s*:(?:is|where)$/i;

function stripPayloads(selector) {
  let out = "";
  let hidden = 0;
  const opened = [];
  for (let i = 0; i < selector.length; i += 1) {
    const ch = selector[i];
    if (ch === "(" || ch === "[") {
      const transparent =
        ch === "(" &&
        hidden === 0 &&
        TRANSPARENT_GROUP.test(selector.slice(0, i));
      opened.push(transparent);
      if (!transparent) hidden += 1;
      continue;
    }
    if (ch === ")" || ch === "]") {
      if (opened.pop() !== true) hidden = Math.max(0, hidden - 1);
      continue;
    }
    if (hidden === 0) out += ch;
  }
  return out;
}

/**
 * True when a variant segment moves the target off the element whose class
 * attribute this is.
 *
 * The test that matters is *not* whether the segment mentions `&`. Raised by
 * Hicks, and my first probe of it was too weak to see it: `[&:hover]` is the
 * element itself in a state, so the element's own `w-`/`h-` absolutely do bear
 * on it, while `*:` names the children and carries no `&` at all. Keying on `&`
 * got both backwards — it excused `w-4 h-8 aspect-square [&:hover]:rounded-full`,
 * a plain 16x32 lozenge, and condemned a 32x32 child in
 * `w-4 *:h-8 *:aspect-square *:rounded-full` using the host's width.
 *
 * What moves the target is a *combinator somewhere after* `&`, a child variant,
 * or a pseudo-element. Two refinements, both raised by Hicks and both checked
 * against what Tailwind actually emits rather than against what it looks like:
 *
 *   - The combinator need not be adjacent. `[&:hover_img]` emits `&:hover img`,
 *     which targets a descendant; requiring the combinator to be adjacent to
 *     the `&` missed it.
 *   - Only a *bare* `[…]` carries a raw selector. A named variant takes its
 *     bracket as an argument — `has-[&>img]` emits `&:has(*>img)` and
 *     `supports-[selector(::before)]` emits an `@supports` block — so in both
 *     the radius lands on the host and nothing inside the bracket can move it.
 *
 * `[.group:hover_&]` puts the combinator before the `&` and stays on-host, which
 * is what `group-hover:` means.
 */
function isOffHost(segment) {
  if (CHILD_VARIANT.test(segment)) return true;
  if (PSEUDO_ELEMENT_VARIANT.test(segment)) return true;

  const arbitrary = /^\[([^]*)\]$/.exec(segment);
  if (!arbitrary) return stripPayloads(segment).includes("::");

  const selector = stripPayloads(arbitrary[1]);
  if (selector.includes("::")) return true;
  const subject = selector.indexOf("&");
  if (subject === -1) return false;
  // `_` is Tailwind's escape for a descendant combinator's space.
  return /[>+~_\s]/.test(selector.slice(subject + 1));
}

function selectorScope(key) {
  if (key === "") return "";
  return key.split("\u0000").filter(isOffHost).join("\u0000");
}

function conditionKey(segments) {
  // Repeated variants are *not* collapsed. `hover:hover:` emits `&:hover:hover`,
  // which CSS counts twice, and deduplicating here dropped it to one class and
  // let a single `focus:` outrank it -- excusing a real 16x32 lozenge. Raised by
  // Hicks, confirmed by Vasquez. Two conditions that differ only in multiplicity
  // still select the same states, which `conditionApplies` decides by membership.
  return [...segments].sort().join("\u0000");
}

/** True when condition `key` holds in every state `target` selects, i.e. key ⊆ target. */
function conditionApplies(key, target) {
  if (key === "") return true;
  if (key === target) return true;
  const targetSet = new Set(target === "" ? [] : target.split("\u0000"));
  return key.split("\u0000").every((segment) => targetSet.has(segment));
}

/**
 * Collect every string literal and template quasi beneath a className value.
 *
 * Each entry carries the exact **source** text and the absolute index it starts
 * at. This matters: a template quasi's `cooked` and `raw` values both normalise
 * `\r\n` to `\n` per spec, so on a CRLF checkout every preceding line shifts
 * offsets computed from them one character to the left. That shipped
 * `border-pf-borderrounded-lgl` — a wrong class, not a syntax error, so nothing
 * failed. Slicing the source directly makes the fix range exact by construction.
 */
function collectStringNodes(node, sourceCode, found = []) {
  if (!node || typeof node !== "object") return found;

  if (node.type === "Literal" && typeof node.value === "string") {
    // A string literal cannot contain a raw newline, so its source is its own
    // text between the quotes.
    const raw = sourceCode.getText(node);
    found.push({
      node,
      text: raw.slice(1, -1),
      textStart: node.range[0] + 1,
      quasi: null,
    });
    return found;
  }
  if (node.type === "TemplateLiteral") {
    const source = sourceCode.getText();
    for (const quasi of node.quasis) {
      // range[0] sits on the opening delimiter (a backtick, or the `}` closing
      // the previous `${`); range[1] sits past the closing one, which is a
      // backtick for the tail and `${` for every other quasi.
      const start = quasi.range[0] + 1;
      const end = quasi.range[1] - (quasi.tail ? 1 : 2);
      found.push({
        node: quasi,
        text: source.slice(start, Math.max(start, end)),
        textStart: start,
        quasi,
      });
    }
    for (const expression of node.expressions)
      collectStringNodes(expression, sourceCode, found);
    return found;
  }

  for (const key of Object.keys(node)) {
    if (key === "parent" || key === "loc" || key === "range") continue;
    const child = node[key];
    if (Array.isArray(child)) {
      for (const entry of child) collectStringNodes(entry, sourceCode, found);
    } else if (
      child &&
      typeof child === "object" &&
      typeof child.type === "string"
    ) {
      collectStringNodes(child, sourceCode, found);
    }
  }
  return found;
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
    openingElement?.attributes?.some((attr) => {
      if (attr.type !== "JSXAttribute") return false;
      if (!WAIVER_ATTRIBUTES.has(attr.name?.name)) return false;

      const value = attr.value;
      // Bare `data-pf-progress-track` / `data-pf-progress-fill` are boolean-ish
      // markers and the only sensible reading of them is "yes". A bare
      // `data-pf-radius` asserts nothing — it does not say *which* radius is
      // intended — so it must not waive. Requiring the value keeps the waiver a
      // deliberate signature rather than an attribute someone left half-typed.
      if (value === null || value === undefined)
        return attr.name.name !== "data-pf-radius";
      if (value.type === "Literal")
        return value.value === "full" || value.value === true;
      if (value.type === "JSXExpressionContainer") {
        const inner = value.expression;
        if (inner?.type === "Literal")
          return inner.value === "full" || inner.value === true;
        // A computed waiver cannot be judged statically. Honour it rather than
        // reporting a line the author may have already reasoned about.
        return true;
      }
      return false;
    }),
  );
}

export const __repairedValueForTest = repairedValue;
export default {
  meta: {
    type: "problem",
    docs: {
      description:
        "Keep border radii within --pf-radius-lg (8px) on rectangular surfaces, per DESIGN-LANGUAGE.md",
      recommended: true,
      url: "file:///src/Web/ReactApp/src/design-system/DESIGN-LANGUAGE.md",
    },
    fixable: "code",
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
        type: "object",
        properties: {
          maxPx: { type: "number" },
          checkFullRound: { type: "boolean" },
        },
        additionalProperties: false,
      },
    ],
  },

  create(context) {
    const options = context.options[0] ?? {};
    const maxPx =
      typeof options.maxPx === "number" ? options.maxPx : MAX_RADIUS_PX;
    const checkFullRound = options.checkFullRound !== false;

    /**
     * Build the replacement token, preserving variants, side and the important
     * marker. The trailing `!` has to be matched explicitly: anchoring the size
     * at `$` made `rounded-xl!` unfixable, and because the untouched token was
     * still handed to `replaceTextRange` the rule emitted a *no-op* fix — which
     * is worse than none, since `--fix` then exits reporting an error it claims
     * to be able to fix. Returning the input unchanged is now caught below.
     */
    function toLargeToken(rawToken) {
      return rawToken.replace(
        /-(?:4xl|3xl|2xl|xl|full|\[[^\]]+\])(!?)$/,
        "-lg$1",
      );
    }

    function reportToken({
      textStart,
      stringNode,
      quasi,
      rawToken,
      offset,
      messageId,
      data,
      autofix,
    }) {
      const start = textStart + offset;
      const range = [start, start + rawToken.length];
      const replacement = toLargeToken(rawToken);

      const descriptor = {
        node: quasi ?? stringNode,
        loc: {
          start: context.sourceCode.getLocFromIndex(start),
          end: context.sourceCode.getLocFromIndex(range[1]),
        },
        messageId,
        data,
      };

      // A rewrite that changes nothing is not a fix. Report it bare rather than
      // offering an action that cannot act.
      if (replacement === rawToken) {
        context.report(descriptor);
        return;
      }

      if (autofix) {
        descriptor.fix = (fixer) => fixer.replaceTextRange(range, replacement);
      } else {
        descriptor.suggest = [
          {
            messageId: "replaceWithLg",
            data: { token: rawToken, replacement },
            fix: (fixer) => fixer.replaceTextRange(range, replacement),
          },
        ];
      }

      context.report(descriptor);
    }

    return {
      JSXAttribute(node) {
        if (node.name.name !== "className" && node.name.name !== "class")
          return;
        if (!node.value) return;

        const strings = collectStringNodes(
          node.value.type === "JSXExpressionContainer"
            ? node.value.expression
            : node.value,
          context.sourceCode,
        );
        if (strings.length === 0) return;

        // The shape evidence may live in a sibling fragment of the same
        // clsx()/template call, so judge circularity against the whole element.
        const classText = strings.map((entry) => entry.text).join(" ");
        // A `/*` inside an arbitrary value is not a local problem: Tailwind emits
        // it verbatim, and CSS then swallows everything up to the next `*/` --
        // including, in Hicks's case, the very `border-radius` rule under
        // discussion, which computed to 0. Nothing about such an element can be
        // read off its class list, so it is left alone rather than reported on
        // evidence the stylesheet has already destroyed.
        //
        // Two refinements, both from Hicks. A `/*` inside a *quoted* value is a
        // CSS string, not a comment -- `before:content-['/*']` opens nothing, and
        // suppressing on it silenced a real 16x32 lozenge. And a comment that
        // opens in one candidate and closes in another swallows every class
        // between them, so `h-[1px/*] ... content-['*/']` is just as unreadable
        // as an unterminated one even though a `*/` does appear later: once a
        // comment is open, quoting stops meaning anything. So the test is whether
        // every comment is wholly contained in a single candidate, not whether
        // the text as a whole happens to balance.
        //
        // A third refinement, also from Hicks: a candidate that emits no CSS
        // cannot open a comment either. `not-starting:w-[1px/*]` compiles to
        // nothing at all, so the stylesheet is intact and the element must still
        // be judged. Inert candidates are dropped before the scan.
        const liveCandidates = classText
          .split(/\s+/)
          .filter(
            (candidate) => !isInertVariant(splitCandidate(candidate).segments),
          );
        // A fourth refinement, and the only one that runs the other way. The
        // suppression above exists because a comment can swallow the *radius*
        // rule, leaving the linter to report a bubble the browser never draws.
        // But when the escaping comment is inside the radius utility's own
        // arbitrary value, the radius text lies entirely to the left of the `/*`
        // and is parsed normally: `rounded-[9999px/*c] w-[64px] h-[32px]`
        // measures a 64x32 box with a 9999px radius -- a lozenge, drawn, and
        // silently excused. Tailwind emits `width`/`height` ahead of
        // `border-radius`, so the shape evidence survives too; all that the
        // comment eats is whatever sorts after it.
        //
        // So a lone escaping comment is judged rather than skipped, and only the
        // escaping candidate itself is judged. Every other shape still returns:
        // two escaping comments may have swallowed each other's rules, and the
        // rule would be reasoning about CSS that was never applied. Found while
        // checking a claim of Vasquez's that this case was safe; it is safe from
        // false reports, which is what he checked, but not from false excuses.
        const escaping = liveCandidates.filter(hasEscapingComment);
        const soleEscaper = escaping.length === 1 ? escaping[0] : null;
        if (escaping.length > 0 && soleEscaper === null) return;
        // An arbitrary value whose parens do not balance cannot be read at all:
        // Tailwind closes it, but measurably in two contradictory ways. Skipping
        // is the only direction that cannot report a circle that is really round.
        if (liveCandidates.some(hasUnbalancedParens)) return;
        const isCircularAt = shapeEvidence(classText);
        const waived = hasWaiverAttribute(node.parent);

        for (const { node: stringNode, text, textStart, quasi } of strings) {
          for (const match of text.matchAll(/\S+/g)) {
            const rawToken = match[0];
            const { segments, utility: bareToken } = splitCandidate(rawToken);
            // A radius on a variant that compiles to nothing is not on the page.
            if (isInertVariant(segments)) continue;
            // When one candidate carries an escaping comment, only that candidate
            // is judged: its own declaration provably survives, because its value
            // lies left of the `/*`, while anything sorted after it may have been
            // eaten. If that sole escaper is not a radius token at all, nothing is
            // judged and the element is excused -- which is why no separate test
            // for that is needed here.
            if (soleEscaper !== null && rawToken !== soleEscaper) continue;
            const size = parseRoundedSize(bareToken);
            if (size === null) continue;

            const reportFullRound = () => {
              if (!checkFullRound || waived) return;
              if (isCircularAt(segments)) return;
              reportToken({
                textStart,
                stringNode,
                quasi,
                rawToken,
                offset: match.index,
                messageId: "fullRound",
                data: { token: rawToken },
                autofix: false,
              });
            };

            if (size === "full") {
              reportFullRound();
              continue;
            }

            const isArbitrary = size.startsWith("[") && size.endsWith("]");
            const px = isArbitrary ? arbitraryToPx(size) : NAMED_RADII[size];

            // An unresolvable value (var(), calc()) or an unrecognised size is
            // not reported: a violation that cannot be proven is not a violation.
            if (px === null || px === undefined) continue;

            if (px === Infinity) {
              reportFullRound();
              continue;
            }

            if (px > maxPx) {
              reportToken({
                textStart,
                stringNode,
                quasi,
                rawToken,
                offset: match.index,
                messageId: "oversized",
                data: { token: rawToken, px: Math.round(px * 100) / 100 },
                // Named sizes map onto the scale deterministically, so they are
                // safe to rewrite. An arbitrary value may have wanted `md` or
                // `sm`, so it only gets a suggestion a human has to accept.
                autofix: !isArbitrary,
              });
            }
          }
        }
      },
    };
  },
};
