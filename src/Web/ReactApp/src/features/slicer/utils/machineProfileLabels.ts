/**
 * Display helpers for machine profile names.
 *
 * OrcaSlicer machine profiles encode the nozzle in the profile NAME
 * ("Prusa CORE One 0.4 nozzle"). When the nozzle is already established by
 * another control, repeating it in the label is noise — but stripping it is
 * only ever a DISPLAY concern. The value sent to the API must always remain the
 * full, unmodified profile name, because `POST /api/slice/` matches on it.
 *
 * The high-flow case is why this needs care. For Prusa CORE One the standard
 * and high-flow profiles share `nozzleDiameter` (0.40) AND `printerVariant`
 * ("0.4"), and `nozzleType` is empty on both — the ONLY signal separating them
 * is the "HF" token inside the name:
 *
 *   Prusa CORE One 0.4 nozzle      -> "Prusa CORE One"
 *   Prusa CORE One HF 0.4 nozzle   -> "Prusa CORE One HF"
 *
 * Stripping the trailing nozzle token keeps those distinct, but a vendor could
 * ship names where the nozzle is the only differentiator. `buildMachineProfileLabels`
 * therefore verifies uniqueness and falls back to raw names rather than render
 * two rows the user cannot tell apart.
 */

/** Trailing "<n> nozzle" / "<n>mm nozzle" token, optionally hyphen-separated. */
const NOZZLE_SUFFIX_PATTERN = /\s*[-–]?\s*\d+(?:\.\d+)?\s*(?:mm)?\s*nozzle\s*$/i;

/**
 * Matches the high-flow marker as a standalone word so "HF" is detected but
 * words merely containing those letters are not.
 */
const HIGH_FLOW_PATTERN = /\bhf\b/i;

/**
 * True when the supplied text designates a high-flow variant.
 *
 * Accepts free-form text (profile name, or a name joined with its
 * compatible-printer list) so callers can detect the variant from whichever
 * field carries it.
 */
export function mentionsHighFlow(text: string): boolean {
  return HIGH_FLOW_PATTERN.test(text);
}

/**
 * Removes a trailing nozzle token for display.
 *
 * Returns the original name when stripping would leave nothing, so a profile
 * named only after its nozzle never renders blank.
 */
export function stripNozzleSuffix(name: string): string {
  const stripped = name.replace(NOZZLE_SUFFIX_PATTERN, '').trim();
  return stripped.length > 0 ? stripped : name;
}

/**
 * Builds raw-name -> display-label pairs for a set of profiles shown together.
 *
 * Stripping is applied only when every resulting label stays unique within the
 * set. If two profiles would collapse to the same label, the whole set falls
 * back to raw names: showing the redundant nozzle is much better than showing
 * two indistinguishable options.
 *
 * @param names Raw profile names rendered in the same list.
 * @returns Map keyed by raw name; values are the labels to display.
 */
export function buildMachineProfileLabels(names: readonly string[]): Map<string, string> {
  const stripped = new Map<string, string>();
  const seen = new Set<string>();
  let collision = false;

  for (const name of names) {
    const label = stripNozzleSuffix(name);
    if (seen.has(label)) {
      collision = true;
      break;
    }
    seen.add(label);
    stripped.set(name, label);
  }

  if (collision) {
    return new Map(names.map((name) => [name, name]));
  }

  return stripped;
}
