import React, { createContext, useContext, useEffect, useState, ReactNode } from 'react';
import { apiClient } from '@/services/api';
import { UserDto, LoginRequest, RegisterRequest } from '@/types/api';

interface AuthContextType {
  user: UserDto | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  login: (credentials: LoginRequest) => Promise<boolean>;
  register: (userData: RegisterRequest) => Promise<boolean>;
  logout: () => Promise<void>;
  hasRole: (role: string) => boolean;
  hasPermission: (resource: string, action: string) => boolean;
  error: string | null;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

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
      
      // Check if this is a mock development token
      if (token === 'mock-dev-token') {
        // Use mock user without calling API
        const mockUser: UserDto = {
          id: 'mock-user-id',
          username: 'admin',
          email: 'admin@printfarmer.local',
          firstName: 'Admin',
          lastName: 'User',
          isActive: true,
          emailConfirmed: true,
          createdAt: new Date(),
          roles: ['farm_admin'],
          permissions: ['printers:read', 'printers:create', 'printers:update', 'printers:delete', 'gcode_harvest:read']
        };
        
        setUser(mockUser);
        setIsLoading(false);
        return;
      }
      
      if (!token) {
        // For development, create a mock user if no token exists
        const mockUser: UserDto = {
          id: 'mock-user-id',
          username: 'admin',
          email: 'admin@printfarmer.local',
          firstName: 'Admin',
          lastName: 'User',
          isActive: true,
          emailConfirmed: true,
          createdAt: new Date(),
          roles: ['farm_admin'],
          permissions: [
            'printers:read', 
            'printers:create', 
            'printers:update', 
            'printers:delete', 
            'gcode_harvest:read',
            'gcode_harvest:create',
            'gcode_harvest:execute',
            'gcode_harvest:delete'
          ]
        };
        
        localStorage.setItem('auth-token', 'mock-dev-token');
        setUser(mockUser);
        setIsLoading(false);
        return;
      }

      try {
        // Try to get current user from API for real tokens
        const userData = await apiClient.getCurrentUser();
        setUser(userData);
        setError(null);
      } catch (err) {
        console.error('Failed to get current user:', err);
        // Fallback to mock user for development
        const mockUser: UserDto = {
          id: 'mock-user-id',
          username: 'admin',
          email: 'admin@printfarmer.local',
          firstName: 'Admin',
          lastName: 'User',
          isActive: true,
          emailConfirmed: true,
          createdAt: new Date(),
          roles: ['farm_admin'],
          permissions: [
            'printers:read', 
            'printers:create', 
            'printers:update', 
            'printers:delete', 
            'gcode_harvest:read',
            'gcode_harvest:create',
            'gcode_harvest:execute',
            'gcode_harvest:delete'
          ]
        };
        
        setUser(mockUser);
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
    } catch (err: any) {
      const errorMessage = err.message || 'Login failed';
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
    } catch (err: any) {
      const errorMessage = err.message || 'Registration failed';
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

  return (
    <AuthContext.Provider value={value}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthContextType {
  const context = useContext(AuthContext);
  if (context === undefined) {
    throw new Error('useAuth must be used within an AuthProvider');
  }
  return context;
}