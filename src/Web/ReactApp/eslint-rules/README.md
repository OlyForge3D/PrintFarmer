# Local ESLint rules (`local/*`)

Project-specific rules that encode PrintFarmer conventions a generic linter cannot know
about. Registered in [`eslint-plugin-local.js`](./eslint-plugin-local.js) and configured in
[`../eslint.config.js`](../eslint.config.js).

| Rule                            | Enforces                                                                                                                          |
| ------------------------------- | --------------------------------------------------------------------------------------------------------------------------------- |
| `local/pf-require-apiclient`    | All REST calls go through the `apiClient` singleton from `@/services/api` — no direct `axios`/`fetch`, no custom axios instances. |
| `local/pf-no-raw-html-controls` | Shared UI components instead of raw `<button>`/`<input>`/`<select>`/`<textarea>`. See `UI_COMPONENTS_GUIDE.md`.                   |
| `local/pf-no-unguarded-console` | `console.log/debug/info` in UI code is wrapped in a `window.PrintFarmerDebug` guard; no raw object dumps in JSX.                  |
| `local/no-hardcoded-colors`     | Theme tokens instead of literal hex/rgb/Tailwind palette colors.                                                                  |
| `local/pf-no-oversized-radius`  | Border radii stay inside the `DESIGN-LANGUAGE.md` scale. See below.                                                               |

## `local/pf-no-oversized-radius`

[`DESIGN-LANGUAGE.md`](../src/design-system/DESIGN-LANGUAGE.md) defines a five-step radius
scale (`--pf-radius-xs` 2px → `--pf-radius-lg` 8px, plus `--pf-radius-full`) and states that
**rectangular surfaces cap at 8px**; fully-round is reserved for shapes that are genuinely
circular or pill-shaped by design. Tailwind ships radii far above that ceiling
(`rounded-2xl` = 16px, `rounded-3xl` = 24px), and arbitrary values like `rounded-[1.75rem]`
bypass the scale entirely. This rule keeps both off rectangular surfaces.

It reports two independent families.

### 1. Over the ceiling

Any radius larger than `maxPx`, whether a named size (`rounded-xl` … `rounded-4xl`) or an
arbitrary value (`rounded-[1.35rem]`). Tailwind's optional data-type hint is understood, so
`rounded-[length:1.35rem]` is judged the same as the bare form rather than skipped as
unresolvable. The arbitrary-property form (`[border-radius:12px]`) is judged too — it sets
`border-radius` without ever spelling `rounded`, so until the rule read it, any value at all
passed unseen. Values that genuinely cannot be resolved at lint time (`rounded-[var(--x)]`,
`rounded-[calc(…)]`) are never guessed at — an unprovable violation is not a violation.

Named sizes are **auto-fixable** — `--fix` rewrites them to `rounded-lg`. Arbitrary values
only get a _suggestion_, because the correct replacement is a judgement call: a 1.1rem inner
panel nested inside a 1.35rem card usually wants `rounded-sm`, not `rounded-lg`, to preserve
the concentric descent.

### 2. Fully round (`checkFullRound`)

`rounded-full` on an element that is not demonstrably circular. On by default in the rule's
own options; the project also sets it explicitly, so the config states its intent rather than
inheriting it.

An element passes without any code change when its own classes prove it is round:
explicit matching `w-N`/`h-N`, `size-N`, `aspect-square`, or a spinner animation
(`animate-spin`, `animate-ping`, `pf-animate-spin`). That covers avatars, dots, spinners and
circular icon buttons automatically.

Everything else that legitimately needs a pill — tag chips and progress bars, both sanctioned
by `DESIGN-LANGUAGE.md` — declares it explicitly:

```tsx
<span data-pf-radius="full" className="rounded-full px-3 py-1">
  {tag.name}
</span>
```

The value is required. A bare `data-pf-radius` does not waive, because it does not say which
radius was intended; only `="full"`, `{true}` or a computed expression does.

`data-pf-progress-track` and `data-pf-progress-fill` are honoured as waivers too, and those _are_
boolean markers, so they waive bare. This follows the existing `data-pf-button` convention: it is
greppable, it survives renames, and it cannot be satisfied by accident the way a className
substring match can.

If the element is neither provably circular nor a sanctioned pill, it is neither of those things
and wants `rounded-xs`. That is the usual answer, and the rule offers it as a suggestion.

#### How the shape evidence is read

The rest of this section is the parser's reasoning. It is worth reading when the rule's verdict
surprises you, and skippable otherwise.

Shape evidence is matched **against the variant condition the radius is under**, treating the
variant list as a set: evidence applies when its own condition is a subset of the radius's. A
bare `rounded-full` is excused only by bare evidence; `md:hover:rounded-full` is excused by bare
evidence, by `md:` evidence, or by `md:hover:` evidence, since each of those still applies in
the state the radius does. This is what lets a range input prove its own thumb is circular:
`[&::-webkit-slider-thumb]:w-4 [&::-webkit-slider-thumb]:h-4 [&::-webkit-slider-thumb]:rounded-full`
passes, while the rectangular host `<input>` is still judged on its own bare classes. It also
keeps the guard that matters: `md:aspect-square` cannot excuse a bare `rounded-full`, because
the element is not square below that breakpoint.

Where several conditions could apply, the most specific one wins, so a state that overrides a
dimension is respected: `w-8 h-8 hover:w-4 hover:rounded-full` is reported, because on hover the
element is a 4×8 lozenge. `size-N` is read as `w-N h-N` so it takes part in the same resolution
and can be contradicted the same way — `size-8 hover:w-4 hover:rounded-full` is reported.
`aspect-square` and the spinner animations name no dimensions, but they are overruled by
different evidence. `aspect-square` loses to any _definite_ width/height pair, however coarsely
that pair was selected, because `width`/`height` and `aspect-ratio` are resolved in separate
cascades and only then combined — used-value computation drops the ratio whenever both axes are
definite. So `aspect-square w-4 h-8` is a 4×8 box, `aspect-square w-full h-8` is not a circle
either, and `w-4 h-8 hover:aspect-square` is a lozenge on hover even though the ratio is the more
specific declaration. Comparing the two declarations' specificity would be arbitrating a contest
CSS does not hold — raised by Hicks. An
animation is a heuristic rather than a declaration, so nothing in the cascade overrules it; it
loses only to a pair _proved_ to differ. `animate-spin w-4 h-8` is a lozenge and is reported,
but `animate-ping h-full w-full` is exactly what a pulse indicator inside a square parent looks
like, and the rule cannot see the parent. A _proven_ non-square ratio is such a proof, so it
contradicts an animation too — `animate-spin aspect-[2/1]` is a lozenge whatever its size — and
stops doing so once any definite pair overrides the ratio.

`aspect-*` therefore has **four** readings, not two, and only one `aspect-ratio` declaration
reaches the box in any given state — the most specific one, which the rule resolves before
reading it:

- **square** — provably 1:1 (`aspect-square`, `aspect-[2/2]`). Evidence of a circle.
- **non-square** — provably not 1:1 (`aspect-video`, `aspect-[4/2]`). Proof of a lozenge, strong
  enough to contradict a spinner animation.
- **removed** — readably _un-squares_ the box: `aspect-auto`, `initial`, `unset`, and a degenerate
  `[0/1]` or `[1/0]`, all of which CSS resolves to `auto`. The earlier proof is genuinely gone, so
  these withdraw it and the site is reported.
- **unknowable** — valid CSS whose value the rule cannot read: any function call (`calc()`,
  `min()`, `var()`), and `inherit`/`revert`/`revert-layer`, which expose a parent or lower-cascade
  value that is not in this file. These do **not** withdraw a coarser proof.

That last distinction is the one worth understanding, because it is the only place the rule
knowingly accepts a miss. `aspect-square hover:aspect-[calc(1/2)]` really is a 2:1 lozenge on
hover, and it passes silently. The alternative was worse: Tailwind normalises `aspect-[calc(1)]`
to `aspect-ratio: calc(1)`, which is 1, so withdrawing on anything unreadable reports genuine
circles. The only way to silence such a report is `data-pf-radius="full"` — and that attribute is
documented above as the ledger of deliberate **pills**. Filing a circle in it makes the codebase
lie in the one place a later reader goes looking. A missed lozenge is a missed lozenge; a
dishonest waiver corrupts the record. Bishop, Hicks and Vasquez converged on this after
disagreeing about it; the reasoning that settled it was the waiver's documented meaning, not the
CSS.

