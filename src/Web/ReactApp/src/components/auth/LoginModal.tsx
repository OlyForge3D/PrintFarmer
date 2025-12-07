import React, { useState, useEffect, useCallback } from 'react';
import { Link } from 'react-router-dom';
import { FormSkeleton } from '@/components/skeletons/FormSkeleton';
import { X, Eye, EyeOff, LogIn } from 'lucide-react';
import { PrintFarmerLogo } from '@/components/PrintFarmerLogo';
import { useAuth } from '@/contexts/AuthHooks';
import { Button, Input } from '@/components/ui';

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

  const handleClose = useCallback(() => {
    if (!isLoading) {
      onClose();
      setUsername('');
      setPassword('');
    }
  }, [isLoading, onClose]);

  // Handle ESC key to close modal
  useEffect(() => {
    if (!isOpen) return;

    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !isLoading) {
        handleClose();
      }
    };

    document.addEventListener('keydown', handleEscape);
    return () => document.removeEventListener('keydown', handleEscape);
  }, [isOpen, isLoading, handleClose]);

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
          <Button
            onClick={handleClose}
            disabled={isLoading}
            variant="subtle"
            size="sm"
            aria-label="Close sign in modal"
            title="Close"
            className="!p-0 !h-auto"
          >
            <X className="h-5 w-5" />
          </Button>
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
            <Input
              type="text"
              id="username"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              placeholder="Enter your username or email"
              required
              disabled={isLoading}
              className="w-full"
            />
          </div>

          <div>
            <label htmlFor="password" className="block text-sm font-medium text-pf-text-primary mb-1">
              Password
            </label>
            <div className="relative">
              <Input
                type={showPassword ? 'text' : 'password'}
                id="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                placeholder="Enter your password"
                required
                disabled={isLoading}
                className="w-full pr-10"
              />
              <Button
                onClick={() => setShowPassword(!showPassword)}
                variant="subtle"
                size="sm"
                disabled={isLoading}
                className="absolute right-3 top-1/2 -translate-y-1/2 !p-0 !h-auto"
              >
                {showPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              </Button>
            </div>
          </div>

          <div className="flex items-center justify-between text-sm">
            <Link
              to="/forgot-password"
              className="text-pf-accent hover:text-pf-accent-hover font-medium"
              onClick={onClose}
            >
              Forgot password?
            </Link>
          </div>

          <div className="flex items-center justify-between pt-4">
            <Button
              type="button"
              onClick={onSwitchToRegister}
              variant="subtle"
              disabled={isLoading}
            >
              Need an account? Register
            </Button>
            <Button
              type="submit"
              disabled={isLoading || !username || !password}
              variant="primary"
              className="flex items-center gap-2"
            >
              {isLoading ? (
                <>
                  <div className="pf-animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                  <span>Signing In...</span>
                </>
              ) : (
                <>
                  <LogIn className="h-4 w-4" />
                  <span>Sign In</span>
                </>
              )}
            </Button>
          </div>
        </form>
      </div>
    </div>
  );
}