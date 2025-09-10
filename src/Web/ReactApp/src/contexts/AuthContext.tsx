/* eslint-disable react-refresh/only-export-components */
import React, { createContext, useEffect, useState, ReactNode, useContext } from 'react';
import { apiClient } from '@/services/api';
import { UserDto, LoginRequest, RegisterRequest } from '@/types/api';
import type { AuthContextType } from './AuthContextValue';

// AuthContextType now in separate file (AuthContextValue.ts) for faster refresh friendliness

export const AuthContext = createContext<AuthContextType | undefined>(undefined);

interface AuthProviderProps {
  children: ReactNode;
}

export function AuthProvider({ children }: AuthProviderProps) {
  const [user, setUser] = useState<UserDto | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const isAuthenticated = user !== null;

  // Initialize authentication state on mount
  useEffect(() => {
    const initializeAuth = async () => {
      const token = localStorage.getItem('auth-token');
      
      if (!token) {
        setIsLoading(false);
        return;
      }

      try {
        // Try to get current user from API
        const userData = await apiClient.getCurrentUser();
        setUser(userData);
        setError(null);
      } catch (err) {
        console.error('Failed to get current user:', err);
        // Remove invalid token
        localStorage.removeItem('auth-token');
        setUser(null);
        setError(null);
      } finally {
        setIsLoading(false);
      }
    };

    initializeAuth();
  }, []);

  const login = async (credentials: LoginRequest): Promise<boolean> => {
    setIsLoading(true);
    setError(null);

    try {
      const result = await apiClient.login(credentials);
      
      if (result.success && result.token && result.user) {
        localStorage.setItem('auth-token', result.token);
        setUser(result.user);
        return true;
      } else {
        setError(result.error || 'Login failed');
        return false;
      }
    } catch (err: unknown) {
      const errorMessage = err instanceof Error ? err.message : 'Login failed';
      setError(errorMessage);
      return false;
    } finally {
      setIsLoading(false);
    }
  };

  const register = async (userData: RegisterRequest): Promise<boolean> => {
    setIsLoading(true);
    setError(null);

    try {
      const result = await apiClient.register(userData);
      
      if (result.success && result.token && result.user) {
        localStorage.setItem('auth-token', result.token);
        setUser(result.user);
        return true;
      } else {
        setError(result.error || 'Registration failed');
        return false;
      }
    } catch (err: unknown) {
      const errorMessage = err instanceof Error ? err.message : 'Registration failed';
      setError(errorMessage);
      return false;
    } finally {
      setIsLoading(false);
    }
  };

  const logout = async (): Promise<void> => {
    setIsLoading(true);
    
    try {
      await apiClient.logout();
    } catch (err) {
      console.error('Logout error:', err);
    } finally {
      localStorage.removeItem('auth-token');
      setUser(null);
      setError(null);
      setIsLoading(false);
    }
  };

  const hasRole = (role: string): boolean => {
    if (!user || !user.roles) return false;
    return user.roles.includes(role) || user.roles.includes('farm_admin');
  };

  const hasPermission = (resource: string, action: string): boolean => {
    if (!user || !user.permissions) return false;
    
    // Admin has all permissions
    if (user.roles?.includes('farm_admin')) return true;
    
    // Check specific permission
    const permissionString = `${resource}:${action}`;
    return user.permissions.includes(permissionString);
  };

  const value: AuthContextType = {
    user,
    isAuthenticated,
    isLoading,
    login,
    register,
    logout,
    hasRole,
    hasPermission,
    error,
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

// Hook moved to separate file (AuthHooks.ts) to satisfy react-refresh rule
export function useAuthInternal(): AuthContextType {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within an AuthProvider');
  return ctx;
}

// Public hook export (kept here to align with existing import paths and lint guidance)
export function useAuth() {
  return useAuthInternal();
}