A value that is not valid CSS at all is a fifth case, and behaves oppositely to _unknowable_: the
declaration is dropped and the earlier ratio stands untouched, so
`aspect-square hover:aspect-[banana] hover:rounded-full` is still a circle, while a bare
`aspect-[banana] rounded-full` has no evidence and is reported. Dropped and indefinite are easy to
conflate and behave in opposite directions — a dropped declaration leaves the previous one
standing, an indefinite one replaces it. That covers a
negative component, and anything outside CSS's `<number>` grammar, which is narrower than
JavaScript's — `[0x2]` is a number to `Number()` and to nobody else. Ratios are compared by value
rather than by spelling, so `aspect-[2/2]` is square and `aspect-[4/2]` is not, and the sign test
looks at the components because `[-1/-1]` divides to 1. Replacement is subject to the same
specificity rule as everything else, so `aspect-square hover:aspect-[2/1] hover:rounded-full` is
reported while the unprefixed `aspect-square hover:aspect-[2/1] rounded-full` is not.

The same dropped/indefinite/unknowable split governs `w-`/`h-`/`size-`, and for the same reasons.
A declaration CSS discards — `h-[banana]`, `h-[calc(2)]`, `h-[none]`, a mis-cased data-type hint
like `h-[LENGTH:1rem]` — leaves the previous height standing, so `w-8 h-8 hover:h-[banana]` is
still a 32×32 circle. A valid but indefinite one (`h-auto`, `min-content`, `stretch`) replaces it
and unpins the axis. And a value the rule cannot resolve to a number — `calc(1px*2)`,
`min(1px,2px)` — is _opaque_: it pins nothing and contradicts nothing, keeping only the power to
prove a square when the identical value appears on both axes. A bare `-1px` is not opaque but
dropped, because a negative length is invalid on `width`/`height`. The rule does not type-check CSS
math, and where it cannot know, it does not withdraw a proof.

Whether it withdraws the _previous_ value is a separate question, and the answer is whether CSS
applied it. Where the arithmetic resolves to a length, or the value is a `var()`, the declaration
certainly won and the one it overrode is gone, so the axis is withdrawn as well as filed opaquely.
Omitting that resurrected a value CSS had discarded: in
`aspect-square w-[4px] h-[32px] hover:w-[calc(32px*1)]` the stale 4px contradicted `aspect-square`
and reported a genuine 32x32 circle — raised by Bishop. Where the arithmetic cannot be followed at
all, or its addends disagree in unit degree, the declaration may simply be invalid, so the earlier
one stands exactly as it does for any other dropped value. A negative _result_ is not such a case:
`calc(1px - 2px)` parses, applies and clamps to zero, so it does withdraw. Only a bare `-1px` is
dropped. Treating both alike was wrong in the reporting direction — raised by Hicks, with computed
styles, against a reading that CSS drops the computed form too.

That last power is fenced on both sides. Carrying a length token is not the same as _being_ a
length: CSS multiplication adds units and division cancels them, so `calc(1px*2)` is a length but
`calc(1px/1px)` is a number and `calc(1px*1px)` an area, and CSS drops both exactly as it drops
`calc(2)`. A pair CSS never applies proves nothing, so unit degree is checked whenever the
arithmetic can be followed — and where it cannot, the value stays opaque rather than being guessed
at. `min()`, `max()` and `clamp()` are followed on the same terms, their arguments having to agree
in degree; arity is part of that, since `clamp()` takes exactly three arguments and CSS drops it
outright otherwise. `abs()`, `round()`, `mod()`, `rem()` and `hypot()` are followed on those same
terms — they preserve their operands' degree, and each has its own arity to check. `round()` is the
exception that proves the rule: its optional first argument is a strategy keyword rather than an
operand, so it is dropped before degrees are compared and before the arity is counted, and it is
allowed through the token walk as an argument and nowhere else — `w-[up]` is still no length, and an
invented strategy is still not one. Raised by Hicks. CSS's math constants are admitted on the same
terms — `pi`, `e`, `infinity` and `NaN` are `<number>`s of fixed value, so they are scalars wherever
they appear and `calc(1px*pi)` is a plain length; refusing them dropped both axes and reported a
real circle, raised by Vasquez. `env()` and `attr()` stay out for the reason `var()` does: their
contents are unknown and may be a percentage, as `env(x,50%)` shows plainly. Treating them as
unreadable left a stale base width standing — raised by Hicks. Being merely _comparable_ is not
enough for that: comparability only settles whether two identically spelled values may be paired,
whereas following the arithmetic is what lets a value withdraw its axis and hand the question to
`aspect-square`. Fixing one without the other left `calc(32px)` excused and `abs(32px)` reported.
The pair must also still be _winning_: in `w-[calc(1px*2)] h-[calc(1px*2)] hover:w-8` the
hover width is more specific, so on hover the box is 32×2 and the standing pair no longer describes
it. The comparison runs the other way too, so a hover-scoped opaque pair does overrule a base
`w-8 h-8`.

One more asymmetry, easy to get wrong in the tempting direction: a percentage **height** is
definite only when the parent's own height does not depend on its content, so `h-full` cannot
overrule a ratio and `w-full h-full aspect-square` reads as a square. Widths have no such rule,
so `aspect-square w-full h-8` stays reported — it is a real 100%-by-32px lozenge.

Evidence is also read **in the scope the radius is in**. `[&:hover]` and `[.group:hover_&]` are
this element in some state, so its own `w-`/`h-` bear on them, while `*:`, `[&_img]:` and
`before:` name a different box, whose dimensions the host's classes do not describe. Host evidence
may still _excuse_ a descendant-scoped radius, which errs toward leniency, but it may no longer
contradict one: pairing the host's width with a child's height invented lozenges that existed on
neither element.

What counts as leaving the host is narrower than it looks, and is decided against what Tailwind
actually emits rather than against how the variant reads. A combinator moves the target only when
it sits at the top level of a **bare** `[…]` selector, somewhere after the `&` — `[&:hover_img]`
emits `&:hover img` and is a descendant, `[.group:hover_&]` puts the combinator before the `&` and
is still the host. A combinator inside a payload moves nothing: `[&:has(>img)]` is `&:has(>img)`,
and a _named_ variant takes its bracket as an argument entirely, so `has-[&>img]` emits
`&:has(*>img)` and `supports-[selector(::before)]` emits an `@supports` block — in both the radius
stays on the host, where host dimensions can contradict it. The one exception is
`:is()` and `:where()` standing alone as their own compound: they are selector
_lists_, so `[:is(&_section)]` emits `:is(& section)` and really does target the
`<section>` — but attached to a compound they merely qualify it, and
`[&:is(.a_.b)]` is still the host.

The proof of squareness is scoped as well as the contradiction. Reading only the
winning declaration across the whole cascade let a host pair carrying more
variants displace a descendant's own, so host evidence condemned a descendant
indirectly; the element's own scope is now tried as a fallback. The asymmetry
survives in the lenient direction only — a host pair that outranks a
descendant's still _excuses_ it, which is a false negative the rule accepts.

Proving squareness needs **comparable** lengths, not merely equal spelling. `w-full` and
`h-full` are both definite, but they are 100% of two _different_ containing-block axes, so they
are equal only when the parent happens to be square; `w-screen`/`h-screen` expand to `100vw` and
`100vh`; fractions are likewise axis-relative; and `fit`/`min`/`max` size to content. None of
those pairs proves a circle. What does: the spacing scale, `px`, the viewport-unit keywords, and
arbitrary values built only from axis-independent units.

