import React, { useState } from 'react';
import { FormSkeleton } from '@/components/skeletons/FormSkeleton';
import { X, Eye, EyeOff, UserPlus } from 'lucide-react';
import { PrintFarmerLogo } from '@/components/PrintFarmerLogo';
import { useAuth } from '@/contexts/AuthHooks';

interface RegisterModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSwitchToLogin: () => void;
}

export function RegisterModal({ isOpen, onClose, onSwitchToLogin }: RegisterModalProps) {
  const [formData, setFormData] = useState({
    username: '',
    email: '',
    password: '',
    confirmPassword: '',
    firstName: '',
    lastName: '',
  });
  const [showPassword, setShowPassword] = useState(false);
  const [showConfirmPassword, setShowConfirmPassword] = useState(false);
  const [isLoading, setIsLoading] = useState(false);
  const [validationErrors, setValidationErrors] = useState<string[]>([]);
  const { register, error } = useAuth();

  const validateForm = () => {
    const errors: string[] = [];

    if (formData.username.length < 3) {
      errors.push('Username must be at least 3 characters long');
    }

    if (!formData.email || !/\S+@\S+\.\S+/.test(formData.email)) {
      errors.push('Please enter a valid email address');
    }

    if (formData.password.length < 6) {
      errors.push('Password must be at least 6 characters long');
    }

    if (formData.password !== formData.confirmPassword) {
      errors.push('Passwords do not match');
    }

    setValidationErrors(errors);
    return errors.length === 0;
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (isLoading || !validateForm()) return;

    setIsLoading(true);
    try {
      const result = await register({
        username: formData.username,
        email: formData.email,
        password: formData.password,
        firstName: formData.firstName || undefined,
        lastName: formData.lastName || undefined,
      });
      if (result === 'pending') {
        // Redirect to registration pending page
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
        setValidationErrors([]);
      }
    } finally {
      setIsLoading(false);
    }
  };

  const handleClose = () => {
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
      setValidationErrors([]);
    }
  };

  const handleInputChange = (field: keyof typeof formData, value: string) => {
    setFormData(prev => ({ ...prev, [field]: value }));
    // Clear validation errors when user starts typing
    if (validationErrors.length > 0) {
      setValidationErrors([]);
    }
  };

  if (!isOpen) return null;

  const allErrors = [...validationErrors, ...(error ? [error] : [])];

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black bg-opacity-50">
      <div className="bg-pf-bg-1 rounded-lg shadow-xl max-w-md w-full mx-4 border border-pf-border max-h-[90vh] overflow-y-auto">
        <div className="flex items-center justify-between p-6 border-b border-pf-border">
          <div className="flex items-center gap-2">
            <PrintFarmerLogo size={32} className="mr-2" />
            <span className="text-xl font-bold tracking-tight text-pf-accent">PRINTFARMER</span>
            <span className="text-xl font-semibold text-pf-text-primary flex items-center ml-3">
              <UserPlus className="h-5 w-5 mr-2" />Create Account
            </span>
          </div>
          <button
            onClick={handleClose}
            disabled={isLoading}
            className="text-pf-text-tertiary hover:text-pf-text-primary disabled:opacity-50"
            aria-label="Close registration modal"
            title="Close"
            type="button"
          >
            <X className="h-5 w-5" />
          </button>
        </div>
        {isLoading && (
          <div className="p-6"><FormSkeleton fields={6} /></div>
        )}
  <form onSubmit={handleSubmit} className="p-6 space-y-4">
          <div className="sr-only" role="status" aria-live="polite">
            {isLoading ? 'Creating account...' : 'Form ready'}
          </div>
          {allErrors.length > 0 && (
            <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded-md text-sm space-y-1">
              {allErrors.map((err, index) => (
                <div key={index}>{err}</div>
              ))}
            </div>
          )}

          <div className="grid grid-cols-2 gap-4">
            <div>
              <label htmlFor="firstName" className="block text-sm font-medium text-pf-text-primary mb-1">
                First Name
              </label>
              <input
                type="text"
                id="firstName"
                value={formData.firstName}
                onChange={(e) => handleInputChange('firstName', e.target.value)}
                className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
                placeholder="Optional"
                disabled={isLoading}
              />
            </div>
            <div>
              <label htmlFor="lastName" className="block text-sm font-medium text-pf-text-primary mb-1">
                Last Name
              </label>
              <input
                type="text"
                id="lastName"
                value={formData.lastName}
                onChange={(e) => handleInputChange('lastName', e.target.value)}
                className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
                placeholder="Optional"
                disabled={isLoading}
              />
            </div>
          </div>

          <div>
            <label htmlFor="username" className="block text-sm font-medium text-pf-text-primary mb-1">
              Username *
            </label>
            <input
              type="text"
              id="username"
              value={formData.username}
              onChange={(e) => handleInputChange('username', e.target.value)}
              className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
              placeholder="Choose a username"
              required
              disabled={isLoading}
            />
          </div>

          <div>
            <label htmlFor="email" className="block text-sm font-medium text-pf-text-primary mb-1">
              Email Address *
            </label>
            <input
              type="email"
              id="email"
              value={formData.email}
              onChange={(e) => handleInputChange('email', e.target.value)}
              className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
              placeholder="Enter your email"
              required
              disabled={isLoading}
            />
          </div>

          <div>
            <label htmlFor="password" className="block text-sm font-medium text-pf-text-primary mb-1">
              Password *
            </label>
            <div className="relative">
              <input
                type={showPassword ? 'text' : 'password'}
                id="password"
                value={formData.password}
                onChange={(e) => handleInputChange('password', e.target.value)}
                className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary pr-10"
                placeholder="Create a password"
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

          <div>
            <label htmlFor="confirmPassword" className="block text-sm font-medium text-pf-text-primary mb-1">
              Confirm Password *
            </label>
            <div className="relative">
              <input
                type={showConfirmPassword ? 'text' : 'password'}
                id="confirmPassword"
                value={formData.confirmPassword}
                onChange={(e) => handleInputChange('confirmPassword', e.target.value)}
                className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary pr-10"
                placeholder="Confirm your password"
                required
                disabled={isLoading}
              />
              <button
                type="button"
                onClick={() => setShowConfirmPassword(!showConfirmPassword)}
                className="absolute right-3 top-1/2 transform -translate-y-1/2 text-pf-text-tertiary hover:text-pf-text-primary"
                disabled={isLoading}
              >
                {showConfirmPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              </button>
            </div>
          </div>

          <div className="flex items-center justify-between pt-4">
            <button
              type="button"
              onClick={onSwitchToLogin}
              className="text-pf-accent hover:text-pf-accent-dark text-sm"
              disabled={isLoading}
            >
              Already have an account? Sign In
            </button>
            <button
              type="submit"
              disabled={isLoading || !formData.username || !formData.email || !formData.password}
              className="px-4 py-2 bg-pf-accent text-white rounded-md hover:bg-pf-accent-dark focus:outline-none focus:ring-2 focus:ring-pf-accent disabled:opacity-50 disabled:cursor-not-allowed flex items-center"
            >
              {isLoading ? (
                <>
                  <div className="pf-animate-spin rounded-full h-4 w-4 border-b-2 border-white mr-2"></div>
                  Creating Account...
                </>
              ) : (
                <>
                  <UserPlus className="h-4 w-4 mr-2" />
                  Create Account
                </>
              )}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}