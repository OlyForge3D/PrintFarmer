import { useContext } from 'react';
import { SpoolmanContext } from './SpoolmanContext';

export function useSpoolman() {
  const ctx = useContext(SpoolmanContext);
  if (!ctx) throw new Error('useSpoolman must be used within a SpoolmanProvider');
  return ctx;
}