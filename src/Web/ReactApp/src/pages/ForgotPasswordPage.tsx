import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { X, Mail, ArrowLeft } from 'lucide-react';
import { PrintFarmerLogo } from '@/components/PrintFarmerLogo';
import { apiClient } from '@/services/api';

export default function ForgotPasswordPage() {
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
                <Mail className="h-5 w-5 mr-2" />
                Reset Email Sent
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
              If an account exists with that email address, you will receive password reset instructions.
              Please check your email inbox.
            </div>

            <p className="text-pf-text-secondary text-sm">
              The password reset link will expire in 1 hour. If you don't receive an email within a few minutes,
              please check your spam folder.
            </p>

            <button
              onClick={handleClose}
              className="w-full bg-pf-accent text-white py-2 px-4 rounded-md hover:bg-pf-accent-hover font-medium transition-colors flex items-center justify-center gap-2"
              type="button"
            >
              <ArrowLeft className="h-4 w-4" />
              Back to Sign In
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
              <Mail className="h-5 w-5 mr-2" />
              Forgot Password
            </span>
          </div>
          <button
            onClick={handleClose}
            disabled={isLoading}
            className="text-pf-text-tertiary hover:text-pf-text-primary disabled:opacity-50"
            aria-label="Close forgot password"
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
            Enter your email address and we'll send you instructions to reset your password.
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
              autoFocus
            />
          </div>

          <div className="flex gap-3">
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
              disabled={isLoading || !email}
              className="flex-1 bg-pf-accent text-white py-2 px-4 rounded-md hover:bg-pf-accent-hover font-medium transition-colors disabled:opacity-50 flex items-center justify-center gap-2"
            >
              {isLoading ? (
                <>Processing...</>
              ) : (
                <>
                  <Mail className="h-4 w-4" />
                  Send Reset Link
                </>
              )}
            </button>
          </div>

          <div className="text-center pt-2">
            <button
              type="button"
              onClick={handleClose}
              className="text-pf-accent hover:text-pf-accent-hover text-sm font-medium"
              disabled={isLoading}
            >
              Back to Sign In
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