That last case generalises further than it first looks, because comparability is only consulted
after the two value _strings_ have already matched exactly. So an axis-dependent unit cannot slip
through on unit alone — `w-[10cqw]` and `h-[10cqh]` are different strings and never match, while
`w-[10cqw] h-[10cqw]` is 10% of the container's _width_ on both axes and genuinely is a circle.
The same holds for `lh`, for the container-query units, for the inline- and block-axis viewport
units (`vi`, `vb`), and for `min()`/`max()`/`clamp()`/`calc()` over comparable operands, along with
`abs()`, `round()`, `mod()`, `rem()` and `hypot()`, which preserve their operands' dimension and do
not care which axis they land on — `w-[abs(32px)] h-[abs(32px)]` is a plain circle, and refusing it
reported one. Raised by Hicks.
Scientific notation is accepted because CSS accepts it: `w-[1e2px]` is a valid 100px length.
`sin()`, `cos()`, `tan()`, `pow()`, `sqrt()`, `log()`, `exp()` and `sign()` return a `<number>`
rather than a length, and angle units are legal only _inside_ them, so each such call is collapsed
to a bare scalar before the value is read at all: `w-[calc(100px*sin(90deg))]` is an ordinary
100px length that was being refused on sight of `deg`, dropping both axes and reporting a real
circle — raised by Vasquez. Widening the unit and function whitelists instead, as first proposed,
would have excused `w-[10deg]`, `w-[sin(90deg)]` and `w-[sign(2rem)]`, none of which are lengths.
`asin()`, `acos()`, `atan()` and `atan2()` return _angles_, so they are deliberately not collapsed:
`calc(1px*atan2(2,1))` is invalid and CSS drops it, which is an absence of evidence rather than
evidence of a circle. A call is only collapsed at an argument count CSS accepts, for the same
reason — `sin(90deg,0deg)` is invalid, and collapsing it regardless manufactured a length out of a
declaration that never applied. Raised by Hicks.
A call is likewise only collapsed when its arguments are of a type the function accepts, so
`sin(10px)` and `pow(2rem,2)` stay refused, and an _empty_ argument (`sin()`) is refused too — it
was reading as arity one, which excused a genuine lozenge that the base rule reports, the one place
this work made the rule worse before making it better. `sign()` is exempt, because CSS Values 4
gives it no type restriction. Only a _bare_ dimension is typed by unit: units inside an argument may
cancel, `sin(1px/1px)` is a legal dimensionless number, and rejecting it left both axes opaque and
reported a square that is square by construction — raised by Vasquez. A compound argument is not
therefore waved past, though; it is asked what it resolves to, and only degree zero is a
`<number>`. Trading the whole check away for that one case meant `sin(1px*2)` — a length where CSS
wants a number, so the declaration is dropped — silently excused a lozenge the base rule reports.
Raised by Hicks. That reading recurses into the argument's own numeric calls, but strictly inward,
one nesting level per step, so it terminates. `round()`'s rounding
interval is optional (`round(<rounding-strategy>?, A, B?)`), so `round(2)` is accepted.
The collapse is a claim about _type_, not magnitude: `calc(32px*sin(45deg))` is never equated with
`32px`, because two arbitrary values are only ever compared as written. That is also why a
substituted argument needs no special case — `sin(var(--a))` collapses to the same scalar on both
sides, so `w-[calc(32px*sin(var(--a)))] h-[calc(...)]` is square whatever `--a` is, while the same
width against a plain `h-8` stays unprovable and is still reported. A guard on `var()` here was
tried and removed: it broke only the first of those and bought back no report.
What is still refused is anything whose length depends on which
property it lands on even when spelled identically (a bare `%`), anything unknowable at lint time
(`var()` and `env()`, whose values can themselves hold a percentage, or an unrecognised function),
anything that is not a length (`10deg`, a non-zero unitless
number), and bare keywords like `fit-content`.

`w-auto` / `h-auto` / `size-auto` are recorded as _indefinite readings_ rather than skipped,
because `auto` is the everyday dimension keyword that pins nothing, and `aspect-ratio` is only
overruled when _both_ axes are definite — `w-full h-auto aspect-square` really is a circle.
Recording rather than skipping is what lets a more specific `auto` override a coarser length:
`w-4 h-8 hover:w-auto hover:aspect-square` is a 32×32 circle on hover, and dropping the reading
left the stale `w-4` standing and reported a lozenge — raised by Hicks. An indefinite reading
leaves that axis unproven, which neither excuses a radius on its own (`w-8 h-auto rounded-full`
still reports) nor contradicts a ratio. The CSS-wide keywords behave the same way, because the
initial value of `width`/`height` is `auto`, so `h-[initial]`, `h-[unset]`, `h-[revert]` and
`h-[revert-layer]` are all indefinite, and `h-[inherit]` is treated the same way because the
inherited computed value is not knowable here. Matching is case-insensitive, as CSS is. Any value
containing `var()` joins them, wherever the `var()` sits, and unlike other opaque values it cannot
prove a square even when identical on both axes — raised by Hicks, against an earlier claim here
that it could. Substitution happens before the value is parsed, so the same token may be an
axis-relative percentage that resolves to different lengths on the two axes; it may equally hold
`auto`, which makes `calc(auto + 0px)` invalid at computed-value time, and an invalid
non-inherited declaration falls back to its initial value — which is `auto`.

`theme()` is opaque for the same reason and one more: it is resolved at build time against the
project's theme, which `repairedValue` does not model. The emission test therefore asserts only that
Tailwind emits nothing for the shapes it is given, not that a reproduction matches — which is
verdict-neutral, since an unresolved `theme()` and a resolved one are equally opaque here. Raised by
Bishop.

Identical spelling is the one path that reads a value without typing it, so it is checked first for
values the browser can be _shown_ to drop. `w-[calc(1px_2px)] h-[calc(1px_2px)]` is two juxtaposed
lengths where `calc()` wants one expression; both declarations are dropped, whatever pair sits
underneath them stands, and reading equality from spelling alone excused a real lozenge — raised by
Hicks. The test is deliberately one-sided: a value is condemned only when it holds no function call
_and_ no sign. The function-call half is because there the whole grammar is understood and it still
would not parse, so `clamp(1rem,2vw,3rem)` keeps its innocence of a fault this model cannot see,
while an unrecognised name is _not_ deferred — `banana(1px)` is a parse error, and being generous
about unknown names let one prove a square.

A semicolon is the third way in, and it needed no grammar at all: `size-[calc(1px+2px;3px)]` holds a
call and a sign, so both halves above wave it past, and the pair spells identically so the element
looked square. It is not. Tailwind emits the semicolon verbatim, the browser ends the declaration at
it, and the element measures 1264×0 in Chromium with the radius still applied. Raised by Hicks. The
check runs on the comment-stripped text rather than the raw one, so `abs(1px/*x;y*/)` — a real 1px —
is not condemned along with it. I first wrote here that the orphaned `3px)` kills the _following_
declaration as well; it does not. `width: calc(1px+2px;3px); height: 1px` measures 1264×**1**, so the
next declaration survives, and the sentence was corrected after measuring rather than left standing
because it sounded plausible.

The check is blunt: any surviving semicolon condemns, wherever it sits. That is wrong in principle,
because CSS `if()` uses semicolons to separate its branches and a semicolon there is ordinary. A
scoped version was written — a stack tracking which function encloses each semicolon, with `if()`
suspending the test for its whole subtree — and then deleted, because it agreed with the blunt test
on all 483 pins and neither removing its scoping nor removing its exemption moved a single verdict.
The reason is the limitation below: `if()` is a function this rule cannot read at all, so an `if()`
value is refused for opacity before its semicolons are ever reached. Ninety lines no test can see are
worse than the one line they replace. The day `if()` becomes readable is the day the scoping earns a
pin, and there is a follow-up issue for it.

### `!important` has two spellings and only one was modelled

`rounded-xl!` and `!rounded-xl` are Tailwind's importance markers, and the rule read them from the
start. But Tailwind copies the contents of an arbitrary value into the declaration verbatim, so
`size-[16px!important]` emits `width: 16px!important` — the same cascade mechanism, spelled inside
the brackets where the class-level reader never looked. `size-[16px!important] h-[32px]` is therefore
a genuine 16×16 circle, measured, and the rule reported it as a lozenge. Raised by Hicks.

Every clause of the fix was measured rather than assumed, because CSS's grammar here is looser than
it looks: whitespace and comments may sit on either side of the `!`, the keyword is case-insensitive,
and nothing may follow it. So `16px!IMPORTANT`, `16px_!important`, `16px!/*c*/important` and
`16px!important/*c*/` are all important, while `16px!importantx` is not — that last one is a whole
invalid declaration, which is why the value is handed on untouched for the ordinary readers to
condemn, and why the element measures 1264×32 rather than 16×32.

### The importance check was written three times, and the first two were the wrong shape

