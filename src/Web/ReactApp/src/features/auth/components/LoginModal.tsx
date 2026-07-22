import React, { useState, useCallback } from 'react';
import { Link } from 'react-router';
import { FormSkeleton } from '@/common/components/skeletons/FormSkeleton';
import { EyeIcon, EyeOffIcon, KeyIcon, LoginIcon } from '@/common/components/icons/MdiIcons';
import { PrintFarmerLogo } from '@/common/components/PrintFarmerLogo';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { Button, Checkbox, Input } from '@/common/components/ui';
import { AuthSurface, type AuthSurfaceVariant } from '@/features/auth/components/AuthSurface';

interface LoginModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSwitchToRegister: () => void;
  surface?: AuthSurfaceVariant;
}

export function LoginModal({
  isOpen,
  onClose,
  onSwitchToRegister,
  surface = 'modal',
}: LoginModalProps) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [rememberMe, setRememberMe] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [isLoading, setIsLoading] = useState(false);
  const [passkeyLoading, setPasskeyLoading] = useState(false);
  const [passkeyError, setPasskeyError] = useState<string | null>(null);
  const { login, loginWithPasskey, error } = useAuth();

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (isLoading) return;

    setPasskeyError(null);
    setIsLoading(true);
    try {
      const success = await login({ username, password, rememberMe });
      if (success) {
        onClose();
        setUsername('');
        setPassword('');
      }
    } finally {
      setIsLoading(false);
    }
  };

  const handlePasskeyLogin = async () => {
    if (passkeyLoading || isLoading || !username) return;

    setPasskeyError(null);
    setPasskeyLoading(true);
    try {
      const success = await loginWithPasskey(username);
      if (success) {
        onClose();
        setUsername('');
        setPassword('');
      }
    } catch (err: unknown) {
      const apiErr = err as { details?: string; message?: string };
      setPasskeyError(apiErr?.details ?? apiErr?.message ?? 'Passkey sign-in failed');
    } finally {
      setPasskeyLoading(false);
    }
  };

  const handleClose = useCallback(() => {
    if (!isLoading && !passkeyLoading) {
      onClose();
      setUsername('');
      setPassword('');
      setPasskeyError(null);
    }
  }, [isLoading, passkeyLoading, onClose]);

  return (
    <AuthSurface
      isOpen={isOpen}
      onClose={handleClose}
      title="Sign In"
      titleIcon={<PrintFarmerLogo size={28} />}
      width="max-w-md"
      isDisabled={isLoading || passkeyLoading}
      showCloseButton={surface === 'page'}
      closeAriaLabel="Close sign in"
      surface={surface}
    >
      <div className="space-y-4">
        {isLoading && <FormSkeleton fields={2} />}
        <form onSubmit={handleSubmit} className="space-y-4" aria-live="polite">
          {error && (
            <div className="rounded-md border border-pf-border bg-pf-bg-2 px-4 py-3 text-sm" style={{ color: 'var(--pf-error)' }}>
              {error}
            </div>
          )}

          <div>
            <label htmlFor="username" className="mb-1 block text-sm font-medium text-pf-text-primary">
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
            <label htmlFor="password" className="mb-1 block text-sm font-medium text-pf-text-primary">
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
                type="button"
                onClick={() => setShowPassword(!showPassword)}
                variant="subtle"
                size="sm"
                disabled={isLoading}
                className="absolute right-3 top-1/2 !h-auto !p-0 -translate-y-1/2"
                aria-label={showPassword ? 'Hide password' : 'Show password'}
              >
                {showPassword ? <EyeOffIcon className="h-4 w-4" /> : <EyeIcon className="h-4 w-4" />}
              </Button>
            </div>
          </div>

          <div className="flex items-center justify-between text-sm">
            <Checkbox
              label="Remember me"
              checked={rememberMe}
              onChange={(e) => setRememberMe(e.target.checked)}
              disabled={isLoading}
            />
            <Link
              to="/forgot-password"
              className="font-medium text-pf-accent hover:text-pf-accent-hover"
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
              disabled={isLoading || passkeyLoading}
            >
              Register
            </Button>
            <Button
              type="submit"
              disabled={isLoading || passkeyLoading || !username || !password}
              variant="primary"
              iconLeft={isLoading ? undefined : <LoginIcon className="h-4 w-4" />}
            >
              {isLoading ? (
                <>
                  <div className="pf-animate-spin h-4 w-4 rounded-full border-b-2 border-white"></div>
                  <span>Signing In...</span>
                </>
              ) : (
                'Sign In'
              )}
            </Button>
          </div>

          <div className="flex items-center gap-3 pt-1" aria-hidden="true">
            <div className="h-px flex-1 bg-pf-border" />
            <span className="text-xs text-pf-text-secondary">or</span>
            <div className="h-px flex-1 bg-pf-border" />
          </div>

          {passkeyError && (
            <div
              role="alert"
              className="rounded-md border border-pf-border bg-pf-bg-2 px-4 py-3 text-sm"
              style={{ color: 'var(--pf-error)' }}
            >
              {passkeyError}
            </div>
          )}

          <Button
            type="button"
            onClick={handlePasskeyLogin}
            disabled={isLoading || passkeyLoading || !username}
            variant="secondary"
            className="w-full"
            aria-label="Sign in with passkey"
            title={!username ? 'Enter your username above to sign in with a passkey' : undefined}
            iconLeft={passkeyLoading ? undefined : <KeyIcon className="h-4 w-4" />}
          >
            {passkeyLoading ? (
              <>
                <div className="pf-animate-spin h-4 w-4 rounded-full border-b-2 border-current"></div>
                <span>Verifying passkey…</span>
              </>
            ) : (
              'Sign in with passkey'
            )}
          </Button>
        </form>
      </div>
    </AuthSurface>
  );
}
