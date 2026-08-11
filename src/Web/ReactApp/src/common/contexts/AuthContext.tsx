/* eslint-disable react-refresh/only-export-components */
import React, { createContext, useEffect, useState, ReactNode, useCallback, useRef } from 'react';
import { apiClient } from '@/services/api';
import { UserDto, LoginRequest, RegisterRequest } from '@/types/api';
import { loginWithPasskey as passkeyLogin } from '@/services/passkeyService';
import { queryClient } from '@/services/queryClient';
import { clearSensitiveUserQueries } from '@/common/auth/sensitiveQueryCache';
import { resetAuthenticatedSignalRSession } from '@/common/auth/authenticatedSignalRSession';
import { subscribeToAuthenticationExpiration } from '@/common/auth/authenticationExpiration';
import { AUTH_SESSION_ESTABLISHED_EVENT } from '@/services/authEvents';
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
            window.dispatchEvent(new Event(AUTH_SESSION_ESTABLISHED_EVENT));
          }
        } catch (err) {
          console.error('Failed to synchronize authentication state:', err);
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
    const generation = ++authTransitionGeneration.current;
    setIsLoading(true);
    setError(null);

    try {
      const result = await apiClient.login(credentials);
      if (generation !== authTransitionGeneration.current) {
        return false;
      }

      // If user is inactive (not approved), show a special error and do not store token
      if (result.success && result.user && result.user.isActive === false) {
        setError('Your account is pending admin approval. You cannot log in until approved.');
        return false;
      }

      if (result.success && result.token && result.user) {
        await resetAuthenticatedSignalRSession();
        if (generation !== authTransitionGeneration.current) {
          return false;
        }

        localStorage.setItem('auth-token', result.token);
        // Purge any previous identity's sensitive cache before the
        // authenticated UI renders for this user (#762).
        await clearSensitiveUserQueries(queryClient);
        if (generation !== authTransitionGeneration.current) {
          return false;
        }

        setUser(result.user);
        window.dispatchEvent(new Event(AUTH_SESSION_ESTABLISHED_EVENT));
        return true;
      } else {
        setError(result.error || 'Login failed');
        return false;
      }
    } catch (err: unknown) {
      const errorMessage = err instanceof Error ? err.message : 'Login failed';
      if (generation === authTransitionGeneration.current) {
        setError(errorMessage);
      }
      return false;
    } finally {
      if (generation === authTransitionGeneration.current) {
        setIsLoading(false);
      }
    }
  };

  const loginWithPasskey = async (username: string): Promise<boolean> => {
    const generation = ++authTransitionGeneration.current;
    setIsLoading(true);
    setError(null);

    try {
      const result = await passkeyLogin(username);
      if (generation !== authTransitionGeneration.current) {
        return false;
      }

      if (result.success && result.user && result.user.isActive === false) {
        setError('Your account is pending admin approval. You cannot log in until approved.');
        return false;
      }

      if (result.success && result.token && result.user) {
        await resetAuthenticatedSignalRSession();
        if (generation !== authTransitionGeneration.current) {
          return false;
        }

        localStorage.setItem('auth-token', result.token);
        // Purge any previous identity's sensitive cache before the
        // authenticated UI renders for this user (#762).
        await clearSensitiveUserQueries(queryClient);
        if (generation !== authTransitionGeneration.current) {
          return false;
        }

        setUser(result.user);
        window.dispatchEvent(new Event(AUTH_SESSION_ESTABLISHED_EVENT));
        return true;
      } else {
        setError(result.error || 'Passkey login failed');
        return false;
      }
    } finally {
      if (generation === authTransitionGeneration.current) {
        setIsLoading(false);
      }
    }
  };

  const register = async (userData: RegisterRequest): Promise<boolean | 'pending'> => {
    const generation = ++authTransitionGeneration.current;
    setIsLoading(true);
    setError(null);

    try {
      const result = await apiClient.register(userData);
      if (generation !== authTransitionGeneration.current) {
        return false;
      }

      // If registration is successful but user is inactive, redirect to pending page
      if (result.success && result.user && result.user.isActive === false) {
        // Do not store token or set user
        return 'pending';
      }
      if (result.success && result.token && result.user) {
        await resetAuthenticatedSignalRSession();
        if (generation !== authTransitionGeneration.current) {
          return false;
        }

        localStorage.setItem('auth-token', result.token);
        // Purge any previous identity's sensitive cache before the
        // authenticated UI renders for this user (#762).
        await clearSensitiveUserQueries(queryClient);
        if (generation !== authTransitionGeneration.current) {
          return false;
        }

        setUser(result.user);
        window.dispatchEvent(new Event(AUTH_SESSION_ESTABLISHED_EVENT));
        return true;
      } else {
        setError(result.error || 'Registration failed');
        return false;
      }
    } catch (err: unknown) {
      const errorMessage = err instanceof Error ? err.message : 'Registration failed';
      if (generation === authTransitionGeneration.current) {
        setError(errorMessage);
      }
      return false;
    } finally {
      if (generation === authTransitionGeneration.current) {
        setIsLoading(false);
      }
    }
  };

  const logout = async (): Promise<void> => {
    const generation = ++authTransitionGeneration.current;
    const tokenAtStart = localStorage.getItem('auth-token');
    setIsLoading(true);

    try {
      await resetAuthenticatedSignalRSession();
      if (generation !== authTransitionGeneration.current) {
        return;
      }

      await apiClient.logout();
    } catch (err) {
      console.error('Logout error:', err);
    } finally {
      if (
        generation === authTransitionGeneration.current
        && localStorage.getItem('auth-token') === tokenAtStart
      ) {
        localStorage.removeItem('auth-token');
        setUser(null);
        setError(null);
        try {
          // Purge sensitive cache immediately on logout so it cannot leak into
          // the next identity, even if that identity never calls login() (e.g.
          // another tab, or a future flow that swaps identity without this
          // AuthContext's login path) (#762).
          await clearSensitiveUserQueries(queryClient);
        } finally {
          if (generation === authTransitionGeneration.current) {
            setIsLoading(false);
          }
        }
      }
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
    if (user.permissions.includes(permissionString)) return true;

    // A resource-level admin grant implies every finer-grained action on that same
    // resource (e.g. "calibration:admin" implies "calibration:read"). This never
    // crosses resources and does not extend beyond the "admin" action.
    if (action !== 'admin' && user.permissions.includes(`${resource}:admin`)) return true;

    return false;
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