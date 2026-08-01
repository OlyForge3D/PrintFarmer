/**
 * Label for the command palette's keyboard shortcut, matched to the platform.
 *
 * `navigator.platform` is deprecated but is still the only synchronously
 * readable platform signal in every browser we support; `userAgentData` is
 * Chromium-only and its high-entropy fields need an async call. We read
 * `userAgentData.platform` when the browser exposes it and fall back otherwise,
 * which gives a correct answer everywhere without an await in render.
 */
export function commandPaletteShortcutLabel(): string {
  if (typeof navigator === 'undefined') {
    return 'Ctrl K';
  }

  const uaData = (navigator as Navigator & { userAgentData?: { platform?: string } }).userAgentData;
  const platform = uaData?.platform ?? navigator.platform ?? '';

  return /mac/i.test(platform) ? '⌘K' : 'Ctrl K';
}
