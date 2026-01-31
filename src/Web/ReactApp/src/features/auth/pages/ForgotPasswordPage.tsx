import React, { useState } from 'react';
import { useNavigate } from 'react-router';
import { EmailIcon, ArrowLeftIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { PrintFarmerLogo } from '@/common/components/PrintFarmerLogo';
import { Button, Input, FormField  } from '@/common/components/ui';
import { apiClient } from '@/services/api';

export function ForgotPasswordPage() {
  const [email, setEmail] = useState('');
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState(false);
  const navigate = useNavigate();

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (isLoading) return;

    setError(null);
    setIsLoading(true);

    try {
      const response = await apiClient.forgotPassword(email);
      
      if (response.success) {
        setSuccess(true);
      } else {
        setError(response.message || 'Failed to send password reset email');
      }
    } catch {
      // Always show success to prevent email enumeration
      setSuccess(true);
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
                <EmailIcon className="h-5 w-5 mr-2" />
                Reset Email Sent
              </span>
            </div>
            <Button
              type="button"
              onClick={handleClose}
              variant="subtle"
              size="sm"
              className="p-0! h-auto!"
              aria-label="Close"
              title="Close"
            >
              <CloseIcon className="h-5 w-5" />
            </Button>
          </div>

          <div className="p-6 space-y-4">
            <div className="bg-green-50 border border-green-200 text-green-700 px-4 py-3 rounded-md text-sm">
              If an account exists with that email address, you will receive password reset instructions.
              Please check your email inbox.
            </div>

            <p className="text-pf-text-secondary text-sm">
              The password reset link will expire in 1 hour. If you don't receive an email within a few minutes,
              please check your spam folder.
            </p>

            <Button
              type="button"
              onClick={handleClose}
              variant="primary"
              className="w-full justify-center"
              iconLeft={<ArrowLeftIcon className="h-4 w-4" />}
            >
              Back to Sign In
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
              <EmailIcon className="h-5 w-5 mr-2" />
              Forgot Password
            </span>
          </div>
          <Button
            type="button"
            onClick={handleClose}
            disabled={isLoading}
            variant="subtle"
            size="sm"
            className="p-0! h-auto!"
            aria-label="Close forgot password"
            title="Close"
          >
            <CloseIcon className="h-5 w-5" />
          </Button>
        </div>

        <form onSubmit={handleSubmit} className="p-6 space-y-4">
          {error && (
            <div className="bg-pf-bg-2 border border-pf-border px-4 py-3 rounded-md text-sm" style={{ color: 'var(--pf-error)' }}>
              {error}
            </div>
          )}

          <p className="text-pf-text-secondary text-sm">
            Enter your email address and we'll send you instructions to reset your password.
          </p>

            <FormField
              label="Email Address"
              inline={false}
            >
              <Input
                type="email"
                id="email"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                placeholder="your.email@example.com"
                required
                disabled={isLoading}
                autoFocus
              />
            </FormField>

          <div className="flex gap-3">
            <Button
              type="button"
              onClick={handleClose}
              disabled={isLoading}
              variant="secondary"
              className="flex-1"
            >
              Cancel
            </Button>
            <Button
              type="submit"
              disabled={isLoading || !email}
              variant="primary"
              className="flex-1 justify-center"
              iconLeft={!isLoading ? <EmailIcon className="h-4 w-4" /> : undefined}
            >
              {isLoading ? 'Processing' : 'Send Reset Link'}
            </Button>
          </div>

          <div className="text-center pt-2">
            <Button
              type="button"
              onClick={handleClose}
              variant="subtle"
              size="sm"
              disabled={isLoading}
            >
              Back to Sign In
            </Button>
          </div>
        </form>
      </div>
    </div>
  );
}
