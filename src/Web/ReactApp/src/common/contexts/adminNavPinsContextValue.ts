import { createContext } from 'react';

export interface AdminNavPinsContextValue {
  pinnedIds: string[];
  setPinned: (destinationId: string, pinned: boolean) => void;
}

export const AdminNavPinsContext = createContext<AdminNavPinsContextValue>({
  pinnedIds: [],
  setPinned: () => undefined,
});
