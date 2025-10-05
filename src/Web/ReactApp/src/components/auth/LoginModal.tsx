import React, { useState } from 'react';
import { FormSkeleton } from '@/components/skeletons/FormSkeleton';
import { X, Eye, EyeOff, LogIn } from 'lucide-react';
import { PrintFarmerLogo } from '@/components/PrintFarmerLogo';
import { useAuth } from '@/contexts/AuthHooks';

interface LoginModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSwitchToRegister: () => void;
}

export function LoginModal({ isOpen, onClose, onSwitchToRegister }: LoginModalProps) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [isLoading, setIsLoading] = useState(false);
  const { login, error } = useAuth();

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (isLoading) return;

    setIsLoading(true);
    try {
      const success = await login({ username, password });
      if (success) {
        onClose();
        setUsername('');
        setPassword('');
      }
    } finally {
      setIsLoading(false);
    }
  };

  const handleClose = () => {
    if (!isLoading) {
      onClose();
      setUsername('');
      setPassword('');
    }
  };

  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black bg-opacity-50">
      <div className="bg-pf-bg-1 rounded-lg shadow-xl max-w-md w-full mx-4 border border-pf-border">
        <div className="flex items-center justify-between p-6 border-b border-pf-border">
          <div className="flex items-center gap-2">
            <PrintFarmerLogo size={32} className="mr-2" />
            <span className="text-xl font-bold tracking-tight text-pf-accent">PRINTFARMER</span>
            <span className="text-xl font-semibold text-pf-text-primary flex items-center ml-3">
              <LogIn className="h-5 w-5 mr-2" />Sign In
            </span>
          </div>
          <button
            onClick={handleClose}
            disabled={isLoading}
            className="text-pf-text-tertiary hover:text-pf-text-primary disabled:opacity-50"
            aria-label="Close sign in modal"
            title="Close"
            type="button"
          >
            <X className="h-5 w-5" />
          </button>
        </div>
        {isLoading && (
          <div className="p-6"><FormSkeleton fields={2} /></div>
        )}
        <form onSubmit={handleSubmit} className="p-6 space-y-4" aria-live="polite">
          {error && (
            <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded-md text-sm">
              {error}
            </div>
          )}

          <div>
            <label htmlFor="username" className="block text-sm font-medium text-pf-text-primary mb-1">
              Username or Email
            </label>
            <input
              type="text"
              id="username"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
              placeholder="Enter your username or email"
              required
              disabled={isLoading}
            />
          </div>

          <div>
            <label htmlFor="password" className="block text-sm font-medium text-pf-text-primary mb-1">
              Password
            </label>
            <div className="relative">
              <input
                type={showPassword ? 'text' : 'password'}
                id="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary pr-10"
                placeholder="Enter your password"
                required
                disabled={isLoading}
              />
              <button
                type="button"
                onClick={() => setShowPassword(!showPassword)}
                className="absolute right-3 top-1/2 transform -translate-y-1/2 text-pf-text-tertiary hover:text-pf-text-primary"
                disabled={isLoading}
              >
                {showPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              </button>
            </div>
          </div>

          <div className="flex items-center justify-between pt-4">
            <button
              type="button"
              onClick={onSwitchToRegister}
              className="text-pf-accent hover:text-pf-accent-dark text-sm"
              disabled={isLoading}
            >
              Need an account? Register
            </button>
            <button
              type="submit"
              disabled={isLoading || !username || !password}
              className="px-4 py-2 bg-pf-accent text-white rounded-md hover:bg-pf-accent-dark focus:outline-none focus:ring-2 focus:ring-pf-accent disabled:opacity-50 disabled:cursor-not-allowed flex items-center"
            >
              {isLoading ? (
                <>
                  <div className="pf-animate-spin rounded-full h-4 w-4 border-b-2 border-white mr-2"></div>
                  Signing In...
                </>
              ) : (
                <>
                  <LogIn className="h-4 w-4 mr-2" />
                  Sign In
                </>
              )}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}