import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';
import { describe, expect, it } from 'vitest';

const TEST_DIR = dirname(fileURLToPath(import.meta.url));
const REACT_APP_ROOT = resolve(TEST_DIR, '../../..');

// ---------------------------------------------------------------------------
// #1102 source-level guard.
//
// The built-stylesheet assertions above prove the component-layer defaults sit
// where they should. They do NOT prove the variant map stopped declaring paint
// as a utility, because a re-added `bg-transparent` lands in @layer utilities
// and leaves every component-layer rule untouched — the checks above stay green
// while the defect is fully restored. This was verified by injection: adding
// `bg-transparent` back to `subtle` left all four assertions passing even
// though the built artifact then sorted `.bg-transparent` 19,791 bytes after
// `.bg-pf-accent`, i.e. the original bug exactly.
//
// So assert the source contract directly: the bare variants own no background
// or shadow utility in any state.
// ---------------------------------------------------------------------------
const BUTTON_SOURCE = resolve(REACT_APP_ROOT, 'src/common/components/ui/Button.tsx');
const BARE_VARIANTS = ['ghost', 'subtle', 'tab', 'toggle', 'link'] as const;

function readVariantClasses(source: string): Map<string, string> {
  const start = source.indexOf('const variantClasses');
  if (start < 0) throw new Error('variantClasses declaration not found in Button.tsx');
  const end = source.indexOf('\n};', start);
  if (end < 0) throw new Error('variantClasses closing brace not found in Button.tsx');
  const block = source.slice(start, end);
  const entries = new Map<string, string>();
  for (const match of block.matchAll(/^\s*(\w+):\s*'([^']*)'/gm)) {
    entries.set(match[1], match[2]);
  }
  return entries;
}

/**
 * Utilities that paint a property the component-layer default owns.
 *
 * The built base rule declares `color`, `box-shadow`, `background-color` and
 * `border-color`, so a re-add of any of those four reproduces #1102 — not just
 * a background. Matching `bg-`/`shadow-` alone left `text-pf-*`, `border-pf-*`
 * and `ring-pf-*` able to suppress caller paint with the guard still green.
 *
 * Colour-capable prefixes are shared with purely structural utilities
 * (`border-b-2`, `text-left`, `ring-0`), so a bare prefix match would be a
 * false positive. Require evidence the token is actually a colour: a `pf-`
 * design token, an arbitrary value, a CSS-wide colour keyword, or a Tailwind
 * palette shade. The last two matter because `text-white` is already used by
 * the `success` variant, so copying it onto a bare variant is plausible and
 * would otherwise reproduce #1102 with this guard green.
 */
const COLOUR_CAPABLE =
  /^(?:text|border|ring|outline|divide|from|via|to|fill|stroke|accent|caret|decoration)-/;

/** `white`, `black`, `transparent`, `current`, `inherit`. */
const NAMED_COLOUR = /^(?:white|black|transparent|current|inherit)$/;

/**
 * A Tailwind palette shade such as `red-500`. Anchored on a known hue list so
 * that structural tokens sharing the shape — `border-b-2` reduces to `b-2` —
 * cannot match.
 */
const PALETTE_SHADE =
  /^(?:slate|gray|grey|zinc|neutral|stone|red|orange|amber|yellow|lime|green|emerald|teal|cyan|sky|blue|indigo|violet|purple|fuchsia|pink|rose)-\d{2,3}$/;

/**
 * Strip Tailwind's important marker. Both spellings are live in this repo —
 * v3's leading `!bg-transparent` (40+ sites) and v4's trailing `justify-start!`
 * (71 sites) — and each defeats a *different* check: a leading `!` breaks
 * `startsWith('bg-')`, a trailing one breaks the `$`-anchored value regexes.
 *
 * An important paint utility is strictly worse than the #1102 defect it would
 * hide: `!important` in @layer utilities beats the @layer components default
 * unconditionally, so it defeats the core fix itself rather than merely winning
 * on source order.
 *
 * `!+` rather than `!` because `text-white!!` is accepted by the compiler.
 */
const stripImportant = (utility: string): string => utility.replace(/^!+/, '').replace(/!+$/, '');

/**
 * Reduce a class token to its bare utility: drop every state variant, then the
 * important marker.
 *
 * The variant separator must be found at bracket depth 0. A naive
 * `lastIndexOf(':')` lands *inside* an arbitrary value — `text-[color:var(--x)]`
 * reduces to `var(--x)]`, which matches nothing and silently reports a paint
 * utility as safe. `Button.tsx` already ships `bg-[var(--pf-button-primary-bg)]`,
 * so that shape is one edit away from being live.
 */
const bareUtility = (token: string): string => {
  let depth = 0;
  let cut = -1;
  for (let i = 0; i < token.length; i += 1) {
    const ch = token[i];
    if (ch === '[') depth += 1;
    else if (ch === ']') depth -= 1;
    else if (ch === ':' && depth === 0) cut = i;
  }
  return stripImportant(token.slice(cut + 1));
};

const classTokens = (classes: string): string[] => classes.split(/\s+/).filter(Boolean);

function paintUtilities(classes: string): string[] {
  return classTokens(classes).filter((token) => {
    const utility = bareUtility(token);
    if (utility === 'shadow') return true;
    if (utility.startsWith('bg-') || utility.startsWith('shadow-')) return true;
    if (!COLOUR_CAPABLE.test(utility)) return false;
    const value = utility.slice(utility.indexOf('-') + 1).split('/')[0];
    return (
      utility.includes('pf-') ||
      utility.includes('[') ||
      NAMED_COLOUR.test(value) ||
      PALETTE_SHADE.test(value)
    );
  });
}