The first two versions were regexes, and both were argued about at the level of quantifiers: greedy
head or lazy head, which separator alternation, where the optional comment goes. Mutation testing
said the choice was inert, so round 37 recorded it as unpinned and moved on — and asserted, in a
comment, that no pin could separate the two readings. Hicks produced one in the next round:
`size-[16px!important/*!important/**/] h-[32px]` measures 16×16, and the regex read the commented-out
occurrence. Vasquez arrived at the same class of defect from the other side, through `arbitraryToPx`.
Escapes broke it too — `16px!\important`, `16px!imp\ortant`, `16px!IMPOR\TANT` and `16px!\69mportant`
are all important in Chromium and all four were missed.

Neither quantifier was ever the answer. Both versions pattern-matched _text_ where a CSS parser
_reads a value_, so every case that separated the two was a case where reading and matching diverge:
a comment, an escape, a separator. The third version does what the parser does — discard comments,
find the last `!`, validate that what follows is one identifier, decode its escapes, then compare
case-insensitively against `important` — and it is both correct on all twelve measured spellings and
shorter than either regex. The same pivot as the repaired-value emitter earlier in this rule's
history: stop deriving the model, implement the mechanism.

Two consequences worth stating, because both look like gaps and neither is. `\69mportant` is
important but `\69 mportant` is not: the space terminates the numeric escape, and Tailwind escapes
that space into the generated _selector_, so the class never matches at all. And `_` is Tailwind's
space, so `\69_mportant` and `!_important` behave as the spaced spellings would — all pinned.

The keyword is matched from the last _unescaped_ `!`, and the qualifier is the whole point. Round 38
argued that first-versus-last was a provable equivalence: comments are gone by then, so any remaining
`!` is real, and two real ones make the declaration invalid under either reading. The premise was
false. `\!` survives comment stripping and is a literal character, not a delimiter, and
`h-[var(--x\!y,32px)!important] w-[16px]` is a measured 16×32 lozenge that a first-`!` reading lets
through. Hicks built that case after the claim was made — the second time in two rounds that a claim
here was stronger than its evidence, and the reason the escape-skip is now written down as unpinned
rather than presented as proved.

Four more spellings of the same lesson, all measured, all found by Hicks after the reader was
written. A six-digit hex escape above U+10FFFF is legal CSS and illegal input to
`String.fromCodePoint`, so `size-[16px!\110000]` **crashed the linter** with a `RangeError` mid-run;
CSS Syntax §4.3.7 says to substitute U+FFFD, and now the decoder does. `stripCssComments` substitutes
a _space_, so removing an interior comment padded the value — `size-[16px/*c*/!important]` became
`size-[16px ]`, which no reader parses, and that produced a false report on a measured circle and a
false excuse on its measured lozenge twin, one error in each direction from one missing trim.

### Four valid lengths the radius reader could not read

`16PX`, `+16px`, `1e1px` and `_16px_` are all valid CSS, measure 16px, 16px, 10px and 16px, and were
all returned as unresolvable — so all four were silently excused. Units are case-insensitive, `+` is
a valid sign, scientific notation is a valid `<number>`, and `_` is Tailwind's space. A leading `-`
is deliberately still refused: a negative radius is invalid, CSS drops the declaration, and
`rounded-[-16px]` really does measure 0.

The data-type hint has an ordering that matters too. `rounded-[length:/**/16px]` is a real 16px
radius and `rounded-[/**/length:16px]` computes to 0, because Tailwind honours the hint only at the
literal start of the value. Stripping comments before the hint collapsed the two into one string and
produced a false excuse for the first and a false report for the second. The hint is now removed
first, from the raw text, and comments after.

### A comment in a radius value is a comment, not an unreadable value

Every value reader in this rule strips comments before reading. `arbitraryToPx`, the one that reads
the radius itself, did not. `rounded-[16px/*c*/]` emits `border-radius: 16px/*c*/`, a real and
measured 16px radius, and the function returned `null` — so the site was waved through with no report
at all. That is a false _excuse_, which is the quiet direction: it hid oversized radii rather than
inventing them, and nothing in the suite noticed.

Vasquez found it through the artifact the old importance regex left behind,
`rounded-[16px/*!important*/!important]`, but the plain spelling on the first line shows the gap had
nothing to do with importance and predated it. Fixed by routing the value through the same
`stripCssComments` every sibling reader already used; pinned in all four spellings, including the
`length:` data-type hint and the `rounded-[9999px/*c*/]` full-round case.

### An unterminated comment does not make the radius unreadable

When a class opens a comment that never closes, the rule skips the whole element: the comment eats
the rest of the stylesheet, so the linter would be judging CSS the browser never applied. Vasquez
checked this case in round 38 and reported it safe. It is safe from false _reports_, which is what he
checked, and that half of his reasoning is right — `w-[64px/*c] rounded-[9999px] h-[32px]` really does
measure **r=0**, because the comment really did eat the `border-radius` rule, and reporting it would
be a bubble that is not on the page.

But the other half does not follow. When the escaping comment is inside the radius utility's _own_
arbitrary value, the radius text lies entirely to the left of the `/*` and is parsed normally.
`rounded-[9999px/*c] w-[64px] h-[32px]` measures a 64×32 box with a **9999px** radius — the exact
lozenge this rule exists to catch — and it was being waved through. Tailwind emits `width` and
`height` ahead of `border-radius`, so the shape evidence survives too; all the comment eats is
whatever sorts after it.

So a lone escaping comment is now judged rather than skipped, and only the escaping candidate itself
is judged. Two escaping comments still suppress the element, because they can swallow each other's
rules. The other-token suppression is not theoretical either:
`rounded-[8px/*c] rounded-[9999px] w-[64px] h-[32px]` measures r=8px, so judging the second token
would be a false report — pinned, along with its mirror image, which measures r=9999px and must be
reported.

The sign half is the subtler one, and it was got wrong twice in opposite directions.
`w-[calc(2rem+4px)]` looks like the same fault as the juxtaposed pair, because CSS Values requires
whitespace around `+` and `-` and the tokenizer would read `2rem` and then `+4px`. That reading is
correct about CSS and irrelevant here: the rule is given a Tailwind _class name_, and Tailwind
normalises the spacing before emitting, so the stylesheet says `width: calc(2rem + 4px)` and the
element really is 36×36. Condemning the unspaced spelling reported a genuine circle. Both Bishop and
Vasquez checked the grammar and agreed with the condemnation; Hicks checked the compiled output and
overturned all three of us. **Reason about emitted CSS, not source CSS.**

The over-correction was to let a sign _anywhere_ buy that amnesty. Tailwind's repair is confined to
math functions: `w-[1px+]` is emitted verbatim as `width: 1px+`, the browser drops it, and the
element keeps whatever size it already had. A blanket amnesty silenced that and silenced it for the
whole element — Hicks again, with the compiled output again. The guard is now scoped to a `calc()`
body, which is exactly the text Tailwind rewrites. The same sign therefore means opposite things
either side of a bracket, and both spellings are pinned so neither can drift.

The other half was four numeric patterns that all admitted a leading `-` and none a `+`, so a signed
length was unreadable rather than merely unusual. Widening them was twice judged unfalsifiable — once
by me and once by Bishop, who built the widened mutant himself and measured no difference across
fourteen probes — and it was the pins that were wrong both times. The cases in hand were all
identical _twins_, which are settled by the liveness guard above and never reach the numeric
comparison; a length spelled two ways — `w-[+1px] h-[1px]` — puts the comparability gate and the
value reader on the path; and `size-[+2rem]` puts the _fourth_ pattern there, because one dimension
means no twin, so nothing intercepts the value before the validity check condemns a real length and
reports a real square. That last one only became visible once the amnesty above was narrowed, which
is the whole lesson: **a surviving mutant means no pin distinguishes the change, which is as often a
missing pin as it is dead code, and the two are only told apart by constructing the case.** All four
patterns are now load-bearing and pinned individually. `w-[-2rem]` is pinned as _reporting_, because
`width` takes no negative length and that declaration really is dropped — the widening admits a sign,
not any sign. Reported independently by Hicks and by Bishop, who each caught the twin symptom.

