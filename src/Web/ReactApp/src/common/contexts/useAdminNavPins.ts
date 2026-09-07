import { useContext } from 'react';
import { AdminNavPinsContext } from '@/common/contexts/adminNavPinsContextValue';

export function useAdminNavPins() {
  return useContext(AdminNavPinsContext);
}
