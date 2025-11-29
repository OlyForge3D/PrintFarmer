import { useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { Button } from '@/components/ui';
import { apiClient } from '../services/api';

export function ConfirmEmailPage() {
  const [searchParams] = useSearchParams();
  const navigate = useNavigate();
  const [status, setStatus] = useState<'confirming' | 'success' | 'error'>('confirming');
  const [message, setMessage] = useState('');

  useEffect(() => {
    const confirmEmail = async () => {
      const token = searchParams.get('token');
      
      if (!token) {
        setStatus('error');
        setMessage('Invalid confirmation link. No token provided.');
        return;
      }

      try {
        const result = await apiClient.confirmEmail(token);
        
        if (result.success) {
          setStatus('success');
          setMessage(result.message || 'Email confirmed successfully!');
        } else {
          setStatus('error');
          setMessage(result.message || 'Email confirmation failed.');
        }
      } catch (error: unknown) {
        setStatus('error');
        const errorMessage = (error as { response?: { data?: { message?: string } }; message?: string })?.response?.data?.message || 
                           (error as { message?: string })?.message || 
                           'An error occurred while confirming your email.';
        setMessage(errorMessage);
      }
    };

    confirmEmail();
  }, [searchParams]);

  return (
    <div className="min-h-screen flex items-center justify-center bg-pf-bg-1 px-4">
      <div className="max-w-md w-full bg-pf-bg-0 border border-pf-border rounded-lg shadow-lg p-8">
        <div className="text-center">
          {status === 'confirming' && (
            <>
              <div className="mx-auto w-16 h-16 mb-4">
                <svg className="animate-spin text-pf-accent" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                  <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                  <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
                </svg>
              </div>
              <h1 className="text-2xl font-bold text-pf-text mb-2">
                Confirming Email
              </h1>
              <p className="text-pf-text-muted">
                Please wait while we confirm your email address...
              </p>
            </>
          )}

          {status === 'success' && (
            <>
              <div className="mx-auto w-16 h-16 mb-4 flex items-center justify-center bg-green-500/10 rounded-full">
                <svg className="w-10 h-10 text-green-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                </svg>
              </div>
              <h1 className="text-2xl font-bold text-pf-text mb-2">
                Email Confirmed!
              </h1>
              <p className="text-pf-text-muted mb-6">
                {message}
              </p>
              <Button
                variant="primary"
                className="w-full"
                onClick={() => navigate('/login')}
              >
                Go to Login
              </Button>
            </>
          )}

          {status === 'error' && (
            <>
              <div className="mx-auto w-16 h-16 mb-4 flex items-center justify-center bg-red-500/10 rounded-full">
                <svg className="w-10 h-10 text-red-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                </svg>
              </div>
              <h1 className="text-2xl font-bold text-pf-text mb-2">
                Confirmation Failed
              </h1>
              <p className="text-pf-text-muted mb-6">
                {message}
              </p>
              <div className="space-y-3">
                <Button
                  variant="primary"
                  className="w-full"
                  onClick={() => navigate('/login')}
                >
                  Go to Login
                </Button>
                <Button
                  variant="secondary"
                  className="w-full"
                  onClick={() => navigate('/')}
                >
                  Go to Home
                </Button>
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
