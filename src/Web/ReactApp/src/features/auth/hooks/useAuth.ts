import { useContext } from 'react';
import { AuthContext } from '@/common/contexts/auth-context';
import type { AuthContextType } from '@/contexts/AuthContextValue';

export function useAuthInternal(): AuthContextType {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within an AuthProvider');
  return ctx;
}

export function useAuth(): AuthContextType {
  return useAuthInternal();
}
