import { createContext } from 'react';

export interface AdminNavPinsContextValue {
  pinnedIds: string[];
  setPinned: (destinationId: string, pinned: boolean) => void;
  movePinned: (destinationId: string, targetIndex: number, orderedPinnedIds?: readonly string[]) => void;
}

export const AdminNavPinsContext = createContext<AdminNavPinsContextValue>({
  pinnedIds: [],
  setPinned: () => undefined,
  movePinned: () => undefined,
});