A CSS comment is whitespace, and Tailwind copies one straight through: `w-[1px/**/]` emits
`width: 1px/**/` and computes to 1px. Reading the comment as part of the value made an ordinary
length unrecognisable, and this test condemns what it cannot recognise, so a genuine 1×1 circle was
reported. Comments are stripped before any dimension is read, unterminated ones included, since CSS
closes a comment at end-of-input rather than discarding the declaration. Raised by Hicks.
Vasquez's case, and his line: a broken _selector_ drops the whole rule, so misjudging it fails toward
excusing and is safe, while a broken _value_ is an axiom the proof rests on, so trusting one
hallucinates a square that is not in the DOM.

Eight known limitations, deliberately left. Numbered, because the count has drifted against the
prose once already — raised by Bishop, and drifted again when the eighth was added.

1. A radius is judged in the state it is _declared_ in rather than in every state it survives into,
   so `w-8 h-8 rounded-full hover:w-4` is excused even though the box is 16×32 on hover. Both Hicks
   and Vasquez independently built cases resting on this and attributed them to the specificity
   model; it predates this rule's current form and reproduces unchanged on `development` at
   `bfd9bd766`. Widening it is a real change of scope rather than a fix, and is tracked separately.
2. Breakpoints are modelled as an unordered set rather than as Tailwind's cumulative cascade, so
   `w-8 h-8 md:w-4 lg:rounded-full` is excused when it should not be.
3. Sorting the variant list collapses `[&_img]:hover:` and `hover:[&_img]:` onto one key.
4. Unprefixed host evidence is accepted as an _excuse_ for a radius scoped to a descendant, so
   `w-8 h-8 [&_img]:rounded-full` passes even though the `<img>` need not share the host's size. It
   is not accepted as a _contradiction_: pairing the host's width with the descendant's height
   invented a lozenge that existed on neither element, so contradiction evidence must come from the
   same selector scope as the radius — raised by Hicks.
5. Ties between conditions of equal variant count are unioned rather than resolved, so given
   `hover:w-8 focus:w-4 hover:focus:rounded-full` the rule considers both widths and accepts the one
   that matches.

The remaining three are stated in place below: `var()` read as indefinite against `aspect-square`,
`auto || <ratio>` proving squareness but never a lozenge, and `if()` read as indefinite.

Which conditions are _equal_ is itself modelled rather than counted: a media
variant emits an at-rule around an ordinary class and so adds no specificity, which means `hover:`
outranks `md:lg:` however many breakpoints are stacked, and the raw variant count only breaks ties
among conditions that bear the same number of selectors. Counting every variant alike was worse
than imprecise — it let `md:lg:w-8` outrank `hover:w-4` and report a real circle, the one direction
these approximations must not fail in. Raised by Bishop, and found from the excusing side by Hicks,
where the same tie let a 16x32 lozenge pass. Named range variants such as `max-lg:` emit nested
media queries exactly as `md:` does and are modelled the same way — raised by Hicks, whose probe
found `max-lg:max-md:w-8` beating `hover:w-4` and reporting a real circle. Ids sit above selectors
in their own column, as CSS weighs them, so `[#id]:` beats any number of stacked pseudo-classes —
raised by Vasquez, from the same reporting-a-circle direction. A variant that is nothing but a
`:where()` goes the other way entirely: Tailwind puts the utility's own class inside the wrapper,
so the selector weighs zero and loses to the bare utility rather than tying with it — raised by
Hicks, with computed styles showing the base winning. Combined with any other variant it is not
zero but _unknown_, because whether the later variant lands inside the wrapper or outside it decides
whether any weight survives, and the sorted condition key has already discarded that order. An
arbitrary variant is likewise a whole selector rather than one class, so `[.a.b.c_&]` outweighs
`hover:focus:` — both raised by Hicks, both reporting real circles. That weighing follows the
selector into the argument lists of `:is()`, `:not()` and `:has()`, which take the weight of their
most specific argument and contribute nothing themselves: tallying the text instead made
`[&:is(.a,.b)]` three classes, beat a genuine two-class condition and reported a circle — raised by
Vasquez — while the named spelling `has-[.a.b.c]` had the opposite fault, counting as one class and
losing to `hover:focus:` — raised by Hicks. An arbitrary _at-rule_ variant is not a selector at all
and weighs nothing, but it is spelled with brackets like one, so `[@media(hover:hover)]:` slipped
past the at-rule list and the colon in its media feature bought it a pseudo-class it does not have —
also raised by Vasquez. `group-[…]` and `peer-[…]` carry a selector too, and their marker class is
compiled inside `:where()`, so the arbitrary payload is the whole weight: counting it as one class
let `group-[.a.b.c]:` lose to `hover:focus:`. `:nth-child()` and `:nth-last-child()` have the
opposite fault, because an `of S` clause is weighed like `:is()` — one pseudo-class plus the most
specific selector in `S`, not one per entry — so a flat tally made `:nth-child(2 of .a,.b,.c)` four
columns' worth and beat a genuine three-class condition. Both raised by Hicks; Tailwind's own
emission settles both, `group-[.a.b.c]:` compiling to `:is(:where(.group):is(.a.b.c) *)`.
`:host()` and `:host-context()` have exactly the `:nth-child()` shape — a class for the
pseudo-class itself plus the most specific entry of its argument — and were being tallied flatly,
which made `:host(:is(.a,.b,.c))` four classes where CSS says two, outranked a real `.x.y.z` ancestor
and picked a lozenge. Raised by Vasquez, in the _reachable_ descendant spelling
`[:host(:is(.a,.b,.c))_&]:` that Hicks supplied: `[&:host(...)]` puts the utility's own class before
`:host` and cannot match a shadow host at all, so it pins nothing. The inner `:is()` is not
decoration either — `:host()` takes a single `<compound-selector>`, not a list, so `:host(.a,.b,.c)`
is invalid and pins nothing that a browser will ever run. Also Hicks. That
`:is()` wrapper is load-bearing in its own right: a payload holding a selector _list_ takes its most
specific entry rather than their sum, and a named group or peer carries a `/name` modifier after the
bracket which is not part of the selector at all — both also raised by Hicks, both reporting real
circles.
The same `:is()` treatment applies to a _bare_ arbitrary variant, which is emitted as written and
matched one list entry at a time, so `[.a,.b,.c_&]` is one class rather than three and rightly
loses to `[.x.y_&]`; and Tailwind's `in-*` variant puts its whole ancestor selector inside
`:where()` — so it weighs nothing and must not fall through to the one-class default. That test
first matched only the bracket spelling, which left `in-focus-within:` on the default; the prefix is
now zeroed however it is spelled, with `in-range:` excepted because it is a genuine pseudo-class in
its own right and there is no other `:in-*` pseudo-class for the exception to grow for. All raised
by Hicks, the second time against the fix for the first.
A bare _type_ selector sits in CSS's third column: below every class and id, yet still above the
at-rule-and-source-order tie-break that the segment count stands in for, so `group-[section]:`
really does beat `md:print:` and was losing to it — raised by Hicks. Declaring every type-bearing
payload unrankable stood in for that column for two rounds. It is not good enough, because it
defers even when a _higher_ column has already settled the question: `hover:focus:` is two classes
and beats `group-[.a_section]:`'s one whatever the type column says, and withdrawing there excused
a real 16x32 lozenge — also Hicks. So the column is now carried honestly, and only a payload that
weighs nothing yet is not empty — syntax this model does not recognise at all — stays unrankable.
The type count is read with namespaces (`*|section` is one type, not two) and does not treat an
_escaped_ underscore as a combinator, since Tailwind spells a literal underscore `\_`.
Prefixes compose, so the payload is read recursively rather than matched against a fixed list of
spellings per branch. Tailwind stacks variants — `group-nth-[1_of_.a.b.c]` emits
`:where(.group):nth-child(1 of .a.b.c)`, and `not-nth-[1_of_.a.b.c]` emits
`:not(:nth-child(1 of .a.b.c))` — so a branch that matched `nth-[…]` only where it stood alone left
the four-class weight unread and excused a lozenge. Hicks raised both spellings and called the
per-prefix regexes whack-a-mole; the payload is now peeled one prefix at a time and re-read, which
answers the compositions nobody has written down yet as well as the two that were reported. The
recursion has one exception, which is not a special case so much as the recursion asking the right
question: `not-` negates whatever _kind_ of variant follows it, and an at-rule negated is still an
at-rule. `not-print:` emits `@media not print` and adds no specificity, while `not-hover:` emits
`&:not(:hover)` and keeps its class, so the remainder is tested against the at-rule list before it
is read as a selector — raised by Hicks. Both this and the `in-*` widening above are invisible on a
two-variant example, because a wrong weight of 1 _ties_ with `focus:` and ties are unioned into an
excuse (limitation 5) that happens to agree with the browser; the pins for both put the winner on
the other side of the tie so the mechanism, not the coincidence, is what is being asserted.

