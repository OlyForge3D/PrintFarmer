import { createContext, useContext } from 'react';

/**
 * The DOM node the settings shell exposes *below* its scroll pane, or `null`
 * before the shell has mounted it.
 *
 * ## Why the save bar cannot simply be `position: sticky`
 *
 * It was, and it never once stuck. `position: sticky` pins an element to its
 * nearest scrolling ancestor, and the bar had two problems at the same time:
 *
 * 1. The settings scroll pane could not scroll. Its `flex-1 min-h-0` chain
 *    bottoms out in `PageTemplate`, which is a plain block with `min-h-full` —
 *    a *minimum*, not a bound — so the pane grew to full content height instead
 *    of filling a fixed viewport slot. Measured on System Config at 1440x900:
 *    pane `clientHeight === scrollHeight === 1034px`, `canScroll: false`.
 * 2. Because the pane never scrolled, the bar sat wherever the content flow put
 *    it — 249px below the fold (`top: 1098` in a 900px viewport). Dirtying a
 *    field produced Save and Discard buttons the user could not see.
 *
 * Fixing only the first would still leave the bar inside the scroll flow, where
 * an intervening `overflow` wrapper re-captures it as the sticky containing
 * block. Verified: bounding the chain alone moved the pane to `canScroll: true`
 * and left the bar at `top: 1098`, still invisible.
 *
 * ## Why a portal
 *
 * Docking the bar *outside* the scrollport removes the problem rather than
 * tuning around it: as a flex sibling under the pane it is always on screen, and
 * no containing-block rule can move it. But the state it needs — which groups
 * are dirty, the save and discard handlers — lives in `SettingsPage`, the
 * content, not the shell.
 *
 * A portal keeps ownership where the knowledge is, exactly as
 * `SettingsHeaderSlotContext` does for the Essential/Everything toggle: the page
 * decides whether a bar is warranted, React puts the DOM under the pane, and the
 * bar disappears the instant that page unmounts. Lifting the dirty state into
 * the shell would instead put a dead bar under every non-settings admin page.
 */
export const SettingsFooterSlotContext = createContext<HTMLElement | null>(null);

export function useSettingsFooterSlot(): HTMLElement | null {
  return useContext(SettingsFooterSlotContext);
}
