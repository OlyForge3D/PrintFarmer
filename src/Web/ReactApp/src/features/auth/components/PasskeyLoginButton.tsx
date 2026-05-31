import React, { useState } from 'react';
import { Button } from '@/common/components/ui';
import { KeyIcon } from '@/common/components/icons/MdiIcons';
import { passkeyLogin, isPasskeySupported } from '@/features/auth/services/passkeyService';
import { getPasskeyErrorMessage } from '@/features/auth/types/passkey';
import type { PasskeyError } from '@/features/auth/types/passkey';

interface PasskeyLoginButtonProps {
  usernameHint?: string;
  onSuccess: (token: string) => void;
  onError?: (error: PasskeyError, message: string) => void;
  disabled?: boolean;
}

export function PasskeyLoginButton({ usernameHint, onSuccess, onError, disabled }: PasskeyLoginButtonProps) {
  const [isLoading, setIsLoading] = useState(false);
  const supported = isPasskeySupported();

  if (!supported) {
    return null;
  }

  const handleClick = async () => {
    if (isLoading) return;
    setIsLoading(true);

    try {
      const result = await passkeyLogin(usernameHint);
      if (result.success) {
        onSuccess(result.token);
      } else {
        const message = getPasskeyErrorMessage(result.error);
        onError?.(result.error, message);
      }
    } finally {
      setIsLoading(false);
    }
  };

  return (
    <Button
      type="button"
      variant="secondary"
      onClick={handleClick}
      disabled={disabled || isLoading}
      className="w-full"
      iconLeft={isLoading ? undefined : <KeyIcon className="h-4 w-4" ariaLabel="Passkey" />}
    >
      {isLoading ? (
        <>
          <div className="pf-animate-spin rounded-full h-4 w-4 border-b-2 border-current" />
          <span>Authenticating…</span>
        </>
      ) : (
        'Sign in with passkey'
      )}
    </Button>
  );
}
