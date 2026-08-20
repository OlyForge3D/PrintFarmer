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
 * Matches the high-flow marker.
 *
 * Deliberately NOT `\bhf\b`: profile names ship both spaced and unspaced forms
 * ("Prusa CORE One HF 0.4 nozzle", "Prusa MK4S HF0.4 nozzle"), and `\b` fails
 * on the unspaced form because there is no word boundary between "F" and "0".
 * That produced a row whose visible label ended in "HF" but carried no badge.
 * The lookarounds still refuse letter-adjacent matches, so "HFX600" and
 * "shelfhf" are not treated as high flow.
 */
const HIGH_FLOW_PATTERN = /(?:^|[^a-z])hf(?![a-z])/i;

/**
 * True when the supplied text designates a high-flow variant.
 *
 * This is a NAME heuristic, not a verified hardware capability: for Prusa
 * CORE One the standard and HF profiles share `nozzleDiameter` and
 * `printerVariant`, and `nozzleType` is empty on both, so nothing structural
 * distinguishes them. Callers must phrase user-facing copy accordingly rather
 * than asserting volumetric limits as fact.
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
 * Builds raw-name -> display-label pairs for profiles rendered TOGETHER.
 *
 * Pass only the rows that appear side by side — typically one nozzle group.
 * Nozzle is constant within such a group, so trimming the nozzle token is both
 * safe and unique there.
 *
 * Do NOT pass a printer's entire profile set: every multi-nozzle printer
 * collides by construction ("Prusa CORE One 0.4 nozzle" and
 * "Prusa CORE One 0.6 nozzle" both trim to "Prusa CORE One"), which would trip
 * the fallback below and silently disable trimming everywhere.
 *
 * Stripping is applied only when every resulting label stays unique within the
 * supplied set. If two would collapse, the whole set falls back to raw names:
 * showing a redundant nozzle is much better than showing two indistinguishable
 * options.
 *
 * @param names Raw profile names rendered in the same group.
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
