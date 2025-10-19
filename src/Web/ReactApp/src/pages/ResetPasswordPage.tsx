import React, { useState, useEffect } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { X, Eye, EyeOff, Key, CheckCircle } from 'lucide-react';
import { PrintFarmerLogo } from '@/components/PrintFarmerLogo';
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
            <button
              onClick={handleClose}
              className="text-pf-text-tertiary hover:text-pf-text-primary"
              aria-label="Close"
              title="Close"
              type="button"
            >
              <X className="h-5 w-5" />
            </button>
          </div>

          <div className="p-6 space-y-4">
            <div className="bg-green-50 border border-green-200 text-green-700 px-4 py-3 rounded-md text-sm">
              Your password has been reset successfully! You can now sign in with your new password.
            </div>

            <button
              onClick={handleClose}
              className="w-full bg-pf-accent text-white py-2 px-4 rounded-md hover:bg-pf-accent-hover font-medium transition-colors"
              type="button"
            >
              Continue to Sign In
            </button>
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
          <button
            onClick={handleClose}
            disabled={isLoading}
            className="text-pf-text-tertiary hover:text-pf-text-primary disabled:opacity-50"
            aria-label="Close reset password"
            title="Close"
            type="button"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="p-6 space-y-4">
          {error && (
            <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded-md text-sm">
              {error}
            </div>
          )}

          <p className="text-pf-text-secondary text-sm">
            Enter your new password below. Make sure it's at least 8 characters long.
          </p>

          <div>
            <label htmlFor="email" className="block text-sm font-medium text-pf-text-primary mb-1">
              Email Address
            </label>
            <input
              type="email"
              id="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
              placeholder="your.email@example.com"
              required
              disabled={isLoading}
            />
          </div>

          <div>
            <label htmlFor="newPassword" className="block text-sm font-medium text-pf-text-primary mb-1">
              New Password
            </label>
            <div className="relative">
              <input
                type={showNewPassword ? 'text' : 'password'}
                id="newPassword"
                value={newPassword}
                onChange={(e) => setNewPassword(e.target.value)}
                className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary pr-10"
                placeholder="Enter new password"
                required
                disabled={isLoading}
                minLength={8}
              />
              <button
                type="button"
                onClick={() => setShowNewPassword(!showNewPassword)}
                className="absolute right-3 top-1/2 -translate-y-1/2 text-pf-text-tertiary hover:text-pf-text-primary"
                aria-label={showNewPassword ? 'Hide password' : 'Show password'}
                disabled={isLoading}
              >
                {showNewPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              </button>
            </div>
            <p className="text-xs text-pf-text-tertiary mt-1">Minimum 8 characters</p>
          </div>

          <div>
            <label htmlFor="confirmPassword" className="block text-sm font-medium text-pf-text-primary mb-1">
              Confirm New Password
            </label>
            <div className="relative">
              <input
                type={showConfirmPassword ? 'text' : 'password'}
                id="confirmPassword"
                value={confirmPassword}
                onChange={(e) => setConfirmPassword(e.target.value)}
                className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary pr-10"
                placeholder="Confirm new password"
                required
                disabled={isLoading}
                minLength={8}
              />
              <button
                type="button"
                onClick={() => setShowConfirmPassword(!showConfirmPassword)}
                className="absolute right-3 top-1/2 -translate-y-1/2 text-pf-text-tertiary hover:text-pf-text-primary"
                aria-label={showConfirmPassword ? 'Hide password' : 'Show password'}
                disabled={isLoading}
              >
                {showConfirmPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              </button>
            </div>
          </div>

          <div className="flex gap-3 pt-2">
            <button
              type="button"
              onClick={handleClose}
              disabled={isLoading}
              className="flex-1 border border-pf-border text-pf-text-primary py-2 px-4 rounded-md hover:bg-pf-bg-2 font-medium transition-colors disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={isLoading || !token || !email || !newPassword || !confirmPassword}
              className="flex-1 bg-pf-accent text-white py-2 px-4 rounded-md hover:bg-pf-accent-hover font-medium transition-colors disabled:opacity-50 flex items-center justify-center gap-2"
            >
              {isLoading ? (
                <>Resetting...</>
              ) : (
                <>
                  <Key className="h-4 w-4" />
                  Reset Password
                </>
              )}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
