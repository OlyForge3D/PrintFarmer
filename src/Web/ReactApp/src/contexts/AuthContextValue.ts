import type { UserDto, LoginRequest, RegisterRequest } from '@/types/api';

export interface AuthContextType {
  user: UserDto | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  login: (credentials: LoginRequest) => Promise<boolean>;
  loginWithPasskey: (username: string) => Promise<boolean>;
  register: (userData: RegisterRequest) => Promise<boolean | 'pending'>;
  logout: () => Promise<void>;
  hasRole: (role: string) => boolean;
  hasPermission: (resource: string, action: string) => boolean;
  error: string | null;
}
