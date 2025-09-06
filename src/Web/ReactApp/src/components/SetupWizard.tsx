import React, { useState, useEffect } from 'react';
import { Shield, User, Mail, Lock, Eye, EyeOff, CheckCircle } from 'lucide-react';
import { useAuth } from '@/contexts/AuthContext';

interface SetupWizardProps {
  onComplete: () => void;
}

export function SetupWizard({ onComplete }: SetupWizardProps) {
  const [loading, setLoading] = useState(true);
  const [needsSetup, setNeedsSetup] = useState(false);
  const [creating, setCreating] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const { login } = useAuth();
  
  const [formData, setFormData] = useState({
    username: '',
    email: '',
    password: '',
    confirmPassword: '',
    firstName: '',
    lastName: '',
  });

  const [validationErrors, setValidationErrors] = useState<string[]>([]);

  useEffect(() => {
    checkSetupStatus();
  }, []);

  const checkSetupStatus = async () => {
    try {
      const response = await fetch('/api/setup/status');
      if (response.ok) {
        const data = await response.json();
        setNeedsSetup(data.needsSetup);
      } else {
        setError('Failed to check setup status');
      }
    } catch (err) {
      setError('Error checking setup status');
      console.error('Setup status check error:', err);
    } finally {
      setLoading(false);
    }
  };

  const validateForm = () => {
    const errors: string[] = [];

    if (formData.username.length < 3) {
      errors.push('Username must be at least 3 characters long');
    }

    if (!formData.email || !/\S+@\S+\.\S+/.test(formData.email)) {
      errors.push('Please enter a valid email address');
    }

    if (formData.password.length < 8) {
      errors.push('Password must be at least 8 characters long for admin accounts');
    }

    if (formData.password !== formData.confirmPassword) {
      errors.push('Passwords do not match');
    }

    if (!formData.firstName.trim()) {
      errors.push('First name is required');
    }

    if (!formData.lastName.trim()) {
      errors.push('Last name is required');
    }

    setValidationErrors(errors);
    return errors.length === 0;
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (creating || !validateForm()) return;

    setCreating(true);
    setError(null);

    try {
      const response = await fetch('/api/setup/initial-admin', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          username: formData.username,
          email: formData.email,
          password: formData.password,
          firstName: formData.firstName,
          lastName: formData.lastName,
        }),
      });

      if (response.ok) {
        const result = await response.json();
        if (result.success && result.token) {
          // Store the token and update auth state
          localStorage.setItem('auth-token', result.token);
          
          // Update auth context by logging in with the new admin
          await login({ username: formData.username, password: formData.password });
          
          onComplete();
        } else {
          setError(result.error || 'Failed to create admin user');
        }
      } else {
        const errorData = await response.text();
        setError(errorData || 'Failed to create admin user');
      }
    } catch (err) {
      setError('Error creating admin user');
      console.error('Admin creation error:', err);
    } finally {
      setCreating(false);
    }
  };

  const handleInputChange = (field: keyof typeof formData, value: string) => {
    setFormData(prev => ({ ...prev, [field]: value }));
    // Clear validation errors when user starts typing
    if (validationErrors.length > 0) {
      setValidationErrors([]);
    }
    if (error) {
      setError(null);
    }
  };

  if (loading) {
    return (
      <div className="min-h-screen bg-pf-bg-0 flex items-center justify-center">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent"></div>
      </div>
    );
  }

  if (!needsSetup) {
    // Setup not needed, complete immediately
    useEffect(() => onComplete(), [onComplete]);
    return null;
  }

  return (
    <div className="min-h-screen bg-pf-bg-0 flex items-center justify-center p-4">
      <div className="max-w-md w-full bg-pf-bg-1 border border-pf-border shadow-xl rounded-xl p-8">
        {/* Header */}
        <div className="text-center mb-8">
          <div className="flex items-center justify-center w-16 h-16 bg-pf-accent bg-opacity-15 rounded-full mx-auto mb-4">
            <Shield className="h-8 w-8 text-pf-accent" />
          </div>
          <h1 className="text-2xl font-bold text-pf-text-primary mb-2">
            Welcome to PrintFarmer
          </h1>
          <p className="text-pf-text-secondary">
            Let's set up your administrator account to get started.
          </p>
        </div>

        {/* Setup Form */}
        <form onSubmit={handleSubmit} className="space-y-6">
          {/* Error Display */}
          {(error || validationErrors.length > 0) && (
            <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded-md text-sm space-y-1">
              {error && <div>{error}</div>}
              {validationErrors.map((err, index) => (
                <div key={index}>{err}</div>
              ))}
            </div>
          )}

          {/* Name Fields */}
          <div className="grid grid-cols-2 gap-4">
            <div>
              <label htmlFor="firstName" className="block text-sm font-medium text-pf-text-primary mb-2">
                <User className="inline h-4 w-4 mr-1" />
                First Name *
              </label>
              <input
                type="text"
                id="firstName"
                value={formData.firstName}
                onChange={(e) => handleInputChange('firstName', e.target.value)}
                className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
                placeholder="Your first name"
                required
                disabled={creating}
              />
            </div>
            <div>
              <label htmlFor="lastName" className="block text-sm font-medium text-pf-text-primary mb-2">
                Last Name *
              </label>
              <input
                type="text"
                id="lastName"
                value={formData.lastName}
                onChange={(e) => handleInputChange('lastName', e.target.value)}
                className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
                placeholder="Your last name"
                required
                disabled={creating}
              />
            </div>
          </div>

          {/* Username */}
          <div>
            <label htmlFor="username" className="block text-sm font-medium text-pf-text-primary mb-2">
              <User className="inline h-4 w-4 mr-1" />
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
              disabled={creating}
            />
          </div>

          {/* Email */}
          <div>
            <label htmlFor="email" className="block text-sm font-medium text-pf-text-primary mb-2">
              <Mail className="inline h-4 w-4 mr-1" />
              Email Address *
            </label>
            <input
              type="email"
              id="email"
              value={formData.email}
              onChange={(e) => handleInputChange('email', e.target.value)}
              className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
              placeholder="your.email@example.com"
              required
              disabled={creating}
            />
          </div>

          {/* Password */}
          <div>
            <label htmlFor="password" className="block text-sm font-medium text-pf-text-primary mb-2">
              <Lock className="inline h-4 w-4 mr-1" />
              Password *
            </label>
            <div className="relative">
              <input
                type={showPassword ? 'text' : 'password'}
                id="password"
                value={formData.password}
                onChange={(e) => handleInputChange('password', e.target.value)}
                className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary pr-10"
                placeholder="Create a secure password"
                required
                disabled={creating}
              />
              <button
                type="button"
                onClick={() => setShowPassword(!showPassword)}
                className="absolute right-3 top-1/2 transform -translate-y-1/2 text-pf-text-tertiary hover:text-pf-text-primary"
                disabled={creating}
              >
                {showPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              </button>
            </div>
            <p className="text-xs text-pf-text-tertiary mt-1">
              Must be at least 8 characters long
            </p>
          </div>

          {/* Confirm Password */}
          <div>
            <label htmlFor="confirmPassword" className="block text-sm font-medium text-pf-text-primary mb-2">
              <Lock className="inline h-4 w-4 mr-1" />
              Confirm Password *
            </label>
            <input
              type="password"
              id="confirmPassword"
              value={formData.confirmPassword}
              onChange={(e) => handleInputChange('confirmPassword', e.target.value)}
              className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
              placeholder="Confirm your password"
              required
              disabled={creating}
            />
          </div>

          {/* Submit Button */}
          <button
            type="submit"
            disabled={creating || !formData.username || !formData.email || !formData.password || !formData.firstName || !formData.lastName}
            className="w-full px-4 py-3 bg-pf-accent text-white rounded-md hover:bg-pf-accent-dark focus:outline-none focus:ring-2 focus:ring-pf-accent disabled:opacity-50 disabled:cursor-not-allowed flex items-center justify-center"
          >
            {creating ? (
              <>
                <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white mr-2"></div>
                Creating Administrator...
              </>
            ) : (
              <>
                <CheckCircle className="h-4 w-4 mr-2" />
                Create Administrator Account
              </>
            )}
          </button>
        </form>

        {/* Footer */}
        <div className="mt-6 text-center text-xs text-pf-text-tertiary">
          This administrator account will have full access to manage printers, users, and system settings.
        </div>
      </div>
    </div>
  );
}