describe('Button variant map — bare variants own no paint (#1102)', () => {
  const entries = readVariantClasses(readFileSync(BUTTON_SOURCE, 'utf8'));

  it('parses every bare variant out of the source', () => {
    for (const variant of BARE_VARIANTS) {
      expect(entries.has(variant), `variant '${variant}' missing from variantClasses`).toBe(true);
    }
  });

  it.each(BARE_VARIANTS)('%s declares no paint utility in any state', (variant) => {
    const offending = paintUtilities(entries.get(variant) ?? '');
    expect(
      offending,
      `variant '${variant}' declares ${offending.join(', ')} as a utility. Utilities land in ` +
        '@layer utilities, the same layer as caller className, so they suppress caller paint ' +
        'on source order. Move the default into @layer components in styles/controls.css, ' +
        "keyed off [data-pf-variant='" + variant + "'].",
    ).toEqual([]);
  });

  it('still allows structural, non-paint utilities', () => {
    expect(paintUtilities(entries.get('tab') ?? '')).toEqual([]);
    expect(entries.get('tab')).toContain('border-b-2');
    expect(entries.get('link')).toContain('px-0');

    // Structural utilities that share a colour-capable prefix must not trip it.
    expect(
      paintUtilities('border border-b-2 ring-0 px-0 text-left text-sm underline rounded-none'),
    ).toEqual([]);
  });

  it('flags every property the component-layer default owns, in any state', () => {
    // background-color, box-shadow, color, border-color — plus prefix forms.
    expect(paintUtilities('bg-pf-accent')).toEqual(['bg-pf-accent']);
    expect(paintUtilities('shadow')).toEqual(['shadow']);
    expect(paintUtilities('shadow-xs')).toEqual(['shadow-xs']);
    expect(paintUtilities('text-pf-accent')).toEqual(['text-pf-accent']);
    expect(paintUtilities('border-pf-accent')).toEqual(['border-pf-accent']);
    expect(paintUtilities('border-l-pf-accent')).toEqual(['border-l-pf-accent']);
    expect(paintUtilities('ring-pf-accent')).toEqual(['ring-pf-accent']);
    expect(paintUtilities('text-[var(--pf-text-inverse)]')).toEqual([
      'text-[var(--pf-text-inverse)]',
    ]);
    expect(paintUtilities('enabled:hover:text-pf-accent')).toEqual(['enabled:hover:text-pf-accent']);
    expect(paintUtilities('dark:focus:border-pf-accent')).toEqual(['dark:focus:border-pf-accent']);
  });

  /**
   * Regression: the `$`-anchored value regexes above (NAMED_COLOUR,
   * PALETTE_SHADE) once missed a trailing `!`, so `text-white!` passed while
   * `text-white` failed. Both important spellings are live in this repo, and an
   * important paint utility is worse than plain #1102 — it beats caller paint
   * unconditionally rather than only on source order.
   */
  it('sees paint through both Tailwind important spellings', () => {
    // Trailing `!` (v4) — the form that slipped past the value regexes.
    expect(paintUtilities('text-white!')).toEqual(['text-white!']);
    expect(paintUtilities('text-black!')).toEqual(['text-black!']);
    expect(paintUtilities('text-red-500!')).toEqual(['text-red-500!']);
    expect(paintUtilities('border-red-500!')).toEqual(['border-red-500!']);
    expect(paintUtilities('ring-white!')).toEqual(['ring-white!']);
    expect(paintUtilities('enabled:hover:text-white!')).toEqual(['enabled:hover:text-white!']);

    // Leading `!` (v3) — breaks the prefix checks instead of the value regexes.
    expect(paintUtilities('!bg-transparent')).toEqual(['!bg-transparent']);
    expect(paintUtilities('!shadow-md')).toEqual(['!shadow-md']);
    expect(paintUtilities('!text-pf-accent')).toEqual(['!text-pf-accent']);
    expect(paintUtilities('dark:!border-pf-accent')).toEqual(['dark:!border-pf-accent']);

    // Structural utilities stay exempt under either spelling.
    expect(paintUtilities('!p-2 !h-auto justify-start! rounded-none!')).toEqual([]);
  });
});

/**
 * #1102 caller contract, second direction: a disabled control must not repaint
 * under the pointer.
 *
 * A plain `hover:` utility matches on `:disabled`. Before the components-layer
 * move, the variant's own `enabled:hover:bg-*` sat later in @layer utilities and
 * overrode the caller, so a caller's unguarded `hover:` never actually painted
 * and the defect was invisible. Freeing caller paint is what lets it through,
 * which makes it this change's fallout rather than a pre-existing caller bug.
 *
 * Scope is every call site this cluster unmasked, found by walking the tree
 * rather than by listing files. An earlier version enumerated nine files by
 * hand; a repo-wide sweep then found ten more offending sites in files that
 * list did not mention, so the list was not a deliberate scope boundary — it
 * was the limit of what had been looked at.
 *
 * `unstyled` is deliberately excluded. It declares no paint and has no
 * components-layer default, so its callers were always in full control and
 * nothing about them changed here. Its unguarded hovers are pre-existing and
 * belong to their owners.
 */
