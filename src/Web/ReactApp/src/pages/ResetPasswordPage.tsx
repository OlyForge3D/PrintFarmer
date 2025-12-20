import React, { useState, useEffect } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { CloseIcon } from '@/components/icons/MdiIcons';
import { Eye, EyeOff, Key, CheckCircle } from 'lucide-react';
import { PrintFarmerLogo } from '@/components/PrintFarmerLogo';
import { Button, Input, FormField, Alert } from '@/components/ui';
import { apiClient } from '@/services/api';

export default function ResetPasswordPage() {
  const [searchParams] = useSearchParams();
  const [token, setToken] = useState('');
  const [email, setEmail] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [showNewPassword, setShowNewPassword] = useState(false);
  const [showConfirmPassword, setShowConfirmPassword] = useState(false);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState(false);
  const navigate = useNavigate();

  useEffect(() => {
    const tokenParam = searchParams.get('token');
    const emailParam = searchParams.get('email');
    
    if (tokenParam) setToken(tokenParam);
    if (emailParam) setEmail(emailParam);
    
    if (!tokenParam) {
      setError('Invalid or missing reset token. Please request a new password reset link.');
    }
  }, [searchParams]);

  const validatePassword = (password: string): string | null => {
    if (password.length < 8) {
      return 'Password must be at least 8 characters long';
    }
    return null;
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (isLoading) return;

    setError(null);

    // Validate passwords
    const passwordError = validatePassword(newPassword);
    if (passwordError) {
      setError(passwordError);
      return;
    }

    if (newPassword !== confirmPassword) {
      setError('Passwords do not match');
      return;
    }

    if (!token) {
      setError('Invalid reset token. Please request a new password reset link.');
      return;
    }

    setIsLoading(true);

    try {
      const response = await apiClient.resetPassword(token, email, newPassword, confirmPassword);
      
      if (response.success) {
        setSuccess(true);
      } else {
        setError(response.message || 'Failed to reset password. The link may have expired.');
      }
    } catch (err: unknown) {
      if (err && typeof err === 'object' && 'response' in err) {
        const axiosError = err as { response?: { data?: { message?: string } } };
        setError(axiosError.response?.data?.message || 'Failed to reset password. The link may have expired.');
      } else {
        setError('Failed to reset password. The link may have expired.');
      }
    } finally {
      setIsLoading(false);
    }
  };

  const handleClose = () => {
    if (!isLoading) {
      navigate('/login');
    }
  };

  if (success) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-pf-bg-0">
        <div className="bg-pf-bg-1 rounded-lg shadow-xl max-w-md w-full mx-4 border border-pf-border">
          <div className="flex items-center justify-between p-6 border-b border-pf-border">
            <div className="flex items-center gap-2">
              <PrintFarmerLogo size={32} className="mr-2" />
              <span className="text-xl font-bold tracking-tight text-pf-accent">PRINTFARMER</span>
              <span className="text-xl font-semibold text-pf-text-primary flex items-center ml-3">
                <CheckCircle className="h-5 w-5 mr-2" />
                Password Reset
              </span>
            </div>
            <Button
              variant="subtle"
              onClick={handleClose}
              disabled={isLoading}
              className="p-0"
              aria-label="Close"
              title="Close"
            >
              <X className="h-5 w-5" />
            </Button>
          </div>

          <div className="p-6 space-y-4">
            <Alert type="success" title="Password Reset Successful">
              You can now sign in with your new password.
            </Alert>

            <Button
              variant="primary"
              onClick={handleClose}
              className="w-full"
            >
              Continue to Sign In
            </Button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-pf-bg-0">
      <div className="bg-pf-bg-1 rounded-lg shadow-xl max-w-md w-full mx-4 border border-pf-border">
        <div className="flex items-center justify-between p-6 border-b border-pf-border">
          <div className="flex items-center gap-2">
            <PrintFarmerLogo size={32} className="mr-2" />
            <span className="text-xl font-bold tracking-tight text-pf-accent">PRINTFARMER</span>
            <span className="text-xl font-semibold text-pf-text-primary flex items-center ml-3">
              <Key className="h-5 w-5 mr-2" />
              Reset Password
            </span>
          </div>
          <Button
            type="button"
            onClick={handleClose}
            disabled={isLoading}
            variant="subtle"
            size="sm"
            className="!p-0 !h-auto"
            aria-label="Close reset password"
            title="Close"
          >
            <X className="h-5 w-5" />
          </Button>
        </div>

        <form onSubmit={handleSubmit} className="p-6 space-y-4">
          {error && (
            <Alert type="error" title="Error">
              {error}
            </Alert>
          )}

          <p className="text-pf-text-secondary text-sm">
            Enter your new password below. Make sure it's at least 8 characters long.
          </p>

          <FormField label="Email Address">
            <Input
              type="email"
              id="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="your.email@example.com"
              required
              disabled={isLoading}
            />
          </FormField>

          <FormField 
            label="New Password"
            helper="Minimum 8 characters"
            error={newPassword && newPassword.length < 8 ? 'Password must be at least 8 characters' : undefined}
          >
            <div className="relative">
              <Input
                type={showNewPassword ? 'text' : 'password'}
                id="newPassword"
                value={newPassword}
                onChange={(e) => setNewPassword(e.target.value)}
                placeholder="Enter new password"
                required
                disabled={isLoading}
                minLength={8}
              />
              <Button
                type="button"
                onClick={() => setShowNewPassword(!showNewPassword)}
                variant="subtle"
                size="sm"
                className="absolute right-3 top-1/2 -translate-y-1/2 !p-0 !h-auto"
                aria-label={showNewPassword ? 'Hide password' : 'Show password'}
                disabled={isLoading}
              >
                {showNewPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              </Button>
            </div>
          </FormField>

          <FormField label="Confirm New Password">
            <div className="relative">
              <Input
                type={showConfirmPassword ? 'text' : 'password'}
                id="confirmPassword"
                value={confirmPassword}
                onChange={(e) => setConfirmPassword(e.target.value)}
                placeholder="Confirm new password"
                required
                disabled={isLoading}
                minLength={8}
              />
              <Button
                type="button"
                onClick={() => setShowConfirmPassword(!showConfirmPassword)}
                variant="subtle"
                size="sm"
                className="absolute right-3 top-1/2 -translate-y-1/2 !p-0 !h-auto"
                aria-label={showConfirmPassword ? 'Hide password' : 'Show password'}
                disabled={isLoading}
              >
                {showConfirmPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              </Button>
            </div>
          </FormField>

          <div className="flex gap-3 pt-2">
            <Button
              type="button"
              variant="secondary"
              onClick={handleClose}
              disabled={isLoading}
              className="flex-1"
            >
              Cancel
            </Button>
            <Button
              type="submit"
              variant="primary"
              disabled={isLoading || !token || !email || !newPassword || !confirmPassword}
              className="flex-1"
            >
              {isLoading ? (
                <>Resetting...</>
              ) : (
                <>
                  <Key className="h-4 w-4 mr-2" />
                  Reset Password
                </>
              )}
            </Button>
          </div>
        </form>
      </div>
    </div>
  );
}
