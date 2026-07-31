import { createContext, useContext } from 'react';

/**
 * The DOM node the settings shell exposes inside its page header's `actions`
 * slot, or `null` before the shell has mounted it (and on the first render,
 * before the callback ref fires).
 *
 * ## Why a portal and not lifted state
 *
 * The Essential/Everything toggle belongs in the page header — it is a global,
 * persisted preference, not an ephemeral per-page filter. But the thing that
 * knows whether the toggle is *meaningful* is the content page: `SettingsPage`
 * is the only mounted content that has metadata-driven fields to hide, and even
 * it renders zero fields on some tabs.
 *
 * Lifting the state to the shell would put a dead control in the header of
 * every non-settings page (Users, Webhooks, Cameras…). Publishing the count
 * back up through a store or a `setState`-in-effect would work but inverts data
 * flow for a purely visual placement.
 *
 * A portal keeps ownership where the knowledge is: the page renders the control
 * under its own conditions, React puts the DOM in the header, and the control
 * disappears the moment that page unmounts. No registry to keep in sync.
 */
export const SettingsHeaderSlotContext = createContext<HTMLElement | null>(null);

export function useSettingsHeaderSlot(): HTMLElement | null {
  return useContext(SettingsHeaderSlotContext);
}