That question has a third answer, which the first pass missed: some variants compile to _nothing at
all_, and a dead class must not win a comparison. `@starting-style` has no negated form, so
`not-starting:` emits no rule; `group-` and `peer-` need a selector to hang their marker on, so
`group-print:`, `group-dark:` and `group-not-print:` emit no rule either — and `group-not-print:` was
the worst of them, because the `not-print:` inside was weighed on its own merits and bought the dead
class a zero. These go unranked rather than zeroed, which under limitation 5 can only ever excuse.
The at-rule list itself was also short: `noscript`, `inverted-colors`, `pointer-*` and `any-pointer-*`
are all `@media` variants and were being weighed as classes, and the bracket spelling
`not-[@media(pointer:fine)]` arrives by the arbitrary-payload branch and was being read as a `:not()`
selector. All of this was settled by compiling every form against the app's own Tailwind and reading
what came out, rather than by reasoning about what ought to be negatable — which is the same method
that overturned the `calc` ruling above, and the same method that would have prevented it.
`:where()` is recognised whatever its case, pseudo-class names being ASCII case-insensitive, so a
capitalised `:WHERE()` no longer escapes the wrapper test and gets its contents tallied — also
Hicks. A repeated variant is repeated in the emitted selector too: `hover:hover:` compiles to
`&:hover:hover`, which is genuinely two pseudo-classes and genuinely beats a single `focus:`.
Collapsing the segments through a set before ranking counted it once and excused a real 16×32.
Raised by Hicks, confirmed by Vasquez.
`dark:` is in the at-rule list because this
project declares neither `@custom-variant dark` nor a `darkMode` config, so Tailwind compiles it
to `@media (prefers-color-scheme: dark)` — an earlier comment claiming it was class-based here was
simply wrong. That is deliberately not the same as treating the at-rule count
as noise: an at-rule adds no specificity but its utility _is_ emitted later, so it genuinely wins a
tie, which is why `size-8 md:w-4 md:rounded-full` is still a report.
Rather than re-derive CSS
specificity from a selector fragment, a condition that cannot be weighed honestly is left unranked,
and an unranked condition ties with whatever wins instead of beating it or losing to it: its values
join the set considered, so an unreadable variant can only ever excuse. A `#` inside a quoted
attribute value is not an id, and weighing `[&[href="#"]]` in the id column let it outrank every
pseudo-class — raised by Vasquez.
Repeats of the _same_ condition are unioned for a different reason:
which one wins is decided by their order in the emitted stylesheet, and the class attribute does
not determine that — Tailwind emits each utility group in its own sorted order, so
`aspect-square aspect-video` and `aspect-video aspect-square` compile to byte-identical CSS.
Reading either as "last wins" would invent an asymmetry CSS does not have, so both are excused.
`!important` is the one tie CSS does settle, and it is honoured: it outranks selector specificity
outright, so `aspect-square! hover:aspect-video` is a circle. And, limitation 6, because any value containing
`var()` is treated as indefinite, `w-full h-[var(--h)] aspect-square` is read as a possible circle
even though a `--h` holding a length would pin both axes and make the ratio moot — raised by
Vasquez.
Finally, limitation 7: `auto || <ratio>` proves squareness but never a lozenge: on a replaced element `auto`
selects the _natural_ ratio and the specified one applies only in its absence, and the rule cannot
see whether it is looking at a `<div>` or a 2:1 `<img>`. So `aspect-[auto_1/1]` still excuses a
`rounded-full`, while `aspect-[auto_2/1]` no longer contradicts a spinner animation — raised by
Hicks.
And limitation 8: CSS `if()` is not a function this rule can read, so a value built out of one is
indefinite exactly as `var()` is, whatever its branches say. `size-[if(style(--x:yes):16px;else:16px)]`
measures 16×16 in Chromium and is nonetheless reported, and so is the semicolon-free
`size-[if(style(--x:yes):16px)]` — which is the evidence that this is opacity rather than the
semicolon check, since `size-[var(--x,16px)]` behaves the same way. Raised by Hicks, whose
observation was right and whose diagnosis of it was not. Tracked separately in **#1078**.
The first seven err toward excusing rather than toward a false report — the direction that cannot
break a build or provoke a dishonest waiver, and the reason condition rank models at-rule variants
explicitly instead of counting: a proxy that can condemn is not a safe approximation, whatever its
average accuracy. Closing them means modelling breakpoint order,
selector scope, stylesheet emission order, custom-property values and the element's own type, which
is a cascade implementation rather than a shape heuristic. Tracked in **#1064**, to close before
#1046's larger churn leans on this rule. Limitation 8 is the odd one out and points the other way:
it produces a false _report_, which is the direction that can provoke a dishonest waiver, so it is
worth closing on its own terms rather than folded into the cascade work. None of the eight is
reachable in the codebase today: the only
descendant/pseudo-element radius variants are four slider thumbs, and all four carry
same-condition `w`/`h` on the thumb itself, so they pass through the sanctioned path above rather
than through a hole; no site carries two `aspect-*` utilities, a `var()` dimension or an
`auto`-bearing ratio alongside a radius; and CSS `if()` is Chrome-137-and-later syntax that appears
nowhere in this repository.

An axis counts as definite only if its declaration survives parsing, so a value the browser
discards does not pin it: `h-[10deg]` and `h-[banana]` are both dropped and the height falls back
to `auto`, leaving `w-8 h-[10deg] aspect-square` and `w-8 h-[banana] aspect-square` circles. What
counts as surviving is deliberately narrow on units and wide on structure — a unitless number
inside a function is a scalar rather than a malformed length, so `w-[calc(2*1rem)]` is a
comparable 32px, and Tailwind's `length:` data-type hint is stripped before the value is read.
All raised by Hicks.

Because "survives parsing" is decided against the browser rather than against the CSS grammar, the
answers are measured, not reasoned. A CSS comment is a token _separator_, so it is replaced with a
space rather than deleted: deleting it splices `calc(1/**/0px)` — which Chromium drops — into a
valid `calc(10px)` and manufactures a square out of nothing. Raised by Vasquez. The strip also has
to run at the _read_, not only in the comparison helpers: the symmetric spelling
`w-[1px/**/] h-[1px/**/]` was excused by the twin comparison and looked fixed, while the mixed
spelling `w-[16px/**/] h-[16px]` — a real 16×16 circle in Chromium — was dropped before any
comparison and reported. Raised by Bishop. An unterminated comment is a different problem again:
`w-[1px/*]` emits a declaration that is itself fine, but the comment swallows every rule after it,
so the element is skipped rather than judged. Tailwind's whitespace repair around operators covers
every math function and not just `calc()`, so the sign amnesty follows it there — but repair cannot
supply a missing operand, and `min(1px + , 2px)` and `abs(1px + )` are dropped exactly as
`calc(1px + )` is, so the amnesty additionally requires an operand on both sides of the sign.
Raised by Hicks.

Attacking those measurements individually kept producing separate patches for what turned out to
be one missing idea, so the rewrite it forces is now modelled explicitly, once. Tailwind rewrites
an arbitrary value before emitting it, inserting spaces around `+ - * /`, and three previously
independent bugs were all consequences of not representing that rewrite:

- **It is not universal.** Every CSS math function is repaired _except_ `abs()` and `sign()` —
  measured one at a time across `calc min max clamp round mod rem hypot pow sqrt log exp sin cos
tan asin acos atan atan2` — so `abs(1px+2px)` is dropped where `min(1px+2px,9rem)` is repaired
  and computes. An earlier list held only the six that had come up in review, and condemned
  `calc(1px*pow(1+1,2))`, which computes to 4px. Raised by Hicks.
- **It is case-sensitive.** `CALC(1px+2px)` is emitted exactly as written and rejected, so the
  element keeps no size at all. Matching case-insensitively invented a valid 3px square and
  excused it. Also raised by Hicks.
