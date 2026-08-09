/**
 * Shared test helper for asserting the Tailwind "radius contract" on Card,
 * Button, and Badge (see the radius contract doc comment in Badge.radius.test.tsx).
 *
 * Intentionally implemented without a regex. The previous version used
 * `/^(?:\S+:)*!?rounded(?:-|$)/`, whose `(?:\S+:)*` group let `\S+` itself
 * match colons, so a class name with many `:` characters and no matching
 * suffix had exponentially many ways to partition — a classic ReDoS
 * (catastrophic backtracking) pattern. Splitting on `:` up front removes the
 * ambiguity entirely: each variant/utility class is a single flat string
 * with no whitespace, so there is nothing left to backtrack over.
 */
export const radiusClasses = (element: Element): string[] =>
  Array.from(element.classList).filter((name) => {
    const utility = name.split(':').pop() ?? name;
    const base = utility.startsWith('!') ? utility.slice(1) : utility;
    return base === 'rounded' || base.startsWith('rounded-');
  });
