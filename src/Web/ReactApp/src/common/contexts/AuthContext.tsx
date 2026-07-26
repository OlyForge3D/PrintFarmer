/* eslint-disable react-refresh/only-export-components */
import React, { createContext, useEffect, useState, ReactNode, useCallback, useRef } from 'react';
import { apiClient } from '@/services/api';
import { UserDto, LoginRequest, RegisterRequest } from '@/types/api';
import { loginWithPasskey as passkeyLogin } from '@/services/passkeyService';
import { queryClient } from '@/services/queryClient';
import { clearSensitiveUserQueries } from '@/common/auth/sensitiveQueryCache';
import { resetAuthenticatedSignalRSession } from '@/common/auth/authenticatedSignalRSession';
import { subscribeToAuthenticationExpiration } from '@/common/auth/authenticationExpiration';
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
  const authTransitionGeneration = useRef(0);

  const isAuthenticated = user !== null;

  // Initialize authentication state on mount
  useEffect(() => {
    const initializeAuth = async () => {
      const token = localStorage.getItem('auth-token');
      const transitionGeneration = authTransitionGeneration.current;
      const ownsTransition = () =>
        authTransitionGeneration.current === transitionGeneration;
      const isCurrentToken = () =>
        ownsTransition() && localStorage.getItem('auth-token') === token;
      
      if (!token) {
        if (ownsTransition()) {
          setIsLoading(false);
        }
        return;
      }

      try {
        // Try to get current user from API
        const userData = await apiClient.getCurrentUser();
        if (isCurrentToken()) {
          setUser(userData);
          setError(null);
        }
      } catch (err) {
        console.error('Failed to get current user:', err);
        if (!isCurrentToken()) {
          return;
        }

        // Remove invalid token
        await resetAuthenticatedSignalRSession();
        if (isCurrentToken()) {
          localStorage.removeItem('auth-token');
          setUser(null);
          setError(null);
        }
      } finally {
        if (ownsTransition()) {
          setIsLoading(false);
        }
      }
    };

    initializeAuth();
  }, []);

  useEffect(() => {
    let disposed = false;
    const handleAuthTokenChange = (event: StorageEvent) => {
      if (event.key !== 'auth-token' ||
          event.oldValue === event.newValue ||
          (event.storageArea && event.storageArea !== localStorage)) {
        return;
      }

      const expectedToken = event.newValue;
      const transitionGeneration = ++authTransitionGeneration.current;
      const ownsTransition = () =>
        !disposed && authTransitionGeneration.current === transitionGeneration;
      const isCurrentTransition = () =>
        ownsTransition() && localStorage.getItem('auth-token') === expectedToken;
      setIsLoading(true);
      setUser(null);
      setError(null);

      void (async () => {
        try {
          await resetAuthenticatedSignalRSession();
          await clearSensitiveUserQueries(queryClient);
          if (!expectedToken || !isCurrentTransition()) {
            return;
          }

          const userData = await apiClient.getCurrentUser();
          if (isCurrentTransition()) {
            setUser(userData);
          }
        } catch (err) {
          console.error('Failed to synchronize authentication state:', err);
          if (isCurrentTransition() && expectedToken) {
            localStorage.removeItem('auth-token');
          }
        } finally {
          if (ownsTransition()) {
            setIsLoading(false);
          }
        }
      })();
    };

    const unsubscribeFromAuthenticationExpiration = subscribeToAuthenticationExpiration(() => {
      authTransitionGeneration.current++;
      setUser(null);
      setError(null);
      setIsLoading(false);
      void clearSensitiveUserQueries(queryClient).catch(err => {
        console.error('Failed to clear sensitive authentication state:', err);
      });
    });
    window.addEventListener('storage', handleAuthTokenChange);
    return () => {
      disposed = true;
      unsubscribeFromAuthenticationExpiration();
      window.removeEventListener('storage', handleAuthTokenChange);
    };
  }, []);

  const login = async (credentials: LoginRequest): Promise<boolean> => {
    authTransitionGeneration.current++;
    setIsLoading(true);
    setError(null);

    try {
      const result = await apiClient.login(credentials);

      // If user is inactive (not approved), show a special error and do not store token
      if (result.success && result.user && result.user.isActive === false) {
        setError('Your account is pending admin approval. You cannot log in until approved.');
        return false;
      }

      if (result.success && result.token && result.user) {
        await resetAuthenticatedSignalRSession();
        localStorage.setItem('auth-token', result.token);
        // Purge any previous identity's sensitive cache before the
        // authenticated UI renders for this user (#762).
        await clearSensitiveUserQueries(queryClient);
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

  const loginWithPasskey = async (username: string): Promise<boolean> => {
    authTransitionGeneration.current++;
    setIsLoading(true);
    setError(null);

    try {
      const result = await passkeyLogin(username);

      if (result.success && result.user && result.user.isActive === false) {
        setError('Your account is pending admin approval. You cannot log in until approved.');
        return false;
      }

      if (result.success && result.token && result.user) {
        await resetAuthenticatedSignalRSession();
        localStorage.setItem('auth-token', result.token);
        // Purge any previous identity's sensitive cache before the
        // authenticated UI renders for this user (#762).
        await clearSensitiveUserQueries(queryClient);
        setUser(result.user);
        return true;
      } else {
        setError(result.error || 'Passkey login failed');
        return false;
      }
    } finally {
      setIsLoading(false);
    }
  };

  const register = async (userData: RegisterRequest): Promise<boolean | 'pending'> => {
    authTransitionGeneration.current++;
    setIsLoading(true);
    setError(null);

    try {
      const result = await apiClient.register(userData);
      // If registration is successful but user is inactive, redirect to pending page
      if (result.success && result.user && result.user.isActive === false) {
        // Do not store token or set user
        return 'pending';
      }
      if (result.success && result.token && result.user) {
        await resetAuthenticatedSignalRSession();
        localStorage.setItem('auth-token', result.token);
        // Purge any previous identity's sensitive cache before the
        // authenticated UI renders for this user (#762).
        await clearSensitiveUserQueries(queryClient);
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
    authTransitionGeneration.current++;
    setIsLoading(true);

    try {
      await resetAuthenticatedSignalRSession();
      await apiClient.logout();
    } catch (err) {
      console.error('Logout error:', err);
    } finally {
      localStorage.removeItem('auth-token');
      setUser(null);
      setError(null);
      // Purge sensitive cache immediately on logout so it cannot leak into
      // the next identity, even if that identity never calls login() (e.g.
      // another tab, or a future flow that swaps identity without this
      // AuthContext's login path) (#762).
      await clearSensitiveUserQueries(queryClient);
      setIsLoading(false);
    }
  };

  const hasRole = useCallback((role: string): boolean => {
    if (!user || !user.roles) return false;
    return user.roles.includes(role) || user.roles.includes('farm_admin');
  }, [user]);

  const hasPermission = useCallback((resource: string, action: string): boolean => {
    if (!user || !user.permissions) return false;
    
    // Admin has all permissions
    if (user.roles?.includes('farm_admin')) return true;
    
    // Check specific permission
    const permissionString = `${resource}:${action}`;
    return user.permissions.includes(permissionString);
  }, [user]);

  const value: AuthContextType = {
    user,
    isAuthenticated,
    isLoading,
    login,
    loginWithPasskey,
    register: register as (userData: RegisterRequest) => Promise<boolean>, // for compatibility, but actual type is boolean | 'pending'
    logout,
    hasRole,
    hasPermission,
    error,
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

// Hooks are implemented in `AuthHooks.ts` to keep this file component-only and
// satisfy the `react-refresh/only-export-components` rule.