- **It is scoped to the _nearest_ enclosing function, so nesting decides the outcome.**
  `abs(calc(1px+2px))` is repaired on the inside and computes to 3px, while `calc(abs(1px+2px))` is
  not and drops. Classifying by the outermost call got both of those backwards.
- **It applies to `/` and `*` too, which is what makes a comment.** `calc(1px/*)` is emitted as
  `calc(1px / *)` and opens no comment at all, while `abs(1px/*)` and a bare `1px/*` really do open
  one. No amount of scanning the class _as written_ can tell those apart.
- **A bare grouping paren is transparent and inherits whatever encloses it.**
  `calc((16px+16px))` is repaired to `calc((16px + 16px))` and computes to 32px, while
  `abs((1px+2px))` is emitted verbatim and dropped. A group is therefore neither repairable nor
  unrepairable on its own — it has to ask its parent, and treating it as unnamed condemned a value
  the browser computes. Raised by Vasquez.

So the rule computes the repaired value first and asks every later question of that. Raised by
Hicks, whose three separate findings shared this single cause.

Which operators get spaced was derived by hand from measured examples three times, and was wrong
all three times. Each version agreed with every case anyone had thought to try and still failed in
general; the last of them — "an operator is spaced unless an operator, `(` or `,` precedes it" —
spaced `calc((e+pi)*1px)`, which Tailwind emits glued and Chromium rejects outright, so the rule was
excusing a zero-height element as a circle. Raised by Hicks, after two reviewers had independently
confirmed the model as correct.

The distinction all three versions missed is that spacing turns on _dimensions_, not on operands.
`1px` and `50%` are numeric tokens; `e` and `pi` are bare identifiers; and a sign between two
identifiers is left alone. That is not something the examples were ever going to reveal, because
nobody writes `(e+pi)` by accident.

So the model is no longer derived. `repairedValue` is a transcription of the emitter's behaviour,
and `src/test/eslint-rules/repairedValue.test.ts` compiles seventy-odd values with the real Tailwind
and asserts the reproduction is character-identical to what comes out. A pin can only ever be as
good as the case someone imagined; that test fails the moment the two disagree, including when
Tailwind itself changes. It is also the only thing that can catch the parts of the model no verdict
distinguishes — deleting comma spacing turns nine of its cases red and leaves every rule pin green.

Three things about how that test is written are load-bearing, and all three were holes Vasquez found
in the first version of it. Values are handed to the rule exactly as they appear inside the brackets
of a class name, underscores and all, because feeding the rule a pre-spaced copy while feeding
Tailwind the escaped one retires the entire escape mechanism from the test — deleting the underscore
handling outright left every case green. A value Tailwind refuses to emit is asserted to be
unemitted in a separate list rather than skipped, because an early `return` inside `it.each` is
reported as a pass, so a case that quietly stopped emitting would look pinned while asserting
nothing. And the emitted declaration is delimited by the _last_ semicolon in the sheet rather than
matched with `[^;]+`, which truncates at the first one even inside a quoted string: `w-['a;b']`
really does emit `width: 'a;b';`, and the naive read compares it against `'a`.

Reaching for a comment-aware scanner there instead was a wrong turn worth recording, because it
came from blurring two questions. `calc(1px+2px;3px)` is emitted whole, and a scanner that stops
where the _browser_ stops recovers `calc(1px + 2px` — then compares it against the `calc(1px + 2px;3px)`
the emitter actually wrote and calls the transcription broken. The harness has exactly one job: recover
the string Tailwind emitted. What the browser subsequently makes of that string is
`provablyInvalidValue`'s job, and mixing the two makes each answer the other's question. A `w-`
utility emits one declaration, so its terminator is the last semicolon, and that single rule is right
for a semicolon in a string (Vasquez) and for one inside a comment (Hicks) without knowing about
either.

The pieces of the transcription worth stating in prose, because each was a bug first:

- **A sign inside scientific notation is not an operator.** `calc(1e+2px+1px)` is emitted as
  `calc(1e+2px + 1px)` and computes to 101px. The exemption requires a digit before the `e`, because
  CSS also has `e` as a bare constant and there the sign _is_ an operator: `calc(1px*e+1px)` becomes
  `calc(1px * e + 1px)` and computes to 3.718px, while `calc(1px * e+1px)` is rejected.
- **The name in front of `(` is digits and lowercase letters, stopping at anything else.** All three
  halves of that bite. `foo-calc(1px/*)` _is_ repaired, because the scan stops at the hyphen and
  finds `calc` — reading the name as `foo-calc` left the comment intact and silently switched the
  check off for a live lozenge. `2calc(1px/*)` is _not_, because the scan takes the leading digit —
  reading it as `calc` dissolved a comment that really does swallow the following rule, and reported
  an element whose radius the browser had already discarded. And lowercase-only is what makes
  `CALC(1px+2px)` a non-call, emitted verbatim and dropped.
- **A unary sign after `(` or `,` is left alone**, and only the first operator of a run is spaced:
  `calc(+1px)`, `min(1px,+2px)`, `calc(1px++2px)` → `calc(1px + +2px)`.
- **Every comma inside a repairable call is followed by a space.**
- **Which `_` becomes a space is decided by the parse, not by the text.** This one was a text
  substitution until Hicks measured `calc(var(--foo_bar)+1px)` and `url(foo_bar)` and found both
  wrong. A `url()` keeps every underscore in its whole subtree, because a URL is opaque; a `var()`
  or `theme()` keeps them only in its first node, and only if that node is the property name, so
  `var(--x_y,2px_3px)` becomes `var(--x_y,2px 3px)`. Both exemptions also match a suffix, so
  `my_var(` inherits. And the parser's separators are `: , = > < /` and whitespace — `+`, `-` and
  `*` are not among them, so `calc(1px+var(--x_y,2px))` is a single call _named_ `1px+var`, which is
  neither `var` nor a `_var` suffix and therefore loses the exemption that the very same `var()`
  keeps when written first. Nobody derives that; it is read off the parser.

Where repair matters most is that it decides whether a comment exists at all. `calc(_/*2)` is
emitted as `calc( /*2)` and `min(1px,_/*2)` as `min(1px, /*2)`, both keeping a comment that escapes
into the stylesheet, while `calc(1px/*2)` becomes `calc(1px / *2)` and keeps none.

Once repair has run, two multiplicative operators in a row are what a comment degenerates into, and
the second has no operand: `calc(1px / **\/+2px)` and `calc(1px * /2)` are both dropped. This is
also why repair has to run **before** comments are stripped rather than after. Stripping first reads
`calc(1px/**\/+2px)` as the perfectly good sum `calc(1px + 2px)` and excuses a real lozenge, when
what the browser receives holds no comment at all and does not parse. Raised by Hicks.

Where the parens do not balance, nothing is read and the element is skipped. Tailwind does close
them, but not consistently: `calc((1px+2px)` becomes a valid `calc((1px + 2px))` computing to 3px,
while `calc(1px+2px` becomes `calc()1px+2px` and drops. Those differ by a single inner paren and
point opposite ways, so the class cannot be read either way, and skipping is the direction that
cannot report a circle. Raised by Hicks.

That balance is counted outside CSS strings only. A paren inside quotes is a character, not
structure, so `before:content-['(']` was silently skipping every element that carried it — a
decorative pseudo-element switching off the whole check for its host. Also raised by Hicks.

Every reader asks its question of the repaired value, not only the one that decides validity.
Comparability was still reading the class as written, so `calc(1px*pow(1+1,2))` — which reaches the
browser as `calc(1px * pow(1 + 1, 2))` and computes to 4px — would not collapse, two identical axes
stopped proving a square, and a real circle was reported. The bug was not in the comparison; it was
in reading unrepaired text, which is the same mistake in a third place.

Two parts of the rule are deliberately kept though no verdict can distinguish them, and are listed
here rather than quietly left in:

- One null guard in the repair loop cannot fire, because the accumulated output always holds the
  function's own open paren. It is a crash guard rather than behaviour, and is kept on that basis.
- The juxtaposition check condemns multi-value lists such as `rounded-[10px_20px]`. Valid single
  lengths never place a space between an operand and a digit without a delimiter, so it condemns no
  valid expression — it over-excuses, which is the safe direction.

