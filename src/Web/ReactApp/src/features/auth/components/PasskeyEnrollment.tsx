import React, { useState } from 'react';
import { Button, Input } from '@/common/components/ui';
import { KeyIcon } from '@/common/components/icons/MdiIcons';
import { passkeyRegister, isPasskeySupported } from '@/features/auth/services/passkeyService';
import { getPasskeyErrorMessage } from '@/features/auth/types/passkey';

interface PasskeyEnrollmentProps {
  onSuccess?: (credentialId: string) => void;
  onError?: (message: string) => void;
}

export function PasskeyEnrollment({ onSuccess, onError }: PasskeyEnrollmentProps) {
  const [deviceName, setDeviceName] = useState('');
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState(false);
  const supported = isPasskeySupported();

  if (!supported) {
    return (
      <div className="text-sm text-pf-text-secondary">
        Passkeys are not supported in this browser. Ensure you are using HTTPS.
      </div>
    );
  }

  const handleEnroll = async () => {
    if (isLoading) return;
    setIsLoading(true);
    setError(null);
    setSuccess(false);

    try {
      const result = await passkeyRegister(deviceName.trim() || undefined);
      if (result.success) {
        setSuccess(true);
        setDeviceName('');
        onSuccess?.(result.credentialId);
      } else {
        const message = getPasskeyErrorMessage(result.error);
        setError(message);
        onError?.(message);
      }
    } finally {
      setIsLoading(false);
    }
  };

  return (
    <div className="space-y-3">
      <div>
        <label htmlFor="passkey-device-name" className="block text-sm font-medium text-pf-text-primary mb-1">
          Device name (optional)
        </label>
        <Input
          type="text"
          id="passkey-device-name"
          value={deviceName}
          onChange={(e) => setDeviceName(e.target.value)}
          placeholder="e.g. MacBook Touch ID, YubiKey"
          disabled={isLoading}
          className="w-full"
        />
      </div>

      {error && (
        <div className="bg-pf-bg-2 border border-pf-border px-4 py-3 rounded-md text-sm" style={{ color: 'var(--pf-error)' }}>
          {error}
        </div>
      )}

      {success && (
        <div className="bg-pf-bg-2 border border-pf-border px-4 py-3 rounded-md text-sm text-pf-text-primary">
          Passkey registered successfully!
        </div>
      )}

      <Button
        type="button"
        variant="primary"
        onClick={handleEnroll}
        disabled={isLoading}
        iconLeft={isLoading ? undefined : <KeyIcon className="h-4 w-4" ariaLabel="Register passkey" />}
      >
        {isLoading ? (
          <>
            <div className="pf-animate-spin rounded-full h-4 w-4 border-b-2 border-white" />
            <span>Registering…</span>
          </>
        ) : (
          'Register passkey'
        )}
      </Button>
    </div>
  );
}
