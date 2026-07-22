import React, { useState, useCallback } from 'react';
import { FormSkeleton } from '@/common/components/skeletons/FormSkeleton';
import { EyeIcon, EyeOffIcon, UserPlusIcon } from '@/common/components/icons/MdiIcons';
import { PrintFarmerLogo } from '@/common/components/PrintFarmerLogo';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { Button, Input } from '@/common/components/ui';
import { AuthSurface, type AuthSurfaceVariant } from '@/features/auth/components/AuthSurface';

interface RegisterModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSwitchToLogin: () => void;
  surface?: AuthSurfaceVariant;
}

function validateRegisterForm(data: {
  username: string;
  email: string;
  password: string;
  confirmPassword: string;
}): string[] {
  const errors: string[] = [];

  if (data.username.trim().length < 3) {
    errors.push('Username must be at least 3 characters long');
  }

  if (!data.email || !/\S+@\S+\.\S+/.test(data.email)) {
    errors.push('Please enter a valid email address');
  }

  if (data.password.length < 6) {
    errors.push('Password must be at least 6 characters long');
  }

  if (data.password !== data.confirmPassword) {
    errors.push('Passwords do not match');
  }

  return errors;
}

export function RegisterModal({
  isOpen,
  onClose,
  onSwitchToLogin,
  surface = 'modal',
}: RegisterModalProps) {
  const [showPassword, setShowPassword] = useState(false);
  const [showConfirmPassword, setShowConfirmPassword] = useState(false);
  const [isLoading, setIsLoading] = useState(false);
  const [formData, setFormData] = useState({
    username: '',
    email: '',
    password: '',
    confirmPassword: '',
    firstName: '',
    lastName: '',
  });

  const { register, error: authError } = useAuth();
  const [clientErrors, setClientErrors] = useState<string[]>([]);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (isLoading) return;

    const errors = validateRegisterForm({
      username: formData.username,
      email: formData.email,
      password: formData.password,
      confirmPassword: formData.confirmPassword,
    });
    setClientErrors(errors);
    if (errors.length > 0) return;

    setIsLoading(true);
    try {
      const result = await register({
        username: formData.username,
        email: formData.email,
        password: formData.password,
        firstName: formData.firstName.trim() ? formData.firstName.trim() : undefined,
        lastName: formData.lastName.trim() ? formData.lastName.trim() : undefined,
      });

      if (result === 'pending') {
        window.location.href = '/registration-pending';
        return;
      }

      if (result) {
        onClose();
        setFormData({
          username: '',
          email: '',
          password: '',
          confirmPassword: '',
          firstName: '',
          lastName: '',
        });
        setClientErrors([]);
      }
    } finally {
      setIsLoading(false);
    }
  };

  const handleClose = useCallback(() => {
    if (!isLoading) {
      onClose();
      setFormData({
        username: '',
        email: '',
        password: '',
        confirmPassword: '',
        firstName: '',
        lastName: '',
      });
      setClientErrors([]);
    }
  }, [isLoading, onClose]);

  const handleInputChange = (field: keyof typeof formData, value: string) => {
    setFormData((prev) => ({ ...prev, [field]: value }));
  };

  const allErrors = [...clientErrors, ...(authError ? [authError] : [])];

  return (
    <AuthSurface
      isOpen={isOpen}
      onClose={handleClose}
      title="Create Account"
      titleIcon={<PrintFarmerLogo size={28} />}
      width="max-w-md"
      isDisabled={isLoading}
      closeButtonVariant={surface === 'page' ? 'subtle' : 'ghost'}
      closeAriaLabel="Close account creation"
      showCloseButton
      surface={surface}
    >
      <div className="space-y-4">
        {isLoading && <FormSkeleton fields={6} />}
        <form onSubmit={handleSubmit} className="space-y-4" aria-live="polite">
          <div className="sr-only" role="status" aria-live="polite">
            {isLoading ? 'Creating account...' : 'Form ready'}
          </div>

          {allErrors.length > 0 && (
            <div className="space-y-1 rounded-md border border-pf-border bg-pf-bg-2 px-4 py-3 text-sm" style={{ color: 'var(--pf-error)' }}>
              {allErrors.map((err, index) => (
                <div key={index}>{err}</div>
              ))}
            </div>
          )}

          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div>
              <label htmlFor="firstName" className="mb-1 block text-sm font-medium text-pf-text-primary">
                First Name
              </label>
              <Input
                type="text"
                id="firstName"
                name="firstName"
                value={formData.firstName}
                onChange={(e) => handleInputChange('firstName', e.target.value)}
                className="bg-pf-bg-0"
                placeholder="Optional"
                autoComplete="given-name"
                disabled={isLoading}
              />
            </div>
            <div>
              <label htmlFor="lastName" className="mb-1 block text-sm font-medium text-pf-text-primary">
                Last Name
              </label>
              <Input
                type="text"
                id="lastName"
                name="lastName"
                value={formData.lastName}
                onChange={(e) => handleInputChange('lastName', e.target.value)}
                className="bg-pf-bg-0"
                placeholder="Optional"
                autoComplete="family-name"
                disabled={isLoading}
              />
            </div>
          </div>

          <div>
            <label htmlFor="username" className="mb-1 block text-sm font-medium text-pf-text-primary">
              Username *
            </label>
            <Input
              type="text"
              id="username"
              name="username"
              value={formData.username}
              onChange={(e) => handleInputChange('username', e.target.value)}
              className="bg-pf-bg-0"
              placeholder="Choose a username"
              required
              autoComplete="username"
              disabled={isLoading}
            />
          </div>

          <div>
            <label htmlFor="email" className="mb-1 block text-sm font-medium text-pf-text-primary">
              Email Address *
            </label>
            <Input
              type="email"
              id="email"
              name="email"
              value={formData.email}
              onChange={(e) => handleInputChange('email', e.target.value)}
              className="bg-pf-bg-0"
              placeholder="Enter your email"
              required
              autoComplete="email"
              disabled={isLoading}
            />
          </div>

          <div>
            <label htmlFor="password" className="mb-1 block text-sm font-medium text-pf-text-primary">
              Password *
            </label>
            <div className="relative">
              <Input
                type={showPassword ? 'text' : 'password'}
                id="password"
                name="password"
                value={formData.password}
                onChange={(e) => handleInputChange('password', e.target.value)}
                className="bg-pf-bg-0 pr-10"
                placeholder="Create a password"
                required
                autoComplete="new-password"
                disabled={isLoading}
              />
              <Button
                onClick={() => setShowPassword(!showPassword)}
                variant="subtle"
                size="sm"
                disabled={isLoading}
                type="button"
                className="absolute right-3 top-1/2 !h-auto !p-0 -translate-y-1/2"
                aria-label={showPassword ? 'Hide password' : 'Show password'}
              >
                {showPassword ? <EyeOffIcon className="h-4 w-4" /> : <EyeIcon className="h-4 w-4" />}
              </Button>
            </div>
          </div>

          <div>
            <label htmlFor="confirmPassword" className="mb-1 block text-sm font-medium text-pf-text-primary">
              Confirm Password *
            </label>
            <div className="relative">
              <Input
                type={showConfirmPassword ? 'text' : 'password'}
                id="confirmPassword"
                name="confirmPassword"
                value={formData.confirmPassword}
                onChange={(e) => handleInputChange('confirmPassword', e.target.value)}
                className="bg-pf-bg-0 pr-10"
                placeholder="Confirm your password"
                required
                autoComplete="new-password"
                disabled={isLoading}
              />
              <Button
                onClick={() => setShowConfirmPassword(!showConfirmPassword)}
                variant="subtle"
                size="sm"
                disabled={isLoading}
                type="button"
                className="absolute right-3 top-1/2 !h-auto !p-0 -translate-y-1/2"
                aria-label={showConfirmPassword ? 'Hide confirm password' : 'Show confirm password'}
              >
                {showConfirmPassword ? <EyeOffIcon className="h-4 w-4" /> : <EyeIcon className="h-4 w-4" />}
              </Button>
            </div>
          </div>

          <div className="space-y-3 pt-4">
            <Button
              type="submit"
              disabled={isLoading || !formData.username || !formData.email || !formData.password || !formData.confirmPassword}
              variant="primary"
              iconLeft={isLoading ? undefined : <UserPlusIcon className="h-4 w-4" />}
              className="w-full justify-center"
            >
              {isLoading ? (
                <>
                  <div className="pf-animate-spin h-4 w-4 rounded-full border-b-2 border-white"></div>
                  <span>Creating Account...</span>
                </>
              ) : (
                'Create Account'
              )}
            </Button>

            <div className="flex items-center justify-center gap-1 text-sm text-pf-text-secondary">
              <span>Have an account?</span>
              <Button
                onClick={onSwitchToLogin}
                variant="link"
                type="button"
                disabled={isLoading}
              >
                Sign in
              </Button>
            </div>
          </div>
        </form>
      </div>
    </AuthSurface>
  );
}