Two things previously listed here have since been removed from the list, and the reason is worth
keeping. Comma spacing was described as unfalsifiable; it is falsified nine times over by the
emission test, which did not exist when that claim was made. The trailing digit in the function-name
scan was described as unreachable, on the grounds that `atan2` returns an angle and can never form a
length; Hicks then constructed `calc(tan(abs(atan2(1+1,1)))*10px)`, which computes to 20px and
reaches it exactly. Both claims were mine, both were made in good faith, and both were wrong — an
argument that something cannot be tested is a weaker claim than it sounds, and is worth one more
attempt at a test before it is written down.

Unreachable _behaviour_ is still deleted rather than documented — two dead inertness re-derivations
went that way after a throw probe proved no pin and no file reached them. The distinction is between
code that can never change an answer and code that exists so a future change cannot crash.

What repair cannot do bounds the amnesty. It cannot supply a missing operand, so `min(1px + , 2px)`
and `abs(1px + )` are dropped exactly as `calc(1px + )` is. It cannot turn a lone `.` into a number,
so `calc(1px+.)` is dropped while `calc(1px+.5px)` is not. And it cannot insert a _missing operator_:
`calc(1px 2px)` is two operands and no expression, which is also what a comment leaves behind, since
`calc(1/**\/0px)` emits `calc(1 / **\/0px)` and is dropped. A comma is an argument separator and not
a juxtaposition, so `min(1px, 2px)` stays excused.

Both sides of that reading have to agree about `_`, Tailwind's space escape. It must count as
whitespace _between_ operands, and it must be kept out of the operand class as well: `\w` includes
`_`, so a check written with `[\w%)]` read the idiomatic `size-[abs(1px_+_2px)]` as glued and
condemned a value that computes to 3px. Raised indirectly by Vasquez, whose own claim about `abs()`
was wrong in both directions but whose insistence on measuring it found the regression — twice, in
two different rounds, at two different sites.

A sign is not automatically a negative. `-0px` computes to exactly `0px`, indistinguishable from
`0`, so the drop gate tests the magnitude rather than the presence of the sign; a 0×0 box is still
square. Raised by Hicks.

The comment rule is likewise not a simple search for `/*`. Beyond the repair above, a `/*` inside a
quoted arbitrary value is two characters of a CSS string, so `before:content-['/*']` opens nothing
and must not silence the lozenge beside it; but once a comment _is_ open the tokenizer stops
recognising quotes, so a later `'*/'` in a different class really does close it and swallow
everything between. Modelling that exactly would need emission order, which Tailwind does not
promise. The rule instead requires every comment to be wholly contained in a single class, and skips
the element otherwise — the same fail-safe direction as the unterminated case. Raised by Hicks. An
_escaped_ quote is not a quote: `\"` opens no string, so the `/*` behind it starts a real comment,
and a stylesheet containing `width: \"/*;` parses one rule where it should parse two, the next rule
swallowed entirely. Raised by Vasquez, with the swallowed-rule measured rather than argued. Comment
handling also has to survive the _read_: stripping a comment leaves a space behind, and `[/**\/-2px]`
is the same declaration as `[-2px]` — both invalid on an axis, both dropped — so the negative-drop
gate skips leading whitespace rather than anchoring hard at the start. Raised by Bishop.

The comment check does not ask whether the class is a utility Tailwind recognises, and that is a
deliberate over-excuse rather than an oversight. `unknown-[1px/*]` compiles to nothing, so its
comment cannot escape and the element could safely be judged; suppressing it anyway costs a missed
lozenge, which is the affordable direction, since this check can only suppress a report and never
manufacture one. The alternative is a list of recognised utility prefixes, which means Tailwind's
whole namespace plus whatever any plugin contributes — a second source of truth that drifts, and
drifts dangerously: the day it omits a real utility, the rule ignores a comment that genuinely
escapes, the browser swallows the radius, and a flat-cornered element is reported as a bubble.
Raised by Hicks; left as-is on Vasquez's and Bishop's concurrence, having put it to them both rather
than deciding it here.

Inertness is recursive, and recurses _through_ a negation as well as into one. Tailwind has no
double negation at all, so `not-not-hover:` and `not-not-starting:` emit nothing at any depth; and
`not-group-print:`, `has-not-starting:` and `group-not-not-hover:` emit nothing either, so a radius
carried by any of them never reaches the page. Reading `not-` only once treated the second as an
ordinary variant name and reported a radius that does not exist. Raised by Hicks.

There is exactly one inertness predicate, and exactly one gate that consults it. An earlier version
re-derived the same split inside the specificity weigher, one level deep, in three branches — which
is precisely how it came to miss the wrapped spellings above. Making each branch throw showed them
unreachable: the drop gate runs strictly earlier at both entry points, and neither the pins nor any
file in the repository could reach any of them. They are deleted rather than left as a second,
unpinned answer that can drift from the first. Consolidation asked for by Vasquez and Bishop, who
disagreed about whether it blocked the PR; measuring reachability settled it without either of them
having to be overruled. Hicks then found the first pass had removed only one of the three, which is
its own argument for deleting all of them rather than trusting a reading.

A variant that compiles to nothing is dropped outright rather than merely left unranked. Leaving it
unranked stopped it excusing but not being _reported_, so a dead `not-starting:rounded-full` on a
16×32 box still fired at a radius that never reaches the page; and the symmetry matters in the other
direction too, since a dead `not-starting:w-8 not-starting:h-8` pair must not excuse a box that is
really 16×32. Raised by Hicks, whose objection that unranked was a half-measure is accepted in full.
Which bracketed at-rules can be negated is a whitelist — `@media`, `@supports`, `@container` — and
not an enumeration of the dead ones, because guessing wrong on an unknown at-rule costs a false
report in one direction and only a missed one in the other.

Variants are split on **top-level** colons only, with brackets, parentheses, braces and quotes
tracked, so `data-[state=open]:`, `[&[data-state=open]]:`, `supports-[display:grid]:`,
`group-hover/item:` and `@max-md:` all tokenize correctly. A leading or trailing `!` is stripped
from the utility and recorded as importance, which the cascade model then honours.
An earlier regex-based split silently failed on any variant containing a bracketed colon, which
meant those radii were never examined at all.

### Options

```js
'local/pf-no-oversized-radius': ['error', { maxPx: 8, checkFullRound: true }]
```

| Option           | Default | Meaning                                                                         |
| ---------------- | ------- | ------------------------------------------------------------------------------- |
| `maxPx`          | `8`     | Largest permitted radius in px. `8` is the documented `--pf-radius-lg` ceiling. |
| `checkFullRound` | `true`  | Also report non-circular `rounded-full`. Only an explicit `false` disables it.  |

### Current configuration

One tier, repo-wide:

```js
'local/pf-no-oversized-radius': ['error', { maxPx: 8, checkFullRound: true }]
```

That is the documented rule with nothing grandfathered. It was previously split in two —
`maxPx: 12, checkFullRound: false` repo-wide, with the strict settings scoped to
`features/admin`, `features/settings` and `design-system` — because switching on strict mode
across the whole app would have emitted ~170 reports at once and buried the signal. #1022
worked that backlog down to zero (`rounded-xl` → `rounded-lg`, and every `rounded-full` site
adjudicated as either genuinely circular, a sanctioned pill, or a flattened rectangle), so the
scoped override is gone.

The lint baseline is zero errors _and_ zero warnings. Keep it that way: a new report here means
a new design decision, not a backlog item.

### Reading class names

Class strings are collected recursively, so the rule sees radii inside `clsx()`/`cn()` calls,
nested arrays and object keys, template literal quasis, and plain `className` strings.
Responsive and state variants are stripped before matching, so `md:rounded-2xl` and
`hover:rounded-2xl` are caught. Side-specific utilities are handled explicitly — `rounded-l`
is the left side, not a size.

Arbitrary values in units the rule cannot resolve statically (e.g. `rounded-[var(--x)]`) are
left alone rather than guessed at.

Collection is _rooted at the `className`/`class` attribute_: the rule walks down from the
attribute value, not up from a radius token. A class string assigned to a standalone
constant and applied indirectly — `const PANEL = 'rounded-2xl …'` used as
`className={PANEL}` — is therefore never seen, because resolving it needs dataflow the rule
deliberately does not do. Three such sites existed when #1022 landed and were flattened by
hand; a `rounded-2xl`/`rounded-3xl` grep is the way to find any more. Raised by Bishop.