describe('Button caller contract — unmasked callers gate hover on :enabled (#1102)', () => {
  const SRC_ROOT = resolve(REACT_APP_ROOT, 'src');

  /**
   * Variants whose paint this cluster moved into the components layer, and
   * whose callers are therefore newly able to paint. `unstyled` is not one of
   * them — see the note above.
   */
  const UNMASKED_VARIANTS = ['ghost', 'subtle', 'tab', 'toggle', 'link'];

  /**
   * Read the `variant` prop only. Matching bare quoted words anywhere in the
   * tag is wrong: `role="tab"` and `key={tab.name}` both produce a false
   * `tab` hit, which is how a genuinely `unstyled` control got miscounted as
   * fallout during this work. The AST reads the attribute by name, so the
   * confusion is structurally impossible rather than merely guarded against.
   */
  const sourceFiles = (dir: string, acc: string[] = []): string[] => {
    for (const entry of readdirSync(dir)) {
      const full = join(dir, entry);
      if (statSync(full).isDirectory()) sourceFiles(full, acc);
      // Test files are excluded from the caller contracts: their `<Button>`s are
      // fixtures asserting class passthrough, not rendered UI, and holding a
      // fixture to an affordance rule that exists for users is a false positive.
      // `Button.test.tsx` deliberately renders a bare variant with a resting fill
      // and no hover to prove the caller's classes survive verbatim.
      else if (/\.(tsx|jsx)$/.test(entry) && !/\.(test|spec)\.(tsx|jsx)$/.test(entry)) {
        acc.push(full);
      }
    }
    return acc;
  };

  /**
   * Every `<Button>` element in a file, parsed with the TypeScript compiler.
   *
   * Three hand-rolled parsers preceded this one and each shipped a different
   * tokenisation bug: a tag terminated at the first `>` inside an arrow
   * function; a `className` reader that returned raw template text (so an
   * interpolated `${cond ? 'text-pf-error' : ''}` arrived as `"text-pf-error`
   * with a quote glued on and matched nothing); and a variant reader that could
   * not tell `role="tab"` from `variant="tab"`. The compiler already answers all
   * of these exactly, and `typescript` is already a dependency.
   *
   * Own-versus-nested falls out of the AST for free: `node.attributes` are the
   * Button's own props, so `iconCenter={<Icon className="text-pf-error" />}`
   * belongs to the icon and is never attributed to the Button.
   */
  /**
   * One reachable state of an expression: the class string it yields, plus the
   * ternary conditions (keyed by source text) that select it. The conditions
   * exist so a className arm can be paired with the `variant` arm chosen by the
   * same condition; see `branchesOf`.
   */
  interface Branch {
    text: string;
    conds: Map<string, boolean>;
  }

  interface ButtonTag {
    line: number;
    className: string;
  /**
   * The className split into mutually exclusive alternatives, one per branch of
   * any ternary or `&&` inside it. `className` unions every branch, which is
   * what the paint contracts want -- a token is present in the DOM if any branch
   * emits it. The hover contract needs the opposite: `isLast ? 'text-pf-text-primary'
   * : 'text-pf-text-secondary hover:text-pf-text-primary'` looks self-referential
   * only when the two branches are conflated, and is correct when they are not.
   */
  branches: Branch[];
    variants: string[];
    /** The `variant` prop expanded the same way, for condition-aware pairing. */
    variantBranches: Branch[];
    disableable: boolean;
    dynamicVariant: boolean;
  }

  /**
   * Every string the expression can contribute, including the literal segments
   * of a template. `class={`a ${cond ? 'b' : 'c'}`}` yields `a`, `b`, `c` — the
   * union of what can render, which is the right conservative reading for a
   * guard.
   */
  const literalsIn = (node: ts.Node): string[] => {
    const out: string[] = [];
    const visit = (n: ts.Node): void => {
      if (ts.isStringLiteral(n) || ts.isNoSubstitutionTemplateLiteral(n)) out.push(n.text);
      else if (ts.isTemplateHead(n) || ts.isTemplateMiddle(n) || ts.isTemplateTail(n)) out.push(n.text);
      // The callback must return void: `ts.forEachChild` treats any truthy
      // return as "stop", which silently drops the rest of the tree.
      ts.forEachChild(n, visit);
    };
    visit(node);
    return out;
  };

  /**
   * Expands a className expression into one branch per reachable state. Each
   * branch carries the ternary conditions that select it, keyed by condition
   * source text, so a className arm can be paired with the `variant` arm chosen
   * by the same condition. Without that pairing a site like
   * `variant={sel ? 'primary' : 'subtle'} className={sel ? 'bg-x' : 'hover:bg-y'}`
   * looks like a bare variant carrying `bg-x`, when in fact `bg-x` only ever
   * coexists with `primary`, whose paint still masks it.
   *
   * Unknown shapes fall back to every literal they contain, which is the
   * conservative answer: it can only merge branches, never invent one.
   *
   * The alternative cap keeps a className with many independent ternaries from
   * expanding combinatorially. It *throws* rather than degrading to a conflated
   * string: conflation is not conservative here, because a dead hover in one arm
   * hiding behind a working hover in another is exactly the defect this guard
   * exists to catch. A silent false negative there would make the guard green for
   * the reason it should be red. Measured maximum across the repo is 8
   * alternatives over 464 Button call sites, so the cap has never been reached;
   * if it ever is, that is a fact worth surfacing rather than absorbing.
   */
  const MAX_ALTERNATIVES = 64;
  const WHOLE_TREE_TIMEOUT_MS = 20_000;

  const branchesOf = (node: ts.Node): Branch[] => {
    const merge = (a: Branch, b: Branch, sep: string): Branch | null => {
      const conds = new Map(a.conds);
      for (const [key, value] of b.conds) {
        // Two arms of the same condition never coexist, so the combination is
        // unreachable and must not be emitted as a state.
        if (conds.has(key) && conds.get(key) !== value) return null;
        conds.set(key, value);
      }
      return { text: `${a.text}${sep}${b.text}`, conds };
    };

    const cross = (acc: Branch[], alts: Branch[], sep = ' ') => {
      const out = acc.flatMap((a) => alts.map((b) => merge(a, b, sep))).filter((b) => b !== null);
      if (out.length > MAX_ALTERNATIVES) {
        throw new Error(
          `className expands to ${out.length} mutually exclusive alternatives, past the ` +
            `${MAX_ALTERNATIVES} cap. The only way to continue is to conflate the arms, which ` +
            `would let a dead hover in one arm hide behind a working hover in another -- the ` +
            `exact defect this guard exists to catch. Failing loudly instead. Split the ` +
            `className, or raise MAX_ALTERNATIVES if the expansion is genuinely needed.`,
        );
      }
      return out;
    };

    const plain = (text: string): Branch[] => [{ text, conds: new Map() }];

    if (ts.isParenthesizedExpression(node)) return branchesOf(node.expression);
    if (ts.isJsxExpression(node)) return node.expression ? branchesOf(node.expression) : plain('');
    if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) {
      return plain(node.text);
    }
    if (ts.isConditionalExpression(node)) {
      const key = node.condition.getText();
      const tag = (branches: Branch[], value: boolean) =>
        branches.map((b) => {
          if (b.conds.get(key) === !value) return null;
          const conds = new Map(b.conds);
          conds.set(key, value);
          return { text: b.text, conds };
        });
      return [
        ...tag(branchesOf(node.whenTrue), true),
        ...tag(branchesOf(node.whenFalse), false),
      ].filter((b) => b !== null);
    }
    // `cond && 'x'` contributes 'x' or nothing; both are real states. Other
    // binary operators are NOT alternatives of that shape: `a + b` concatenates
    // into one class string, so both operands are present at once, and `||`/`??`
    // select between two operands where the left one is a real candidate.
    // Treating every operator as `&&` silently discarded the left operand, which
    // hid a self-reference spelled `{'text-pf-accent ' + 'hover:text-pf-accent'}`.
    if (ts.isBinaryExpression(node)) {
      const op = node.operatorToken.kind;
      if (op === ts.SyntaxKind.PlusToken) {
        return cross(branchesOf(node.left), branchesOf(node.right), '');
      }
      if (op === ts.SyntaxKind.AmpersandAmpersandToken) {
        return [...plain(''), ...branchesOf(node.right)];
      }
      return [...branchesOf(node.left), ...branchesOf(node.right)];
    }
    if (ts.isTemplateExpression(node)) {
      let acc = plain(node.head.text);
      for (const span of node.templateSpans) {
        acc = cross(acc, branchesOf(span.expression));
        acc = acc.map((b) => ({ ...b, text: `${b.text} ${span.literal.text}` }));
      }
      return acc;
    }
    if (ts.isCallExpression(node)) {
      if (ts.isPropertyAccessExpression(node.expression) && node.expression.name.text === 'join') {
        const separatorNode = node.arguments[0];
        if (
          separatorNode &&
          !ts.isStringLiteral(separatorNode) &&
          !ts.isNoSubstitutionTemplateLiteral(separatorNode)
        ) {
          throw new Error('className uses an array join with a dynamic separator');
        }

        const separator = separatorNode?.text ?? ',';
        const arrayElements = (receiver: ts.Expression): readonly ts.Expression[] => {
          if (ts.isArrayLiteralExpression(receiver)) {
            if (receiver.elements.some(ts.isSpreadElement)) {
              throw new Error('className joins an array containing an unsupported spread element');
            }
            return receiver.elements;
          }
          if (
            ts.isCallExpression(receiver) &&
            ts.isPropertyAccessExpression(receiver.expression) &&
            receiver.expression.name.text === 'filter'
          ) {
            return arrayElements(receiver.expression.expression);
          }
          throw new Error('className uses join() on an unsupported receiver');
        };

        const elements = arrayElements(node.expression.expression);
        if (elements.length === 0) return plain('');

        let acc = branchesOf(elements[0]);
        for (const element of elements.slice(1)) {
          acc = cross(acc, branchesOf(element), separator);
        }
        return acc;
      }

      let acc = plain('');
      for (const arg of node.arguments) acc = cross(acc, branchesOf(arg));
      return acc;
    }
    return plain(literalsIn(node).join(' '));
  };

  const buttonTags = (source: string, fileName = 'f.tsx'): ButtonTag[] => {
    const sf = ts.createSourceFile(fileName, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
    const out: ButtonTag[] = [];

    const read = (node: ts.JsxOpeningElement | ts.JsxSelfClosingElement): void => {
      /**
       * The cap in `branchesOf` throws rather than conflating arms. Rethrow with
       * the site attached: a bare "past the 64 cap" tells you the guard tripped
       * but not where, and a failure you cannot locate is barely better than one
       * you cannot see.
       */
      const expand = (initializer: ts.Node, attribute: string, line: number): Branch[] => {
        try {
          return branchesOf(initializer);
        } catch (error) {
          throw new Error(
            `${fileName}:${line} \`${attribute}\` -- ${(error as Error).message}`,
            { cause: error },
          );
        }
      };

      const tag: ButtonTag = {
        line: sf.getLineAndCharacterOfPosition(node.getStart(sf)).line + 1,
        className: '',
        branches: [],
        variants: ['primary'],
        variantBranches: [{ text: 'primary', conds: new Map() }],
        disableable: false,
        dynamicVariant: false,
      };

      for (const attr of node.attributes.properties) {
        if (!ts.isJsxAttribute(attr)) continue;
        const name = attr.name.getText(sf);
        if (name === 'disabled' || name === 'loading') tag.disableable = true;
        else if (name === 'className' && attr.initializer) {
          tag.className = literalsIn(attr.initializer).join(' ');
          tag.branches = expand(attr.initializer, 'className', tag.line);
        } else if (name === 'variant' && attr.initializer) {
          const literals = literalsIn(attr.initializer);
          tag.variants = literals;
          tag.variantBranches = expand(attr.initializer, 'variant', tag.line);
          tag.dynamicVariant = literals.length === 0;
        }
      }

      out.push(tag);
    };

    const visit = (node: ts.Node): void => {
      if (
        (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) &&
        node.tagName.getText(sf) === 'Button'
      ) {
        read(node);
      }
      ts.forEachChild(node, visit);
    };

    visit(sf);
    return out;
  };

  /**
   * A variant this cluster unmasked. A `variant={expr}` that resolves to no
   * literal is treated as unmasked rather than skipped: it *may* be one of the
   * five, and a guard that silently ignores what it cannot resolve is the same
   * false-negative shape as the parsers this replaced.
   */
  const isUnmasked = (tag: ButtonTag): boolean =>
    tag.dynamicVariant || tag.variants.some((v) => UNMASKED_VARIANTS.includes(v));

  /**
   * The variant literals that can be selected in the state that emits this
   * className branch. When `variant` is a ternary on the same condition as the
   * className, only the arm chosen by that condition applies:
   * `variant={sel ? 'primary' : 'subtle'}` with an accent fill in the `sel` arm
   * is correct code -- the fill only ever renders under `primary`, whose own
   * paint still masks it -- and pairing it with `subtle` is a false positive.
   * An unresolvable `variant={expr}` yields `''`, which callers read as "may be
   * any variant" rather than skipping the tag.
   */
  const variantsFor = (tag: ButtonTag, branch: Branch): string[] => {
    const reachable = tag.variantBranches.filter((v) =>
      [...v.conds].every(
        ([key, value]) => !branch.conds.has(key) || branch.conds.get(key) === value,
      ),
    );
    const applicable = reachable.length > 0 ? reachable : tag.variantBranches;
    return applicable
      .map((v) => v.text.trim())
      .filter((v) => v === '' || UNMASKED_VARIANTS.includes(v));
  };

  /** Whether a bare variant is selected in the state that emits this branch. */
  const isUnmaskedIn = (tag: ButtonTag, branch: Branch): boolean =>
    variantsFor(tag, branch).length > 0;

  it('no unmasked-variant Button pairs `disabled` with an unguarded hover:', () => {
    const offenders: string[] = [];

    for (const file of sourceFiles(SRC_ROOT)) {
      const rel = relative(SRC_ROOT, file).replace(/\\/g, '/');
      for (const tag of buttonTags(readFileSync(file, 'utf8'), file)) {
        if (!tag.disableable || !unguardedHover(tag.className)) continue;
        if (!isUnmasked(tag)) continue;
        offenders.push(`${rel}:${tag.line}`);
      }
    }

    expect(
      offenders,
      'A plain `hover:` utility also matches :disabled. Now that caller paint is no ' +
        'longer overridden by the variant, these repaint under the pointer on a control ' +
        'the user cannot activate. Prefix the hover with `enabled:`.',
    ).toEqual([]);
  }, WHOLE_TREE_TIMEOUT_MS);

  /**
   * #1102 caller contract, third direction: the FOREGROUND channel.
   *
   * Every earlier framing of this cluster — the issue, the four merged PRs and
   * the first seven rounds of this branch — described the fallout as "caller
   * backgrounds now paint". The same cascade governs `color`. Pre-fix, `subtle`
   * emitted `text-pf-text-secondary` as a utility that outsorted a caller's
   * `text-pf-error`, so the caller's colour never painted and the site looked
   * fine. Freeing it is what makes the caller's own choice visible, and a fill
   * token is not a text colour: `--pf-error` is tuned to be painted ON, not to
   * be read against a page surface.
   *
   * Measured across all 8 palettes: `text-pf-error` on `bg-pf-panel` lands at
   * 4.41 rest / 4.06 hover in forge, and on `bg-pf-bg-1` at 4.22 — both below
   * AA. Retargeting to `--pf-error-text` lifts the worst palette to 7.52/7.82.
   *
   * `accent` is deliberately absent from the map: no palette defines
   * `--pf-accent-text`, so there is nothing to retarget to. Its remaining call
   * sites are left alone rather than pointed at a token that does not exist.
   * Their worst measured rest contrast is 4.65 — light, on `bg-pf-bg-0`, the
   * alternating-row surface at `SystemLogsContent.tsx:245`. That still passes
   * AA, but the margin is 0.15 rather than the 0.49 an earlier revision of this
   * comment claimed from a surface those sites do not actually sit on.
   *
   * The count is 6 sites / 7 tokens lexically, of which 5 are reachable at
   * runtime: `TagInput.tsx:291` is a ternary whose accent branch is gated on
   * the same condition that selects `variant='primary'`, so its accent spelling
   * never coexists with the bare variant. Two reviewers reported 6 and 5 and
   * both were right about different populations; the difference is recorded
   * rather than resolved because a future edit to that ternary makes 6 correct.
   *
   * Matching is per token via `bareUtility`, not by regex over the raw string:
   * that is what makes it see `!text-pf-error`, `text-pf-error!` and a token
   * arriving from inside a template interpolation.
   */
  it('no unmasked-variant Button uses a fill token as its foreground', () => {
    const RETARGET: Record<string, string> = {
      'text-pf-error': 'text-pf-error-text',
      'text-pf-warning': 'text-pf-warning-text',
      'text-pf-success': 'text-pf-success-text',
    };

    const offenders = new Set<string>();

    for (const file of sourceFiles(SRC_ROOT)) {
      const rel = relative(SRC_ROOT, file).replace(/\\/g, '/');
      for (const tag of buttonTags(readFileSync(file, 'utf8'), file)) {
        if (!isUnmasked(tag)) continue;
        // Every offending token, not just the first: a site typically carries
        // both a resting and a hover spelling, and reporting one hides the other.
        for (const utility of classTokens(tag.className).map(bareUtility)) {
          if (utility in RETARGET) {
            offenders.add(`${rel}:${tag.line} uses ${utility}, want ${RETARGET[utility]}`);
          }
        }
      }
    }

    expect(
      [...offenders],
      'A fill token used as `color` on a bare variant is no longer suppressed by the ' +
        'variant, so it now paints and is read directly against the page surface. ' +
        'Use the semantic text token instead — it is defined in all 8 palettes.',
    ).toEqual([]);
  }, WHOLE_TREE_TIMEOUT_MS);

  /**
   * SELF-REFERENTIAL HOVER (#1137, round 11).
   *
   * A caller on a bare variant that declares `hover:X` equal to its own resting
   * `X` renders identically in both states. Before #1102 that was invisible:
   * the variant's own paint outranked the caller at rest, so the user saw
   * variant-default -> caller-colour. Once the default moved to
   * `@layer components`, the caller wins at rest too and the hover becomes a
   * no-op. Sixteen sites carried this shape; all sixteen predate the cluster.
   *
   * Measured, not argued (8 palettes, real `Button`, transitions disabled):
   * none of the sixteen was dead in every palette, and an earlier revision of
   * this comment said they were. The components-layer default hover
   * (`controls.css:1699` for ghost, `:1754` for subtle) is still live for any
   * caller that passes no `bg-*`, so those sites keep a background affordance
   * and lose only their colour signal. Ghost's default is a translucent white
   * overlay and shows on every surface; subtle's is `--pf-bg-1` and is invisible
   * only where the surface already equals it. The measurement that produced the
   * false claim rendered two ghost sites as `subtle` on a `bg-pf-bg-1` surface,
   * which is the one combination where subtle's default is inert.
   *
   * The shape that IS dead in every palette is a caller that paints its own
   * background: that overrides the default hover in both states and leaves no
   * affordance at all. Both shapes are flagged, and the message distinguishes
   * them, because the remedies differ.
   *
   * Twelve of the sixteen already carried a `hover:bg-pf-*\/10` tint, so their
   * colour token was redundant; deleting it measured byte-identical in all 8
   * palettes.
   *
   * The remedy is a hover that changes something, not a hover that restates the
   * resting value. Note a tint in the *same hue as the text* erodes contrast:
   * `text-pf-accent` + `enabled:hover:bg-pf-accent/10` measured 4.36 on light,
   * below AA, which is why the accent site uses `enabled:hover:underline`
   * (8/8 feedback, 4.99 retained) rather than a tint.
   */
  const hoverTokens = (className: string) => {
    const rest = new Set<string>();
    const hover = new Set<string>();
    for (const token of classTokens(className)) {
      const bare = bareUtility(token);
      const stripped = token.replace(/!+$/, '');
      const prefix = stripped.endsWith(bare) ? stripped.slice(0, -bare.length) : '';
      (/(^|:)hover:/.test(prefix) ? hover : rest).add(bare);
    }
    return { rest, hover };
  };

  /**
   * The single property each unmasked variant's default hover sets, and whether
   * a caller can silence it.
   *
   * Four of the five default from the COMPONENTS layer in controls.css (`ghost`
   * :1699, `subtle` :1754, `tab` :1764, `toggle` :1786). Those are silenceable:
   * any caller utility declaring the same property at rest displaces them on
   * layer order, which is the whole of #1102.
   *
   * This is why "the caller painted its own background" is not by itself a
   * defect. A resting `bg-*` neutralises the default only for the two variants
   * whose default IS a background; `tab` and `toggle` hover by changing colour,
   * so they keep their affordance under any background the caller declares.
   * Measured: a `tab` with `bg-pf-accent/10` and no hover token still changed
   * its foreground in 8/8 palettes, while the same shape on `ghost` changed
   * nothing in 8/8.
   *
   * `link` is the exception, and it is NOT "no default hover" -- an earlier
   * revision of this table said that, and it was false. `link` ships
   * `enabled:hover:underline` from the VARIANT MAP in Button.tsx, so its default
   * is a text-decoration rather than a colour or a fill.
   *
   * It is silenced by a different mechanism from the other four. Those lose to a
   * caller utility on LAYER ORDER. `link`'s hover sits in the same layer as the
   * caller and sorts after it, so no caller class can displace it -- but a caller
   * that is ALREADY underlined at rest makes the hover a no-op by VALUE
   * EQUALITY. Measured on the real Button across 8 palettes:
   *   text-pf-accent                  -> decoration changed 8/8 (affordance intact)
   *   text-pf-accent no-underline     -> decoration changed 8/8 (hover still wins)
   *   text-pf-text-tertiary underline -> decoration changed 0/8 (dead)
   * The last is the live MeasurementOverlay.tsx:48 shape, which survives only
   * because it also changes colour on hover.
   */
  const DEFAULT_HOVER: Record<string, 'bg' | 'text' | 'deco'> = {
    ghost: 'bg',
    subtle: 'bg',
    tab: 'text',
    toggle: 'text',
    link: 'deco',
  };

  /**
   * The variants a branch leaves with no affordance at all: it declares no hover
   * of its own, and it silences its variant's default hover by declaring that
   * default's property at rest, from @layer utilities.
   *
   * Extracted so the regression tests exercise this decision directly rather
   * than a proxy for it. Asserting on the DEFAULT_HOVER table alone stayed green
   * when the `link` model was deliberately reverted, which is exactly the
   * "gate cannot see the problem" ambiguity these tests exist to remove.
   */
  const silencedVariants = (tag: ButtonTag, branch: Branch): string[] => {
    const { rest, hover } = hoverTokens(branch.text);
    // A hover token that differs from the resting value is real feedback,
    // whatever else the branch declares.
    if ([...hover].some((utility) => !rest.has(utility))) return [];

    // `text-*` is overloaded: `text-sm` is a size and `text-left` an
    // alignment. Only the palette spellings this repo uses for colour
    // count, because only those can silence a colour default hover.
    const declares = (kind: 'bg' | 'text' | 'deco') =>
      [...rest].some((utility) =>
        kind === 'bg'
          ? /^bg-/.test(utility)
          : kind === 'deco'
            ? utility === 'underline'
            : /^text-pf-/.test(utility) || /^text-\[/.test(utility),
      );

    return variantsFor(tag, branch).filter((variant) => {
      // An unresolvable `variant={expr}` could be any of the five. It is
      // only certainly dead when the caller silences every property the
      // five default-hover on, since a resolvable variant would silence
      // just one of them.
      if (variant === '') return declares('bg') && declares('text') && declares('deco');
      return declares(DEFAULT_HOVER[variant]);
    });
  };

  it('no unmasked-variant Button is left with a hover that changes nothing', () => {
    const offenders = new Set<string>();

    for (const file of sourceFiles(SRC_ROOT)) {
      const rel = relative(SRC_ROOT, file).replace(/\\/g, '/');
      for (const tag of buttonTags(readFileSync(file, 'utf8'), file)) {
        for (const branch of tag.branches) {
          if (!isUnmaskedIn(tag, branch)) continue;
          const { rest, hover } = hoverTokens(branch.text);
          // A hover token that differs from the resting value is real feedback,
          // whatever else the branch declares.
          if ([...hover].some((utility) => !rest.has(utility))) continue;
          // The caller has no hover of its own, so the only affordance left is
          // the variant default -- and the caller silences that default by
          // declaring the same property at rest, from @layer utilities.
          const silenced = silencedVariants(tag, branch);

          if (silenced.length > 0) {
            offenders.add(
              `${rel}:${tag.line} silences the ${silenced.join('/')} default hover and declares none`,
            );
          }
          for (const utility of [...hover].filter((u) => rest.has(u))) {
            offenders.add(`${rel}:${tag.line} hover:${utility} restates its resting value`);
          }
        }
      }
    }

    expect(
      [...offenders],
      'This Button has no hover token that changes anything. Two shapes reach here. ' +
        'A caller that declares at rest the one property its variant default hovers ' +
      '(background for ghost/subtle, colour for tab/toggle) silences that default ' +
      'from @layer utilities and is left with no affordance, so it must supply one. ' +
        '`link` is reported only when the caller is already underlined at rest, ' +
        'which makes its `enabled:hover:underline` a no-op by value equality. ' +
        'A caller that merely restates a colour ' +
        'still has its variant default, but that colour token is dead and should go. ' +
        'Prefer a contrast-neutral affordance -- a ring or an underline -- when the ' +
        'text and a candidate tint share a hue: `text-pf-accent` with ' +
        '`hover:bg-pf-accent/10` measured 4.36 on light, below AA.',
    ).toEqual([]);
  }, WHOLE_TREE_TIMEOUT_MS);

  it('reports a `link` caller only when it is underlined at rest', () => {
    // V1, round 12. An earlier revision modelled `link` as having no default
    // hover, so the predicate returned true unconditionally and idiomatic,
    // correct code was reported as dead. `link` in fact ships
    // `enabled:hover:underline` from the variant map.
    //
    // Measured on the real Button across 8 palettes -- see DEFAULT_HOVER.
    const [tag] = buttonTags('<Button variant="link" className="text-pf-accent">Docs</Button>');
    expect(isUnmaskedIn(tag, tag.branches[0]), '`link` is a bare variant').toBe(true);
    expect(silencedVariants(tag, tag.branches[0]), 'correct link code must not be reported').toEqual(
      [],
    );

    // A resting `no-underline` does NOT silence it: the hover sorts after the
    // plain utility in the same layer, so the underline still wins. 8/8.
    const [tag2] = buttonTags(
      '<Button variant="link" className="text-pf-accent no-underline">Docs</Button>',
    );
    expect(silencedVariants(tag2, tag2.branches[0])).toEqual([]);

    // A resting `underline` DOES silence it, by value equality rather than by
    // cascade -- the hover sets the value the element already has. Measured 0/8.
    const [tag3] = buttonTags(
      '<Button variant="link" className="text-pf-accent underline">Docs</Button>',
    );
    expect(silencedVariants(tag3, tag3.branches[0])).toEqual(['link']);

    // The contrast: `subtle`'s default lives in the components layer, so it is
    // silenced by layer order instead. Without this the test would pass on a
    // guard that reports nothing at all.
    const [tag4] = buttonTags('<Button variant="subtle" className="bg-pf-accent-bg">Docs</Button>');
    expect(silencedVariants(tag4, tag4.branches[0])).toEqual(['subtle']);
  });

  it('splits a className by operator, not by assuming every binary is `&&`', () => {
    const initializerOf = (code: string) => {
      const sf = ts.createSourceFile('f.tsx', code, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
      const statement = sf.statements[0] as ts.VariableStatement;
      return statement.declarationList.declarations[0].initializer!;
    };

    // `a + b` is ONE class string: both operands render together. Treating it as
    // an alternation dropped the left operand, so a self-reference split across
    // a concatenation was invisible. Falsified at +4 bytes -> GREEN before this.
    expect(
      branchesOf(initializerOf('const a = "text-pf-accent " + "hover:text-pf-accent";')).map(
        (b) => b.text,
      ),
    ).toEqual(['text-pf-accent hover:text-pf-accent']);

    // `??` and `||` select one operand or the other, so BOTH are real states.
    // The old code returned only the right one, which is why `x ?? ''` -- about
    // a dozen call sites in this tree -- could hide a defect in its left arm.
    expect(
      branchesOf(initializerOf('const a = "text-pf-error hover:text-pf-error" ?? "";')).map(
        (b) => b.text,
      ),
    ).toContain('text-pf-error hover:text-pf-error');
  });

  it('expands array alternatives before applying join', () => {
    const source = (receiverSuffix: string) =>
      [
        '<Button',
        '  variant="subtle"',
        '  className={[',
        "    active ? 'bg-pf-bg-1' : 'bg-pf-bg-1 enabled:hover:bg-pf-bg-2',",
        `  ]${receiverSuffix}.join(' ')}`,
        '/>',
      ].join('\n');

    for (const suffix of ['', '.filter(Boolean)']) {
      const [tag] = buttonTags(source(suffix), 'JoinedClasses.tsx');
      expect(tag.branches.map((branch) => branch.text)).toEqual([
        'bg-pf-bg-1',
        'bg-pf-bg-1 enabled:hover:bg-pf-bg-2',
      ]);
      expect(
        tag.branches.map((branch) => silencedVariants(tag, branch)),
        'the arm without its own hover must remain visible to the guard',
      ).toContainEqual(['subtle']);
    }

    expect(() =>
      buttonTags('<Button className={classes.map(String).join(" ")} />', 'DynamicJoin.tsx'),
    ).toThrow(/DynamicJoin\.tsx:1 `className` -- className uses join\(\) on an unsupported receiver/);
    expect(() =>
      buttonTags('<Button className={[...classes].join(" ")} />', 'SpreadJoin.tsx'),
    ).toThrow(/SpreadJoin\.tsx:1 `className` -- className joins an array containing an unsupported spread/);
  });

  it('fails loudly past the alternative cap instead of conflating the arms', () => {
    // Seven independent ternaries expand to 2^7 = 128 alternatives, past the
    // 64 cap. The previous behaviour joined them into one string, which is the
    // one degradation this guard cannot afford: a dead hover in one arm would
    // hide behind a working hover in another and the site would read clean.
    const ternary = (n: number) => `(c${n} ? "a${n}" : "b${n}")`;
    const src =
      `<Button className={${Array.from({ length: 7 }, (_, i) => ternary(i)).join(' + " " + ')}} />`;

    expect(() => buttonTags(src, 'Pathological.tsx')).toThrow(/past the 64 cap/);
    // The failure must name the site, not just the condition.
    expect(() => buttonTags(src, 'Pathological.tsx')).toThrow(
      /Pathological\.tsx:1 `className`/,
    );

    // Control: one fewer ternary is 64 alternatives, at the cap and not past it,
    // so it must still parse. Without this the test would also pass if the cap
    // threw unconditionally.
    const ok = `<Button className={${Array.from({ length: 6 }, (_, i) => ternary(i)).join(' + " " + ')}} />`;
    expect(buttonTags(ok, 'Ok.tsx')[0].branches).toHaveLength(64);
  });

  it('pairs a className arm with the variant arm chosen by the same condition', () => {
    // `variant={sel ? 'primary' : 'subtle'}` -- the fill in the `sel` arm only
    // ever renders under `primary`, whose own paint still masks it. Reading the
    // two ternaries independently reported a bare variant carrying a fill.
    const src =
      "<Button variant={sel ? 'primary' : 'subtle'} " +
      "className={sel ? 'bg-pf-accent-bg text-pf-accent' : 'hover:bg-pf-bg-2'}>x</Button>";
    const [tag] = buttonTags(src);
    const fill = tag.branches.find((b) => b.text.includes('bg-pf-accent-bg'));
    expect(fill, 'the fill branch should be parsed').toBeDefined();
    expect(isUnmaskedIn(tag, fill!)).toBe(false);

    // The same site with both arms bare stays visible to the contract.
    const bare = buttonTags(src.replace("'primary'", "'ghost'"))[0];
    const bareFill = bare.branches.find((b) => b.text.includes('bg-pf-accent-bg'))!;
    expect(isUnmaskedIn(bare, bareFill)).toBe(true);
  });

  it('detects a self-referential hover, and passes once it differs', () => {
    const injected = `<Button variant="subtle" className="text-pf-error-text hover:text-pf-error-text">x</Button>`;
    const flagged = (src: string) =>
      buttonTags(src).filter((t) =>
        t.branches.some((branch) => {
          if (!isUnmaskedIn(t, branch)) return false;
          const { rest, hover } = hoverTokens(branch.text);
          return [...hover].some((u) => rest.has(u)) && ![...hover].some((u) => !rest.has(u));
        }),
      );

    expect(flagged(injected)).toHaveLength(1);
    // The same shape reached through an important spelling.
    expect(flagged(injected.replace('hover:text', 'hover:!text'))).toHaveLength(1);
    // A hover that actually differs, and a masked variant, are both in contract.
    expect(
      flagged(injected.replace('hover:text-pf-error-text', 'enabled:hover:bg-pf-error/10')),
    ).toHaveLength(0);
    expect(flagged(injected.replace('variant="subtle"', 'variant="primary"'))).toHaveLength(0);
    // A selected control holding its fill while another token supplies feedback.
    expect(
      flagged(
        '<Button variant="ghost" className="bg-pf-accent-bg hover:bg-pf-accent-bg motion-safe:hover:-translate-y-px">x</Button>',
      ),
    ).toHaveLength(0);
    // Branches are evaluated separately: this is correct code, and conflating
    // the two arms of the ternary is what made an earlier revision flag it.
    expect(
      flagged(
        '<Button variant="subtle" className={`p-0 ${isLast ? "text-pf-text-primary" : "text-pf-text-secondary hover:text-pf-text-primary"}`}>x</Button>',
      ),
    ).toHaveLength(0);
    // ...but a dead hover hiding in one arm is still found.
    expect(
      flagged(
        '<Button variant="subtle" className={`p-0 ${isLast ? "text-pf-error-text hover:text-pf-error-text" : "text-pf-text-secondary hover:text-pf-text-primary"}`}>x</Button>',
      ),
    ).toHaveLength(1);
  });

  // `enabled:hover:` is correct; `group-hover:`/`peer-hover:` key off another
  // element's state and say nothing about this control being disabled.
  const unguardedHover = (className: string) =>
    classTokens(className).some((token) =>
      /(?<!enabled:)(?<!group-)(?<!peer-)hover:/.test(token),
    );

  it('detects the defect it is meant to detect', () => {
    const injected = `<Button disabled={busy} className="bg-pf-bg-1 hover:bg-pf-bg-0">x</Button>`;
    expect(
      buttonTags(injected).filter((t) => t.disableable && unguardedHover(t.className)),
    ).toHaveLength(1);

    const fixed = injected.replace('hover:', 'enabled:hover:');
    expect(
      buttonTags(fixed).filter((t) => t.disableable && unguardedHover(t.className)),
    ).toHaveLength(0);
  });

  /**
   * Regression tests for the parser itself, not for any call site. Each shape
   * below defeated a previous hand-rolled parser and was found by a reviewer
   * injecting a live defect, not by reading the code.
   */
  it('reads past an arrow function in a prop', () => {
    // An earlier parser terminated the tag at the first `>` after `<Button`,
    // which an arrow function supplies — truncating before `disabled` and
    // `className`, so the guard passed on a file that had a defect.
    const withArrow = [
      '<Button',
      '  onClick={() => {',
      '    actionItem.onClick();',
      '  }}',
      '  disabled={actionItem.disabled}',
      '  className="rounded-none hover:bg-pf-bg-1"',
      '>label</Button>',
    ].join('\n');

    const [parsed] = buttonTags(withArrow);
    expect(parsed, 'the opening tag must be found at all').toBeDefined();
    expect(parsed.disableable, 'tag truncated before `disabled` by the `=>`').toBe(true);
    expect(parsed.className, 'tag truncated before `className` by the `=>`').toContain(
      'hover:bg-pf-bg-1',
    );
  });

  it('reads a className out of a template interpolation', () => {
    // A string-scanning reader returned raw template text, so an interpolated
    // token arrived quote-prefixed (`"text-pf-error`) and matched nothing.
    // 18 live call sites lost className tokens this way.
    const interpolated = [
      '<Button variant="ghost" className={`px-2 ${',
      "  active ? 'text-pf-error font-medium' : 'text-pf-text-secondary'",
      '}`}>x</Button>',
    ].join('\n');

    const [parsed] = buttonTags(interpolated);
    expect(parsed.variants).toEqual(['ghost']);
    expect(classTokens(parsed.className).map(bareUtility)).toContain('text-pf-error');
  });

  it('ignores paint on a nested element, and sees the Button own it', () => {
    // A child's own `color` beats inheritance in any layer, so nested icon
    // paint is unchanged by #1102 and must not be attributed to the Button.
    const nested =
      '<Button variant="subtle" className="px-2" iconCenter={<Icon className="text-pf-error" />}>x</Button>';
    expect(classTokens(buttonTags(nested)[0].className).map(bareUtility)).not.toContain(
      'text-pf-error',
    );

    const own =
      '<Button variant="subtle" className="px-2 text-pf-error" iconCenter={<Icon className="w-4" />}>x</Button>';
    expect(classTokens(buttonTags(own)[0].className).map(bareUtility)).toContain('text-pf-error');
  });

  it('treats a trailing bare `disabled` and `loading` as disableable', () => {
    expect(buttonTags('<Button disabled className="hover:bg-pf-bg-1">x</Button>')[0].disableable).toBe(
      true,
    );
    expect(buttonTags('<Button loading className="hover:bg-pf-bg-1">x</Button>')[0].disableable).toBe(
      true,
    );
    // Annotations are not the real prop and disable nothing.
    expect(
      buttonTags('<Button aria-disabled="true" className="hover:bg-pf-bg-1">x</Button>')[0]
        .disableable,
    ).toBe(false);
  });

  it('does not skip a Button whose variant is a non-literal expression', () => {
    // `declaredVariants` used to return [] here, so the tag was silently
    // dropped from both contracts. Unresolvable is treated as unmasked.
    const dynamic = '<Button variant={closeButtonVariant} className="text-pf-error">x</Button>';
    const [parsed] = buttonTags(dynamic);
    expect(parsed.dynamicVariant).toBe(true);
    expect(isUnmasked(parsed)).toBe(true);
  });
});
