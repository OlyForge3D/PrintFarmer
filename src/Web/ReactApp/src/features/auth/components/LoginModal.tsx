import React, { useState, useCallback } from 'react';
import { Link } from 'react-router-dom';
import { FormSkeleton } from '@/common/components/skeletons/FormSkeleton';
import { EyeIcon, EyeOffIcon, LoginIcon } from '@/common/components/icons/MdiIcons';
import { PrintFarmerLogo } from '@/common/components/PrintFarmerLogo';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { Button, Input } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';

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

  const formContent = (
    <>
      {isLoading && (
        <div className="px-6 pt-4"><FormSkeleton fields={2} /></div>
      )}
      <form onSubmit={handleSubmit} className="p-6 space-y-4" aria-live="polite">
        {error && (
          <div className="bg-pf-bg-2 border border-pf-border px-4 py-3 rounded-md text-sm" style={{ color: 'var(--pf-error)' }}>
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
              {showPassword ? <EyeOffIcon className="h-4 w-4" /> : <EyeIcon className="h-4 w-4" />}
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
          >
            {isLoading ? (
              <>
                <div className="pf-animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                <span>Signing In...</span>
              </>
            ) : (
              <>
                <LoginIcon className="h-4 w-4" />
                <span>Sign In</span>
              </>
            )}
          </Button>
        </div>
      </form>
    </>
  );

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title="Sign In"
      width="max-w-md"
      isDisabled={isLoading}
    >
      {formContent}
    </Modal>
  );
}