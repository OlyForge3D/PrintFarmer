import { createContext } from 'react';
import type { AuthContextType } from '@/contexts/AuthContextValue';

export const AuthContext = createContext<AuthContextType | undefined>(undefined);
