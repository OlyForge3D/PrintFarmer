/**
 * True when `className` sets a border radius unconditionally.
 *
 * `clsx` concatenates rather than merges, so a caller radius and a primitive
 * default would otherwise compete by Tailwind emission order. Variant-prefixed
 * radii deliberately do not count: keeping the base default preserves a radius
 * outside the condition while Tailwind lets the conditional utility win where
 * it applies.
 *
 * Both `rounded*` utilities and `[border-radius:...]` are recognised.
 */
export const hasRadiusOverride = (className?: string): boolean =>
  className !== undefined &&
  /(?:^|\s)!?(?:rounded(?:-\S+)?|\[border-radius:[^\]\s]+\])!?(?:\s|$)/.test(className);
