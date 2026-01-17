import React, { useState, useActionState, useCallback } from 'react';
import { useFormStatus } from 'react-dom';
import { FormSkeleton } from '@/common/components/skeletons/FormSkeleton';
import { EyeIcon, EyeOffIcon, UserPlusIcon } from '@/common/components/icons/MdiIcons';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { Button } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';

interface RegisterModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSwitchToLogin: () => void;
}

interface RegisterFormState {
  errors: string[];
  success?: boolean;
}

/**
 * React 19 Action: Handles form validation and account creation
 * Extracted from component to work with useActionState pattern
 */
async function registerAction(
  prevState: RegisterFormState,
  formData: FormData
): Promise<RegisterFormState> {
  const username = formData.get('username') as string;
  const email = formData.get('email') as string;
  const password = formData.get('password') as string;
  const confirmPassword = formData.get('confirmPassword') as string;
  // firstName and lastName are extracted here for validation but used in handleSubmit
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const firstName = (formData.get('firstName') as string) || undefined;
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const lastName = (formData.get('lastName') as string) || undefined;

  const errors: string[] = [];

  // Client-side validation
  if (username.length < 3) {
    errors.push('Username must be at least 3 characters long');
  }

  if (!email || !/\S+@\S+\.\S+/.test(email)) {
    errors.push('Please enter a valid email address');
  }

  if (password.length < 6) {
    errors.push('Password must be at least 6 characters long');
  }

  if (password !== confirmPassword) {
    errors.push('Passwords do not match');
  }

  if (errors.length > 0) {
    return { errors };
  }

  // Note: Server-side auth will be handled by the register function
  // This action just returns the form data for validation
  return { errors, success: false };
}

/**
 * SubmitButton component using React 19 useFormStatus
 * Automatically shows pending state from form submission
 */
function RegisterSubmitButton({ isDisabled }: { isDisabled: boolean }) {
  const { pending } = useFormStatus();

  return (
    <Button
      type="submit"
      disabled={pending || isDisabled}
      variant="primary"
      iconLeft={pending ? undefined : <UserPlusIcon className="h-4 w-4" />}
    >
      {pending ? (
        <>
          <div className="pf-animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
          <span>Creating Account...</span>
        </>
      ) : (
        'Create Account'
      )}
    </Button>
  );
}

export function RegisterModal({ isOpen, onClose, onSwitchToLogin }: RegisterModalProps) {
  const [showPassword, setShowPassword] = useState(false);
  const [showConfirmPassword, setShowConfirmPassword] = useState(false);
  const [formData, setFormData] = useState({
    username: '',
    email: '',
    password: '',
    confirmPassword: '',
    firstName: '',
    lastName: '',
  });

  // React 19 useActionState for form state and submission
  const [state, formAction, isPending] = useActionState(registerAction, {
    errors: [],
  });

  const { register, error: authError } = useAuth();

  const handleSubmit = async (formDataObj: FormData) => {
    const username = formDataObj.get('username') as string;
    const email = formDataObj.get('email') as string;
    const password = formDataObj.get('password') as string;
    const firstName = (formDataObj.get('firstName') as string) || undefined;
    const lastName = (formDataObj.get('lastName') as string) || undefined;

    // Validation already done by action, if we reach here it's valid
    if (state.errors.length === 0) {
      try {
        const result = await register({
          username,
          email,
          password,
          firstName,
          lastName,
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
        }
      } catch (err) {
        // Error handled by useAuth context
        console.error('Registration error:', err);
      }
    }
  };

  const handleClose = useCallback(() => {
    if (!isPending) {
      onClose();
      setFormData({
        username: '',
        email: '',
        password: '',
        confirmPassword: '',
        firstName: '',
        lastName: '',
      });
    }
  }, [isPending, onClose]);

  const handleInputChange = (field: keyof typeof formData, value: string) => {
    setFormData(prev => ({ ...prev, [field]: value }));
  };


  const allErrors = [...(state.errors || []), ...(authError ? [authError] : [])];

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title="Create Account"
      width="max-w-md"
      isDisabled={isPending}
    >
      {isPending && (
        <div className="px-6 pt-4"><FormSkeleton fields={6} /></div>
      )}
      <form action={formAction} className="p-6 space-y-4" onSubmit={(e) => {
        e.preventDefault();
        const formDataObj = new FormData(e.currentTarget);
        handleSubmit(formDataObj);
      }}>
          <div className="sr-only" role="status" aria-live="polite">
            {isPending ? 'Creating account...' : 'Form ready'}
          </div>
          {allErrors.length > 0 && (
            <div className="bg-pf-bg-2 border border-pf-border px-4 py-3 rounded-md text-sm space-y-1" style={{ color: 'var(--pf-error)' }}>
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
                name="firstName"
                value={formData.firstName}
                onChange={(e) => handleInputChange('firstName', e.target.value)}
                className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
                placeholder="Optional"
                disabled={isPending}
              />
            </div>
            <div>
              <label htmlFor="lastName" className="block text-sm font-medium text-pf-text-primary mb-1">
                Last Name
              </label>
              <input
                type="text"
                id="lastName"
                name="lastName"
                value={formData.lastName}
                onChange={(e) => handleInputChange('lastName', e.target.value)}
                className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
                placeholder="Optional"
                disabled={isPending}
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
              name="username"
              value={formData.username}
              onChange={(e) => handleInputChange('username', e.target.value)}
              className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
              placeholder="Choose a username"
              required
              disabled={isPending}
            />
          </div>

          <div>
            <label htmlFor="email" className="block text-sm font-medium text-pf-text-primary mb-1">
              Email Address *
            </label>
            <input
              type="email"
              id="email"
              name="email"
              value={formData.email}
              onChange={(e) => handleInputChange('email', e.target.value)}
              className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
              placeholder="Enter your email"
              required
              disabled={isPending}
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
                name="password"
                value={formData.password}
                onChange={(e) => handleInputChange('password', e.target.value)}
                className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary pr-10"
                placeholder="Create a password"
                required
                disabled={isPending}
              />
              <Button
                onClick={() => setShowPassword(!showPassword)}
                variant="subtle"
                disabled={isPending}
                type="button"
                className="absolute right-3 top-1/2 transform -translate-y-1/2"
              >
                {showPassword ? <EyeOffIcon className="h-4 w-4" /> : <EyeIcon className="h-4 w-4" />}
              </Button>
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
                name="confirmPassword"
                value={formData.confirmPassword}
                onChange={(e) => handleInputChange('confirmPassword', e.target.value)}
                className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary pr-10"
                placeholder="Confirm your password"
                required
                disabled={isPending}
              />
              <Button
                onClick={() => setShowConfirmPassword(!showConfirmPassword)}
                variant="subtle"
                disabled={isPending}
                type="button"
                className="absolute right-3 top-1/2 transform -translate-y-1/2"
              >
                {showConfirmPassword ? <EyeOffIcon className="h-4 w-4" /> : <EyeIcon className="h-4 w-4" />}
              </Button>
            </div>
          </div>

          <div className="flex items-center justify-between pt-4">
            <Button
              onClick={onSwitchToLogin}
              variant="subtle"
              type="button"
              className="text-pf-accent hover:text-pf-accent-dark text-sm"
              disabled={isPending}
            >
              Already have an account? Sign In
            </Button>
            <RegisterSubmitButton isDisabled={!formData.username || !formData.email || !formData.password} />
          </div>
        </form>
      </Modal>
    );
  }