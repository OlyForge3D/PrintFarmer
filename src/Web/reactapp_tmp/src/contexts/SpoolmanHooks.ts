import { useContext } from 'react';
import { SpoolmanContext } from './SpoolmanTypes';

export function useSpoolman() {
  const ctx = useContext(SpoolmanContext);
  if (!ctx) throw new Error('useSpoolman must be used within a SpoolmanProvider');
  return ctx;
}