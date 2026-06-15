import { useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import { Alert, Button, Spinner } from '@/common/components/ui';
import { apiClient } from '@/services/api';

type CallbackState = 'processing' | 'error';

function readApiMessage(error: unknown, fallback: string): string {
  if (!error || typeof error !== 'object') {
    return fallback;
  }

  const details = (error as { details?: string }).details;
  const message = (error as { message?: string }).message;
  return details ?? message ?? fallback;
}

export function PrintablesOAuthCallbackPage() {
  const [searchParams] = useSearchParams();
  const navigate = useNavigate();
  const [state, setState] = useState<CallbackState>('processing');
  const [errorMessage, setErrorMessage] = useState<string>('');

  useEffect(() => {
    const code = searchParams.get('code')?.trim();
    const oauthState = searchParams.get('state')?.trim();

    if (!code || !oauthState) {
      setState('error');
      setErrorMessage('Missing OAuth callback parameters. Retry connecting your Printables account.');
      return;
    }

    const completeCallback = async () => {
      try {
        await apiClient.completePrintablesOAuthCallback(code, oauthState);
        navigate('/settings?tab=profile&sub=preferences', { replace: true });
      } catch (error) {
        setState('error');
        setErrorMessage(readApiMessage(error, 'Failed to complete Printables OAuth callback.'));
      }
    };

    void completeCallback();
  }, [navigate, searchParams]);

  if (state === 'processing') {
    return (
      <div className="flex min-h-screen items-center justify-center bg-pf-bg-1 px-4">
        <div className="w-full max-w-md rounded-lg border border-pf-border bg-pf-bg-0 p-6 text-center">
          <div className="mb-3 flex justify-center">
            <Spinner size="lg" />
          </div>
          <h1 className="text-lg font-semibold text-pf-text-primary">Completing Printables connection</h1>
          <p className="mt-2 text-sm text-pf-text-secondary">
            Please wait while we finalize your account link.
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-pf-bg-1 px-4">
      <div className="w-full max-w-md rounded-lg border border-pf-border bg-pf-bg-0 p-6">
        <Alert variant="error" title="Printables connection failed">
          {errorMessage}
        </Alert>
        <div className="mt-4 flex justify-end">
          <Button type="button" variant="primary" onClick={() => navigate('/settings?tab=profile&sub=preferences')}>
            Back to settings
          </Button>
        </div>
      </div>
    </div>
  );
}
